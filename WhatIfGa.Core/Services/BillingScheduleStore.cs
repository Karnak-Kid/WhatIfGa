using System.Text.Json;
using WhatIfGa.Core.Models;

namespace WhatIfGa.Core.Services;

public sealed class BillingScheduleStore(string directory)
{
    private const int MaximumBytes = 64 * 1024;
    private string FilePath => Path.Combine(directory, "billing-schedule.json");

    public async Task<BillingSchedule> LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(FilePath)) return BillingSchedule.Default;

        await using var stream = File.OpenRead(FilePath);
        if (stream.Length > MaximumBytes)
            throw new InvalidDataException("The saved billing schedule exceeds its size limit.");

        try
        {
            var snapshot = await JsonSerializer.DeserializeAsync<Snapshot>(stream, cancellationToken: cancellationToken);
            if (snapshot is not { Version: 1, Schedule: not null })
                throw new InvalidDataException("The saved billing schedule format is unsupported or incomplete.");
            return BillingCalendar.Validate(snapshot.Schedule);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("The saved billing schedule could not be read.", error);
        }
    }

    public async Task SaveAsync(BillingSchedule schedule, CancellationToken cancellationToken)
    {
        var snapshot = new Snapshot(1, BillingCalendar.Validate(schedule));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot);
        if (bytes.Length > MaximumBytes)
            throw new InvalidDataException("The billing schedule exceeds its size limit.");

        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);
        var temporaryPath = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, bytes, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, FilePath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed record Snapshot(int Version, BillingSchedule? Schedule);
}
