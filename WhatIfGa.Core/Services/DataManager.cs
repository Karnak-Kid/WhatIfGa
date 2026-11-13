using System.Text.Json;
using WhatIfGa.Core.Models;

namespace WhatIfGa.Core.Services;

/// <summary>Validates local CSV/XLSX history and stores a bounded, versioned JSON snapshot.</summary>
public sealed class DataManager
{
    private readonly SemaphoreSlim _storageLock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public string DatabasePath { get; }

    public DataManager(string? storageDirectory = null)
    {
        storageDirectory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WhatIfGa");
        DatabasePath = Path.Combine(Path.GetFullPath(storageDirectory), "usage-data.json");
    }

    /// <summary>Reconstructs coverage notices for valid history restored from local storage.</summary>
    public static IReadOnlyList<string> GetCoverageWarnings(IReadOnlyList<UsageData> data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Count == 0) return Array.Empty<string>();
        var errors = UsageValidation.GetErrors(data);
        if (errors.Count > 0) throw new ArgumentException("Coverage requires valid usage data: " + string.Join(" ", errors), nameof(data));
        return UsageTableValidator.GetCoverageWarnings(data.OrderBy(interval => interval.Timestamp).ToArray());
    }

    /// <summary>Dispatches by supported local file extension without changing existing history.</summary>
    public CsvValidationResult ValidateFile(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".csv" => ValidateCsv(path, cancellationToken),
                ".xlsx" => ValidateXlsx(path, cancellationToken),
                _ => Invalid("Unsupported file type. Choose a .csv or .xlsx usage download.")
            };
        }
        catch (ArgumentException) { return Invalid("The selected file path is invalid."); }
    }

    /// <summary>Reads CSV without changing local history; preserves the original physical line numbers.</summary>
    public CsvValidationResult ValidateCsv(string path, CancellationToken cancellationToken = default)
        => ValidateTable(path, CsvUsageReader.Read, isWorkbook: false, cancellationToken);

    /// <summary>Reads XLSX directly without Excel, macros, formula evaluation, or network access.</summary>
    public CsvValidationResult ValidateXlsx(string path, CancellationToken cancellationToken = default)
        => ValidateTable(path, XlsxUsageReader.Read, isWorkbook: true, cancellationToken);

    /// <summary>Validates off the UI thread, then atomically saves valid supported history.</summary>
    public async Task<CsvValidationResult> ImportFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var result = await Task.Run(() => ValidateFile(path, cancellationToken), cancellationToken).ConfigureAwait(false);
        if (result.IsValid) await SaveAsync(result.Data, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>Compatibility entry point for callers explicitly importing a CSV.</summary>
    public async Task<CsvValidationResult> ImportCsvAsync(string path, CancellationToken cancellationToken = default)
    {
        var result = await Task.Run(() => ValidateCsv(path, cancellationToken), cancellationToken).ConfigureAwait(false);
        if (result.IsValid) await SaveAsync(result.Data, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static CsvValidationResult ValidateTable(string path,
        Func<string, CancellationToken, IReadOnlyList<UsageImportRow>> reader, bool isWorkbook,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var rows = reader(path, cancellationToken);
            return UsageTableValidator.Validate(rows, isWorkbook ? "Row" : "Line", isWorkbook, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Xml.XmlException)
        {
            return Invalid($"Could not read the {(isWorkbook ? "XLSX workbook" : "CSV")}: {exception.Message}");
        }
    }

    public async Task SaveAsync(IReadOnlyList<UsageData> data, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = data.OrderBy(interval => interval.Timestamp).ToArray();
        var errors = UsageValidation.GetErrors(snapshot);
        if (errors.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        await _storageLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporaryPath = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
            temporaryPath = DatabasePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, new StorageEnvelope { SchemaVersion = 1, Records = snapshot }, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (stream.Length > UsageValidation.MaximumFileBytes) throw new InvalidDataException("Stored usage exceeds the 10 MiB size limit.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, DatabasePath, overwrite: true);
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
                catch (IOException) { /* The valid snapshot remains intact if temp cleanup is denied. */ }
                catch (UnauthorizedAccessException) { }
            }
            _storageLock.Release();
        }
    }

    public async Task<IReadOnlyList<UsageData>> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _storageLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(DatabasePath)) return Array.Empty<UsageData>();
            await using var stream = new FileStream(DatabasePath, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous);
            if (stream.Length > UsageValidation.MaximumFileBytes) throw new InvalidDataException("Stored usage exceeds the 10 MiB size limit. Import a valid CSV or XLSX file to replace it.");
            StorageEnvelope? envelope;
            try { envelope = await JsonSerializer.DeserializeAsync<StorageEnvelope>(stream, JsonOptions, cancellationToken).ConfigureAwait(false); }
            catch (JsonException exception) { throw new InvalidDataException("Saved usage is not valid JSON. Import a valid CSV or XLSX file to replace it.", exception); }
            if (envelope is null || envelope.SchemaVersion != 1 || envelope.Records is null)
                throw new InvalidDataException("The saved usage format is unsupported or incomplete. Import a valid CSV or XLSX file to replace it.");
            var errors = UsageValidation.GetErrors(envelope.Records);
            if (errors.Count > 0) throw new InvalidDataException("Saved usage failed validation: " + string.Join(" ", errors));
            return Array.AsReadOnly(envelope.Records.OrderBy(interval => interval.Timestamp).ToArray());
        }
        finally { _storageLock.Release(); }
    }

    private static CsvValidationResult Invalid(string error)
        => new(false, new[] { error }, Array.Empty<string>(), Array.Empty<UsageData>());

    public sealed class StorageEnvelope
    {
        public int SchemaVersion { get; set; }
        public UsageData[]? Records { get; set; }
    }
}
