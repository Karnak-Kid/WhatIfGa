using System.Globalization;
using WhatIfGa.Core.Models;

namespace WhatIfGa.Core.Services;

/// <summary>Shared CSV/XLSX schema and value validation; only usage records leave this layer.</summary>
internal static class UsageTableValidator
{
    private const int MaximumReportedErrors = 100;
    private const int MaximumHeaderRow = 32;
    private static readonly string[] TimestampFormats =
    [
        "yyyy-MM-dd HH:mm", "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", "M/d/yyyy H:mm", "M/d/yyyy H:mm:ss", "M/d/yyyy h:mm tt", "M/d/yyyy h:mm:ss tt"
    ];

    public static CsvValidationResult Validate(IReadOnlyList<UsageImportRow> rows, string location, bool isWorkbook,
        CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var data = new List<UsageData>();
        Header? header = null;
        var timestamps = new HashSet<DateTime>();
        var wasSorted = true;
        DateTime? previousTimestamp = null;
        var records = 0;
        var skippedMetadata = false;

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (row.Fields.All(string.IsNullOrWhiteSpace)) continue;
            if (header is null)
            {
                if (row.Number > MaximumHeaderRow)
                    return Invalid($"{location} {row.Number}: the usage header must appear within the first {MaximumHeaderRow} rows.");
                if (IsMetadataRow(row.Fields)) { skippedMetadata = true; continue; }
                header = ReadHeader(row.Fields, out var headerError);
                if (header is null) return Invalid($"{location} {row.Number}: {headerError}");
                continue;
            }

            records++;
            if (records > UsageValidation.MaximumRecords)
            {
                AddError($"at most {UsageValidation.MaximumRecords:N0} hourly records are supported.");
                break;
            }
            var fields = row.Fields;
            var extraData = fields.Count > header.Width && fields.Skip(header.Width).Any(value => !string.IsNullOrWhiteSpace(value));
            if (extraData || (!isWorkbook && fields.Count != header.Width) || fields.Count <= header.LastRequiredColumn)
            { AddError($"expected {header.Width} fields, found {fields.Count}."); continue; }

            DateTime timestamp;
            if (header.TimestampColumn >= 0)
            {
                if (!DateTime.TryParseExact(fields[header.TimestampColumn].Trim(), TimestampFormats, CultureInfo.InvariantCulture,
                        DateTimeStyles.AllowWhiteSpaces, out timestamp))
                { AddError("timestamp is invalid; use yyyy-MM-dd HH:mm in local time without a time-zone suffix."); continue; }
            }
            else
            {
                if (!TryInteger(fields[header.YearColumn], out var year) || !TryInteger(fields[header.MonthColumn], out var month) ||
                    !TryInteger(fields[header.DayColumn], out var day) || !TryInteger(fields[header.HourColumn], out var hour) || hour is < 0 or > 23)
                { AddError("Year, Month, Day and Hour must be valid integers; Hour is 0–23."); continue; }
                try { timestamp = new DateTime(year, month, day, hour, 0, 0); }
                catch (ArgumentOutOfRangeException) { AddError("the date is invalid."); continue; }
            }
            if (timestamp.Ticks % TimeSpan.TicksPerHour != 0)
            { AddError("timestamps must begin on a whole hour; subhourly or daily data is not supported."); continue; }
            if (!TryDecimal(fields[header.KWhColumn], out var kwh) || kwh is < 0 or > UsageValidation.MaximumHourlyKWh)
            { AddError("kWh must be a finite number between 0 and 100,000 (decimal point, no thousands separators)."); continue; }
            decimal? temp = null;
            if (header.TempColumn >= 0 && header.TempColumn < fields.Count && !string.IsNullOrWhiteSpace(fields[header.TempColumn]))
            {
                if (!TryDecimal(fields[header.TempColumn], out var value))
                { AddError("Temp must be a finite number or blank."); continue; }
                temp = value;
            }
            if (!timestamps.Add(timestamp))
            { AddError($"duplicate hourly timestamp {timestamp:yyyy-MM-dd HH:mm}. Use unique local hours; normalize repeated DST hours before importing."); continue; }
            if (previousTimestamp > timestamp) wasSorted = false;
            previousTimestamp = timestamp;
            data.Add(new UsageData(timestamp, kwh, temp));
            if (errors.Count >= MaximumReportedErrors) break;

            void AddError(string message)
            {
                if (errors.Count < MaximumReportedErrors) errors.Add($"{location} {row.Number}: {message}");
            }
        }

        if (header is null) return Invalid("The file is empty or its usage header is missing.");
        if (errors.Count >= MaximumReportedErrors)
            errors.Add("Further errors were not reported; fix these rows and import again.");
        errors.AddRange(UsageValidation.GetErrors(data));
        if (errors.Count > 0) return new CsvValidationResult(false, errors.AsReadOnly(), warnings.AsReadOnly(), Array.Empty<UsageData>());
        var sorted = data.OrderBy(interval => interval.Timestamp).ToArray();
        if (skippedMetadata) warnings.Add("Georgia Power disclaimer/account metadata was skipped; only hourly usage and optional temperature are stored.");
        if (!wasSorted) warnings.Add("Records were out of order and have been sorted by timestamp.");
        warnings.AddRange(GetCoverageWarnings(sorted));
        return new CsvValidationResult(true, errors.AsReadOnly(), warnings.AsReadOnly(), Array.AsReadOnly(sorted));
    }

    public static IReadOnlyList<string> GetCoverageWarnings(IReadOnlyList<UsageData> sorted)
    {
        var warnings = new List<string>();
        var missingHours = UsageCoverage.CountMissingHours(sorted);
        if (missingHours > 0)
            warnings.Add($"{missingHours:N0} hourly interval(s) are missing between the first and last record. Missing usage is not filled or estimated; costs cover recorded usage only.");
        var incompleteMonths = sorted.GroupBy(interval => new DateTime(interval.Year, interval.Month, 1))
            .Where(month => month.Count(row => UsageCoverage.IsExpectedHour(row.Timestamp)) !=
                UsageCoverage.CountExpectedMonthHours(month.Key.Year, month.Key.Month))
            .Select(month => month.Key.ToString("MMM yyyy", CultureInfo.InvariantCulture)).ToArray();
        if (incompleteMonths.Length > 0)
            warnings.Add($"Incomplete calendar month(s): {string.Join(", ", incompleteMonths)}. Full monthly tier thresholds are used; estimates may understate a complete bill.");
        if (sorted.Any(row => !UsageCoverage.IsExpectedHour(row.Timestamp)))
            warnings.Add("The file includes readings in the Eastern spring-forward hour. Their usage is included in estimates. Coverage counts only expected hour labels.");
        warnings.Add("Coverage uses unique Eastern wall-clock hours and skips the spring-forward hour. Normalize repeated fall-back timestamps before import.");
        return warnings.AsReadOnly();
    }

    private static bool IsMetadataRow(IReadOnlyList<string> fields)
    {
        var first = fields.FirstOrDefault()?.Trim().TrimStart('\uFEFF') ?? "";
        // Account numbers are not copied to diagnostics, models, or persistent storage.
        if (first.StartsWith("Account Number:", StringComparison.OrdinalIgnoreCase)) return true;
        return first.StartsWith("Disclaimer:", StringComparison.OrdinalIgnoreCase) && fields.Skip(1).All(string.IsNullOrWhiteSpace);
    }

    private static Header? ReadHeader(IReadOnlyList<string> fields, out string error)
    {
        error = "";
        var width = fields.Count;
        while (width > 0 && string.IsNullOrWhiteSpace(fields[width - 1])) width--;
        var names = fields.Take(width).Select(NormalizeHeader).ToArray();
        if (names.Any(string.IsNullOrWhiteSpace) || names.Distinct(StringComparer.Ordinal).Count() != names.Length)
        { error = "empty or duplicate column headers are not allowed."; return null; }
        var timestampColumn = FindColumn(names, "timestamp", "datetime", "startdatetime");
        var kwhColumn = FindColumn(names, "kwh", "usagekwh");
        var tempColumn = FindColumn(names, "temp", "temperature");
        var yearColumn = FindColumn(names, "year");
        var monthColumn = FindColumn(names, "month");
        var dayColumn = FindColumn(names, "day");
        var hourColumn = FindColumn(names, "hour");
        var hasComponents = yearColumn >= 0 && monthColumn >= 0 && dayColumn >= 0 && hourColumn >= 0;
        if (timestampColumn < 0 && !hasComponents && yearColumn < 0 && monthColumn < 0 && dayColumn < 0)
            timestampColumn = hourColumn;
        if (kwhColumn < 0 || (timestampColumn < 0 && !hasComponents))
        { error = "required headers are Hour,kWh or Timestamp,kWh (optional Temp), or Year,Month,Day,Hour,kWh (optional Temp)."; return null; }
        var lastRequiredColumn = timestampColumn >= 0 ? Math.Max(timestampColumn, kwhColumn)
            : new[] { yearColumn, monthColumn, dayColumn, hourColumn, kwhColumn }.Max();
        // A CSV's trailing empty header fields still count toward the physical row width.
        return new Header(fields.Count, timestampColumn, kwhColumn, tempColumn, yearColumn, monthColumn, dayColumn, hourColumn, lastRequiredColumn);
    }

    private static bool TryInteger(string text, out int value)
        => int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    private static bool TryDecimal(string text, out decimal value)
        => decimal.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    private static int FindColumn(string[] headers, params string[] names)
        => Array.FindIndex(headers, header => names.Contains(header, StringComparer.Ordinal));
    private static string NormalizeHeader(string value)
        => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    private static CsvValidationResult Invalid(string error)
        => new(false, new[] { error }, Array.Empty<string>(), Array.Empty<UsageData>());
    private sealed record Header(int Width, int TimestampColumn, int KWhColumn, int TempColumn,
        int YearColumn, int MonthColumn, int DayColumn, int HourColumn, int LastRequiredColumn);
}
