using System.Globalization;
using WhatIfGa.Core.Models;

namespace WhatIfGa.ViewModels;

public sealed class MonthlyBillRowViewModel(MonthlyBill bill)
{
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

    public DateTime Month => bill.Month;
    public BillingPeriod? Period => bill.Period;
    public string MonthLabel => Period is { } period
        ? $"{period.Start.ToString(period.Start.Year == period.End.Year ? "MMM d" : "MMM d, yyyy", Us)} – {period.End.ToString("MMM d, yyyy", Us)}"
        : bill.Month.ToString("MMMM yyyy", Us);
    public string UsageText => $"{bill.KWh:N1} kWh";
    public string CostText => bill.TotalCost.ToString("C2", Us);
    public string CoverageText => Period is { } period
        ? $"{period.Days} days · {bill.RecordedHours:N0}/{bill.ExpectedHours:N0} recorded hours" + (bill.IsPartial ? " · partial" : "")
        : "";
    private string DailyAverages => Period is { Days: > 0 } period && !bill.IsPartial
        ? $"\n{bill.KWh / period.Days:N1} kWh/day · {(bill.TotalCost / period.Days).ToString("C2", Us)}/day estimated cost"
        : "";

    public string Breakdown => $"{MonthLabel} · {UsageText}\nBilling month · {bill.Month.ToString("MMMM yyyy", Us)}\n{CoverageText}{DailyAverages}\n\n" +
        string.Join("\n\n", bill.LineItems.Select(line =>
            string.Create(Us, $"{line.Description}\n{line.Quantity:N3} {line.Unit} × ${line.Rate:0.00#}/{line.Unit} = ${line.Amount:#,0.00##}"))) +
        $"\n\nEstimated subtotal · {CostText} (rounded to cents)\n\nService charges, fuel adjustments, riders and taxes are excluded.";
}
