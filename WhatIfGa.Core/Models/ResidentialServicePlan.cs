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
    public override string Description => "October–May billing months use $0.082/kWh. June–September billing months use $0.088/kWh for the first 650 kWh, $0.146/kWh for the next 350 kWh, and $0.151/kWh above 1,000 kWh.";
    public override bool IsImplemented => true;

    public override BillEstimate CalculateCost(IReadOnlyList<UsageData> data, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return CalculateCost(data, BillingCalendar.Build(data, BillingSchedule.Default), cancellationToken);
    }

    public override BillEstimate CalculateCost(IReadOnlyList<UsageData> data, IReadOnlyList<BillingPeriod> periods,
        CancellationToken cancellationToken = default)
        => CalculateByBillingPeriod(data, periods, CalculateCharges, cancellationToken);

    private static IReadOnlyList<BillLineItem> CalculateCharges(BillingPeriod period, IReadOnlyList<UsageData> readings,
        decimal usage, CancellationToken cancellationToken)
    {
        var lines = new List<BillLineItem>();
        if (period.BillingMonth.Month is >= 6 and <= 9)
        {
            AddLine(lines, "Summer • first 650 kWh", Math.Min(usage, 650m), SummerFirstRate);
            AddLine(lines, "Summer • next 350 kWh", Math.Min(Math.Max(usage - 650m, 0m), 350m), SummerSecondRate);
            AddLine(lines, "Summer • above 1,000 kWh", Math.Max(usage - 1000m, 0m), SummerThirdRate);
        }
        else
            AddLine(lines, "Winter • all usage", usage, WinterRate);
        return lines.AsReadOnly();
    }

    private static void AddLine(List<BillLineItem> lines, string description, decimal kwh, decimal rate)
    {
        if (kwh > 0 || lines.Count == 0)
            lines.Add(new BillLineItem(description, kwh, rate, kwh * rate));
    }
}
