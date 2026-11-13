namespace WhatIfGa.Core.Models;

public sealed record MonthlyBill(DateTime Month, decimal KWh, IReadOnlyList<BillLineItem> LineItems)
{
    public BillingPeriod? Period { get; init; }
    public int RecordedHours { get; init; }
    public int ExpectedHours { get; init; }
    public int Days => Period?.Days ?? DateTime.DaysInMonth(Month.Year, Month.Month);
    public bool IsPartial => RecordedHours < ExpectedHours;
    public decimal TotalCost => decimal.Round(LineItems.Sum(item => item.Amount), 2, MidpointRounding.AwayFromZero);
}
