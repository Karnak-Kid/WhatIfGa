using WhatIfGa.Core.Services;

namespace WhatIfGa.Core.Models;

/// <summary>Implements the supplied Nights & Weekends summer weekday peak illustration.</summary>
public sealed class NightsAndWeekendsPlan : RatePlan
{
    public const decimal OffPeakRate = 0.078m;
    public const decimal OnPeakRate = 0.303m;
    public override string PlanId => "nights-weekends";
    public override string Name => "Nights & Weekends";
    public override string Description => "Off-peak is $0.078/kWh. On-peak is $0.303/kWh on weekdays from 2–7 p.m. June–September, excluding observed U.S. federal holidays.";
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
        var peakKWh = 0m;
        foreach (var reading in readings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsOnPeak(reading.Timestamp)) peakKWh += reading.KWh;
        }

        var offPeak = totalKWh - peakKWh;
        return Array.AsReadOnly<BillLineItem>(
        [
            new("Off-peak", offPeak, OffPeakRate, offPeak * OffPeakRate),
            new("On-peak • weekdays 2–7 p.m. June–September", peakKWh, OnPeakRate, peakKWh * OnPeakRate)
        ]);
    }

    private static bool IsOnPeak(DateTime timestamp)
    {
        return IsSummerWeekdayPeak(timestamp) && !ObservedFederalHolidays(timestamp.Year).Contains(DateOnly.FromDateTime(timestamp));
    }

    private static HashSet<DateOnly> ObservedFederalHolidays(int year)
    {
        var holidays = new HashSet<DateOnly>();
        void Add(DateOnly date)
        {
            holidays.Add(date);
            if (date.DayOfWeek == DayOfWeek.Saturday) holidays.Add(date.AddDays(-1));
            else if (date.DayOfWeek == DayOfWeek.Sunday) holidays.Add(date.AddDays(1));
        }
        Add(new DateOnly(year, 1, 1));
        Add(NthWeekday(year, 1, DayOfWeek.Monday, 3));
        Add(NthWeekday(year, 2, DayOfWeek.Monday, 3));
        Add(LastWeekday(year, 5, DayOfWeek.Monday));
        Add(new DateOnly(year, 6, 19));
        Add(new DateOnly(year, 7, 4));
        Add(NthWeekday(year, 9, DayOfWeek.Monday, 1));
        Add(NthWeekday(year, 10, DayOfWeek.Monday, 2));
        Add(new DateOnly(year, 11, 11));
        Add(NthWeekday(year, 11, DayOfWeek.Thursday, 4));
        Add(new DateOnly(year, 12, 25));
        return holidays;
    }

    private static DateOnly NthWeekday(int year, int month, DayOfWeek day, int nth)
    {
        var first = new DateOnly(year, month, 1);
        var offset = ((int)day - (int)first.DayOfWeek + 7) % 7;
        return first.AddDays(offset + 7 * (nth - 1));
    }

    private static DateOnly LastWeekday(int year, int month, DayOfWeek day)
    {
        var last = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
        return last.AddDays(-(((int)last.DayOfWeek - (int)day + 7) % 7));
    }
}
