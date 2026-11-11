using WhatIfGa.Core.Models;

namespace WhatIfGa.Core.Services;

/// <summary>Builds and validates the billing windows shared by all rate plans.</summary>
public static class BillingCalendar
{
    private const int MaximumPeriods = 366;
    private const int MaximumDaysPerPeriod = 366;

    public static BillingSchedule Validate(BillingSchedule schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        if (!Enum.IsDefined(schedule.Mode))
            throw new ArgumentException("Choose a supported billing schedule.");
        if (schedule.Mode == BillingMode.Estimated && schedule.ClosingDay is < 1 or > 31)
            throw new ArgumentException("Choose an estimated closing day from 1 to 31.");

        var savedPeriods = schedule.Periods ?? Array.Empty<BillingPeriod>();
        if (schedule.Mode == BillingMode.Actual && savedPeriods.Count == 0)
            throw new ArgumentException("Enter at least one actual billing period.");

        return schedule with { Periods = Array.AsReadOnly(OrderPeriods(savedPeriods)) };
    }

    public static IReadOnlyList<BillingPeriod> Build(IReadOnlyList<UsageData> data, BillingSchedule schedule)
    {
        ArgumentNullException.ThrowIfNull(data);
        var validated = Validate(schedule);
        if (validated.Mode == BillingMode.Actual) return validated.Periods;

        var periods = data.Select(interval => DateOnly.FromDateTime(interval.Timestamp))
            .Distinct()
            .Select(date => validated.Mode == BillingMode.CalendarMonths
                ? CalendarPeriod(date)
                : EstimatedPeriod(date, validated.ClosingDay))
            .Distinct()
            .OrderBy(period => period.Start)
            .ToArray();
        return Array.AsReadOnly(periods);
    }

    internal static BillingPeriod[] OrderPeriods(IReadOnlyList<BillingPeriod> periods)
    {
        ArgumentNullException.ThrowIfNull(periods);
        if (periods.Count > MaximumPeriods)
            throw new ArgumentException($"Enter at most {MaximumPeriods} billing periods.");

        var ordered = periods.ToArray();
        foreach (var period in ordered)
        {
            if (period is null || period.Days <= 0)
                throw new ArgumentException("Each billing period must end after it starts.");
            if (period.Days > MaximumDaysPerPeriod)
                throw new ArgumentException($"Each billing period must span at most {MaximumDaysPerPeriod} days.");
            if (period.BillingMonth.Day != 1)
                throw new ArgumentException("The billing month must use the first day of that month.");
        }

        Array.Sort(ordered, (left, right) => left.Start.CompareTo(right.Start));
        for (var index = 1; index < ordered.Length; index++)
            if (ordered[index - 1].Contains(ordered[index].Start))
                throw new ArgumentException("Billing periods cannot overlap.");
        return ordered;
    }

    private static BillingPeriod CalendarPeriod(DateOnly date)
    {
        var month = FirstOfMonth(date);
        return IsFinalMonth(month)
            ? FinalPeriod(month, month)
            : new BillingPeriod(month, month.AddMonths(1), month);
    }

    private static BillingPeriod EstimatedPeriod(DateOnly date, int closingDay)
    {
        var month = FirstOfMonth(date);
        var close = ClosingDate(month, closingDay);
        if (date < close)
        {
            var start = month == DateOnly.MinValue ? month : ClosingDate(month.AddMonths(-1), closingDay);
            return new BillingPeriod(start, close, month);
        }

        if (IsFinalMonth(month)) return FinalPeriod(close, month);
        var nextMonth = month.AddMonths(1);
        return new BillingPeriod(close, ClosingDate(nextMonth, closingDay), nextMonth);
    }

    private static DateOnly ClosingDate(DateOnly month, int closingDay) =>
        new(month.Year, month.Month, Math.Min(closingDay, DateTime.DaysInMonth(month.Year, month.Month)));

    private static DateOnly FirstOfMonth(DateOnly date) => new(date.Year, date.Month, 1);

    private static bool IsFinalMonth(DateOnly month) => month.Year == 9999 && month.Month == 12;

    private static BillingPeriod FinalPeriod(DateOnly start, DateOnly month) =>
        new(start, DateOnly.MaxValue, month) { IncludesFinalDate = true };
}
