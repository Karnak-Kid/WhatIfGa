using WhatIfGa.Core.Services;

namespace WhatIfGa.Core.Models;

/// <summary>Implements the supplied Nights & Weekends summer weekday peak illustration.</summary>
public sealed class NightsAndWeekendsPlan : RatePlan
{
    public const decimal OffPeakRate = 0.078m;
    public const decimal OnPeakRate = 0.303m;
    public override string PlanId => "nights-weekends";
    public override string Name => "Nights & Weekends";
    public override string Description => "Off-peak $0.078/kWh; summer weekday peak $0.303/kWh, 2–7 p.m. June–September, excluding observed U.S. federal holidays. Supplied illustration; energy only.";
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
        var totals = new Dictionary<BillingPeriod, (decimal KWh, decimal PeakKWh, int RecordedHours)>();
        var unassignedHours = 0;
        foreach (var interval in data)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (interval.KWh < 0) throw new ArgumentException("Hourly kWh cannot be negative.", nameof(data));
            var period = FindPeriod(ordered, DateOnly.FromDateTime(interval.Timestamp));
            if (period is null) { unassignedHours++; continue; }
            var total = totals.GetValueOrDefault(period);
            totals[period] = (total.KWh + interval.KWh,
                total.PeakKWh + (IsOnPeak(interval.Timestamp) ? interval.KWh : 0m),
                total.RecordedHours + (UsageCoverage.IsExpectedHour(interval.Timestamp) ? 1 : 0));
        }

        var bills = new List<MonthlyBill>(totals.Count);
        foreach (var period in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!totals.TryGetValue(period, out var total)) continue;
            var offPeak = total.KWh - total.PeakKWh;
            var lines = new List<BillLineItem>
            {
                new("Off-peak", offPeak, OffPeakRate, offPeak * OffPeakRate),
                new("On-peak • weekdays 2–7 p.m. June–September", total.PeakKWh, OnPeakRate, total.PeakKWh * OnPeakRate)
            };
            var expectedHours = period.Start < period.End ? UsageCoverage.CountExpectedHours(period.Start, period.End) : 0;
            if (period.IncludesFinalDate) expectedHours += 24;
            bills.Add(new MonthlyBill(period.BillingMonth.ToDateTime(TimeOnly.MinValue), total.KWh, lines.AsReadOnly())
            {
                Period = period, RecordedHours = total.RecordedHours, ExpectedHours = expectedHours
            });
        }
        return new BillEstimate(PlanId, Name, bills.AsReadOnly()) { UnassignedHours = unassignedHours };
    }

    private static bool IsOnPeak(DateTime timestamp)
    {
        if (timestamp.Month is < 6 or > 9 || timestamp.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ||
            timestamp.Hour is < 14 or >= 19)
            return false;
        return !ObservedFederalHolidays(timestamp.Year).Contains(DateOnly.FromDateTime(timestamp));
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
