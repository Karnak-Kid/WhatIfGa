using WhatIfGa.Core.Services;

namespace WhatIfGa.Core.Models;

/// <summary>Applies the supplied Smart Usage energy and hourly demand rates.</summary>
public sealed class SmartUsagePlan : RatePlan
{
    public const decimal OffPeakRate = 0.016m;
    public const decimal OnPeakRate = 0.146m;
    public const decimal DemandRate = 12.44m;

    public override string PlanId => "smart-usage";
    public override string Name => "Smart Usage";
    public override string Description => "Energy is $0.016/kWh off-peak and $0.146/kWh on-peak. Peak hours are 2–7 p.m. on June–September weekdays except July 4 and Labor Day. Demand is $12.44/kW for the highest hourly reading in each billing period.";
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
        decimal totalKWh, CancellationToken cancellationToken)
    {
        var onPeakKWh = 0m;
        var offPeakKWh = 0m;
        var maximumDemandKW = 0m;
        foreach (var reading in readings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsOnPeak(reading.Timestamp)) onPeakKWh += reading.KWh;
            else offPeakKWh += reading.KWh;

            // An interval's kWh over one hour is its average demand in kW.
            maximumDemandKW = Math.Max(maximumDemandKW, reading.KWh);
        }

        return Array.AsReadOnly<BillLineItem>(
        [
            new("Off-peak", offPeakKWh, OffPeakRate, offPeakKWh * OffPeakRate),
            new("On-peak • weekdays 2–7 p.m. June–September", onPeakKWh, OnPeakRate, onPeakKWh * OnPeakRate),
            BillLineItem.ForDemand("Maximum hourly demand", maximumDemandKW, DemandRate)
        ]);
    }

    private static bool IsOnPeak(DateTime timestamp)
    {
        if (!IsSummerWeekdayPeak(timestamp)) return false;
        var isIndependenceDay = timestamp.Month == 7 && timestamp.Day == 4;
        var isLaborDay = timestamp.Month == 9 && timestamp.Day <= 7 && timestamp.DayOfWeek == DayOfWeek.Monday;
        return !isIndependenceDay && !isLaborDay;
    }
}
