namespace WhatIfGa.Core.Models;

public enum BillingMode
{
    CalendarMonths,
    Estimated,
    Actual
}

public sealed record BillingSchedule(BillingMode Mode, int ClosingDay, IReadOnlyList<BillingPeriod> Periods)
{
    public static BillingSchedule Default { get; } = new(BillingMode.CalendarMonths, 1, Array.Empty<BillingPeriod>());
}
