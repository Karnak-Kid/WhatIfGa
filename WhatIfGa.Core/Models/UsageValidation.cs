namespace WhatIfGa.Core.Models;

/// <summary>Shared import and persistence constraints.</summary>
public static class UsageValidation
{
    public const int MaximumRecords = 8784;
    public const decimal MaximumHourlyKWh = 100000m;
    public const long MaximumFileBytes = 10 * 1024 * 1024;

    public static IReadOnlyList<string> GetErrors(IReadOnlyList<UsageData> data)
    {
        var errors = new List<string>();
        if (data.Count == 0) errors.Add("The file contains no usage records.");
        if (data.Count > MaximumRecords) errors.Add($"At most {MaximumRecords:N0} hourly records (one leap year) are supported.");
        var timestamps = new HashSet<DateTime>();
        foreach (var interval in data)
        {
            if (interval is null) { errors.Add("A usage record is null."); break; }
            if (interval.Timestamp.Kind != DateTimeKind.Unspecified || interval.Timestamp.Ticks % TimeSpan.TicksPerHour != 0)
            { errors.Add("Timestamps must be hourly local wall-clock values without a time-zone suffix."); break; }
            if (interval.KWh is < 0 or > MaximumHourlyKWh)
            { errors.Add("Hourly kWh must be between 0 and 100,000."); break; }
            if (!timestamps.Add(interval.Timestamp))
            { errors.Add($"Duplicate hourly timestamp: {interval.Timestamp:yyyy-MM-dd HH:mm}."); break; }
        }
        if (data.Count > 0 && data.All(interval => interval is not null))
        {
            var span = data.Max(interval => interval.Timestamp) - data.Min(interval => interval.Timestamp);
            if (span >= TimeSpan.FromDays(366)) errors.Add("Usage history must span less than 366 days (up to one leap year).");
        }
        return errors.AsReadOnly();
    }
}
