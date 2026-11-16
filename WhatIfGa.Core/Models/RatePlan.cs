using WhatIfGa.Core.Services;

namespace WhatIfGa.Core.Models;

public abstract class RatePlan
{
    public abstract string PlanId { get; }
    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract bool IsImplemented { get; }
    public abstract BillEstimate CalculateCost(IReadOnlyList<UsageData> data, CancellationToken cancellationToken = default);

    public virtual BillEstimate CalculateCost(IReadOnlyList<UsageData> data, IReadOnlyList<BillingPeriod> periods,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException($"{Name} has no billing-period calculation.");
    }

    protected BillEstimate CalculateByBillingPeriod(IReadOnlyList<UsageData> data, IReadOnlyList<BillingPeriod> periods,
        Func<BillingPeriod, IReadOnlyList<UsageData>, decimal, CancellationToken, IReadOnlyList<BillLineItem>> calculateCharges,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);
        cancellationToken.ThrowIfCancellationRequested();
        var ordered = BillingCalendar.OrderPeriods(periods);
        var readingsByPeriod = new Dictionary<BillingPeriod, List<UsageData>>();
        var unassignedHours = 0;
        foreach (var reading in data)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reading.KWh < 0)
                throw new ArgumentException("Hourly kWh cannot be negative.", nameof(data));

            var period = FindPeriod(ordered, DateOnly.FromDateTime(reading.Timestamp));
            if (period is null)
            {
                unassignedHours++;
                continue;
            }

            if (!readingsByPeriod.TryGetValue(period, out var readings))
                readingsByPeriod.Add(period, readings = []);
            readings.Add(reading);
        }

        var bills = new List<MonthlyBill>(readingsByPeriod.Count);
        foreach (var period in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!readingsByPeriod.TryGetValue(period, out var readings)) continue;

            var kwh = readings.Sum(reading => reading.KWh);
            var lines = calculateCharges(period, readings, kwh, cancellationToken);
            var expectedHours = UsageCoverage.CountExpectedHours(period.Start, period.End);
            if (period.IncludesFinalDate) expectedHours += 24;
            bills.Add(new MonthlyBill(period.BillingMonth.ToDateTime(TimeOnly.MinValue), kwh, lines)
            {
                Period = period,
                RecordedHours = readings.Count(reading => UsageCoverage.IsExpectedHour(reading.Timestamp)),
                ExpectedHours = expectedHours
            });
        }

        return new BillEstimate(PlanId, Name, bills.AsReadOnly()) { UnassignedHours = unassignedHours };
    }

    protected static bool IsSummerWeekdayPeak(DateTime timestamp) =>
        timestamp.Month is >= 6 and <= 9 && timestamp.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) &&
        timestamp.Hour is >= 14 and < 19;

    private static BillingPeriod? FindPeriod(IReadOnlyList<BillingPeriod> periods, DateOnly date)
    {
        foreach (var period in periods)
        {
            if (period.Start > date) break;
            if (period.Contains(date)) return period;
        }

        return null;
    }
}
