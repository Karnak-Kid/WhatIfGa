using WhatIfGa.Core.Models;

namespace WhatIfGa.Core.Services;

/// <summary>Coverage for unique local hour labels in Georgia Power's Eastern time exports.</summary>
public static class UsageCoverage
{
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.TryFindSystemTimeZoneById("America/New_York", out var zone)
        ? zone
        : TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

    /// <summary>Counts expected unique hour labels from start inclusive to end exclusive.</summary>
    public static int CountExpectedHours(DateOnly start, DateOnly end)
    {
        if (end < start) throw new ArgumentOutOfRangeException(nameof(end), "The end date must follow the start date.");

        var hours = (end.DayNumber - start.DayNumber) * 24;
        return CountExpectedHours(start.ToDateTime(TimeOnly.MinValue), hours);
    }

    public static bool IsExpectedHour(DateTime timestamp)
    {
        // The export has no UTC offsets. Each fall-back hour label is expected once.
        var wallClock = DateTime.SpecifyKind(timestamp, DateTimeKind.Unspecified);
        return !Eastern.IsInvalidTime(wallClock);
    }

    internal static int CountExpectedMonthHours(int year, int month)
        => CountExpectedHours(new DateTime(year, month, 1), DateTime.DaysInMonth(year, month) * 24);

    internal static int CountMissingHours(IReadOnlyList<UsageData> sorted)
    {
        var missing = 0;
        for (var index = 1; index < sorted.Count; index++)
        {
            var previous = sorted[index - 1].Timestamp;
            var gap = sorted[index].Timestamp - previous;
            var skippedHours = (int)(gap.Ticks / TimeSpan.TicksPerHour) - 1;
            if (skippedHours > 0)
                missing += CountExpectedHours(previous.AddHours(1), skippedHours);
        }

        return missing;
    }

    private static int CountExpectedHours(DateTime start, int hours)
    {
        var expected = 0;
        for (var offset = 0; offset < hours; offset++)
        {
            if (IsExpectedHour(start.AddHours(offset))) expected++;
        }

        return expected;
    }
}
