using System.Globalization;
using WhatIfGa.Core.Models;

namespace WhatIfGa.Core.Services;

/// <summary>Derives billing windows from the dates of consecutive bills in a local history export.</summary>
public static class BillHistoryImporter
{
    private static readonly string[] DateFormats = ["yyyy-MM-dd", "M/d/yyyy"];
    private static readonly string[] ExcelDateFormats = ["yyyy-MM-dd HH:mm:ss.FFFFFFF", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF"];

    public static BillHistoryImportResult Read(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var rows = extension switch
        {
            ".csv" => CsvUsageReader.Read(path, cancellationToken),
            ".xlsx" => XlsxUsageReader.Read(path, fields => ReadHeader(fields) is not null, "bill history",
                "No visible worksheet has the required bill history headers Date and Description.", cancellationToken),
            _ => throw new InvalidDataException("Choose a .csv or .xlsx bill history export.")
        };

        var isWorkbook = extension == ".xlsx";
        var location = isWorkbook ? "Worksheet row" : "Line";
        var header = rows.FirstOrDefault(row => row.Fields.Any(field => !string.IsNullOrWhiteSpace(field)))
            ?? throw new InvalidDataException("The bill history export is empty. It needs Date and Description headers.");
        var columns = ReadHeader(header.Fields)
            ?? throw new InvalidDataException($"{location} {header.Number} must have exactly one Date and one Description header.");

        var billDates = new HashSet<DateOnly>();
        var ignoredTransactions = 0;
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (row.Number <= header.Number || row.Fields.All(string.IsNullOrWhiteSpace)) continue;

            if (!ReadField(row.Fields, columns.Description).Equals("Bill", StringComparison.OrdinalIgnoreCase))
            {
                ignoredTransactions++;
                continue;
            }

            if (!TryReadDate(ReadField(row.Fields, columns.Date), isWorkbook, out var date))
                throw new InvalidDataException($"{location} {row.Number} has an invalid Bill date. Use yyyy-MM-dd or M/d/yyyy.");

            billDates.Add(date);
        }

        if (billDates.Count < 2)
            throw new InvalidDataException("The export must contain at least two distinct Bill dates to make a billing period.");

        var dates = billDates.Order().ToArray();
        var periods = new BillingPeriod[dates.Length - 1];
        for (var index = 0; index < periods.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var end = dates[index + 1];
            periods[index] = new BillingPeriod(dates[index], end, new DateOnly(end.Year, end.Month, 1));
        }

        var schedule = BillingCalendar.Validate(new BillingSchedule(BillingMode.Actual, BillingSchedule.Default.ClosingDay, periods));
        return new BillHistoryImportResult(schedule.Periods, dates[0], dates.Length, ignoredTransactions);
    }

    private static (int Date, int Description)? ReadHeader(IReadOnlyList<string> fields)
    {
        var date = FindColumn(fields, "Date");
        var description = FindColumn(fields, "Description");
        return date >= 0 && description >= 0 ? (date, description) : null;
    }

    private static int FindColumn(IReadOnlyList<string> fields, string name)
    {
        var column = -1;
        for (var index = 0; index < fields.Count; index++)
        {
            if (!fields[index].Trim().Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            if (column >= 0) return -1;
            column = index;
        }

        return column;
    }

    private static string ReadField(IReadOnlyList<string> fields, int index) =>
        index < fields.Count ? fields[index].Trim() : string.Empty;

    private static bool TryReadDate(string text, bool isWorkbook, out DateOnly date)
    {
        if (DateOnly.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) return true;

        if (!isWorkbook || !DateTime.TryParseExact(text, ExcelDateFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var timestamp) || timestamp.TimeOfDay != TimeSpan.Zero)
            return false;

        date = DateOnly.FromDateTime(timestamp);
        return true;
    }
}
