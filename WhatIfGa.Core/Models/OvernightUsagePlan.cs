using WhatIfGa.Core.Services;

namespace WhatIfGa.Core.Models;

/// <summary>Calculates the supplied Overnight Advantage time-of-use energy charges.</summary>
public sealed class OvernightUsagePlan : RatePlan
{
    public const decimal SuperOffPeakRate = 0.023m;
    public const decimal OffPeakRate = 0.104m;
    public const decimal OnPeakRate = 0.303m;

    public override string PlanId => "overnight-advantage";
    public override string Name => "Overnight Advantage";
    public override string Description => "Energy costs $0.023/kWh from 11 p.m. to 7 a.m. daily and $0.303/kWh on June–September weekdays from 2 to 7 p.m. Other hours cost $0.104/kWh. The regular schedule applies on holidays.";
    public override bool IsImplemented => true;

    public override BillEstimate CalculateCost(IReadOnlyList<UsageData> data, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return CalculateCost(data, BillingCalendar.Build(data, BillingSchedule.Default), cancellationToken);
    }

    public override BillEstimate CalculateCost(IReadOnlyList<UsageData> data, IReadOnlyList<BillingPeriod> periods,
        CancellationToken cancellationToken = default) => CalculateByBillingPeriod(data, periods, CalculateCharges, cancellationToken);

    private static IReadOnlyList<BillLineItem> CalculateCharges(BillingPeriod period, IReadOnlyList<UsageData> readings,
        decimal totalKWh, CancellationToken cancellationToken)
    {
        var overnightKWh = 0m;
        var offPeakKWh = 0m;
        var onPeakKWh = 0m;
        foreach (var interval in readings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var timestamp = interval.Timestamp;
            if (timestamp.Hour is < 7 or >= 23)
                overnightKWh += interval.KWh;
            else if (IsSummerWeekdayPeak(timestamp))
                onPeakKWh += interval.KWh;
            else
                offPeakKWh += interval.KWh;
        }

        return Array.AsReadOnly<BillLineItem>(
        [
            new("Super off-peak • daily 11 p.m.–7 a.m.", overnightKWh, SuperOffPeakRate, overnightKWh * SuperOffPeakRate),
            new("Off-peak", offPeakKWh, OffPeakRate, offPeakKWh * OffPeakRate),
            new("On-peak • weekdays 2–7 p.m. June–September", onPeakKWh, OnPeakRate, onPeakKWh * OnPeakRate)
        ]);
    }
}
