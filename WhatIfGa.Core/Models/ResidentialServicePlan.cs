using WhatIfGa.Core.Services;

namespace WhatIfGa.Core.Models;

/// <summary>
/// Implements the supplied residential energy rates. Tiers reset at each billing boundary.
/// Incomplete periods retain the full tier thresholds. Fees and taxes are excluded.
/// </summary>
public sealed class ResidentialServicePlan : RatePlan
{
    public const decimal WinterRate = 0.082m;
    public const decimal SummerFirstRate = 0.088m;
    public const decimal SummerSecondRate = 0.146m;
    public const decimal SummerThirdRate = 0.151m;
    public override string PlanId => "residential-service";
    public override string Name => "Residential Service";
    public override string Description => "Seasonal energy pricing: $0.082/kWh in winter; three summer tiers. Supplied illustration; energy only.";
    public override bool IsImplemented => true;

    public override BillEstimate CalculateCost(IReadOnlyList<UsageData> data, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return CalculateCost(data, BillingCalendar.Build(data, BillingSchedule.Default), cancellationToken);
    }

    public override BillEstimate CalculateCost(IReadOnlyList<UsageData> data, IReadOnlyList<BillingPeriod> periods,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        cancellationToken.ThrowIfCancellationRequested();
        var ordered = BillingCalendar.OrderPeriods(periods);
        var totals = new Dictionary<BillingPeriod, (decimal KWh, int RecordedHours)>();
        var unassignedHours = 0;
        foreach (var interval in data)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A shift may merge two valid imported intervals into a larger destination interval.
            if (interval.KWh < 0)
                throw new ArgumentException("Hourly kWh cannot be negative.", nameof(data));
            var period = FindPeriod(ordered, DateOnly.FromDateTime(interval.Timestamp));
            if (period is null)
            {
                unassignedHours++;
                continue;
            }

            var total = totals.GetValueOrDefault(period);
            var recordedHour = UsageCoverage.IsExpectedHour(interval.Timestamp) ? 1 : 0;
            totals[period] = (total.KWh + interval.KWh, total.RecordedHours + recordedHour);
        }

        var bills = new List<MonthlyBill>(totals.Count);
        foreach (var period in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!totals.TryGetValue(period, out var total)) continue;
            var usage = total.KWh;
            var lines = new List<BillLineItem>();
            if (period.BillingMonth.Month is >= 6 and <= 9)
            {
                AddLine(lines, "Summer • first 650 kWh", Math.Min(usage, 650m), SummerFirstRate);
                AddLine(lines, "Summer • next 350 kWh", Math.Min(Math.Max(usage - 650m, 0m), 350m), SummerSecondRate);
                AddLine(lines, "Summer • above 1,000 kWh", Math.Max(usage - 1000m, 0m), SummerThirdRate);
            }
            else
                AddLine(lines, "Winter • all usage", usage, WinterRate);
            var expectedHours = period.Start < period.End ? UsageCoverage.CountExpectedHours(period.Start, period.End) : 0;
            if (period.IncludesFinalDate) expectedHours += 24;
            bills.Add(new MonthlyBill(period.BillingMonth.ToDateTime(TimeOnly.MinValue), usage, lines.AsReadOnly())
            {
                Period = period,
                RecordedHours = total.RecordedHours,
                ExpectedHours = expectedHours
            });
        }

        return new BillEstimate(PlanId, Name, bills.AsReadOnly()) { UnassignedHours = unassignedHours };
    }

    private static BillingPeriod? FindPeriod(IReadOnlyList<BillingPeriod> periods, DateOnly date)
    {
        foreach (var period in periods)
        {
            if (period.Start > date) break;
            if (period.Contains(date)) return period;
        }

        return null;
    }

    private static void AddLine(List<BillLineItem> lines, string description, decimal kwh, decimal rate)
    {
        if (kwh > 0 || lines.Count == 0)
            lines.Add(new BillLineItem(description, kwh, rate, kwh * rate));
    }
}
