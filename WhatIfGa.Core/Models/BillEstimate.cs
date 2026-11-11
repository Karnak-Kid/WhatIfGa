using System.Globalization;
using System.Text;

namespace WhatIfGa.Core.Models;

public sealed record BillEstimate(string PlanId, string PlanName, IReadOnlyList<MonthlyBill> Months)
{
    public int UnassignedHours { get; init; }
    public decimal TotalCost => Months.Sum(month => month.TotalCost);
    public decimal TotalKWh => Months.Sum(month => month.KWh);

    public string GetBreakdown()
    {
        var result = new StringBuilder();
        result.AppendLine($"{PlanName} — energy charges only");
        foreach (var month in Months)
        {
            var periodLabel = month.Period is { } period
                ? $"{period.Start:MMM d, yyyy} to {period.End:MMM d, yyyy} ({period.Days} days, {period.BillingMonth:MMM yyyy} bill)"
                : month.Month.ToString("MMM yyyy", CultureInfo.InvariantCulture);
            result.AppendLine($"{periodLabel}: {month.KWh:N2} kWh • {month.TotalCost.ToString("C2", CultureInfo.GetCultureInfo("en-US"))}");
            foreach (var item in month.LineItems)
                result.AppendLine($"  {item.Description}: {item.KWh:0.####} × ${item.Rate:0.000}/kWh = ${item.Amount:0.00##}");
            if (month.IsPartial)
                result.AppendLine($"  Partial coverage: {month.RecordedHours:N0} of {month.ExpectedHours:N0} hourly readings.");
        }
        if (Months.Count > 0)
            result.AppendLine($"Total: {TotalCost.ToString("C2", CultureInfo.GetCultureInfo("en-US"))}");
        else
            result.AppendLine("No hourly readings fall in the selected billing periods.");
        if (UnassignedHours > 0)
            result.AppendLine($"{UnassignedHours:N0} hourly readings fall outside the selected billing periods.");
        result.Append("Based on the supplied rate illustration. Taxes, fuel recovery, service charges, and other riders are excluded. Missing hours are not extrapolated.");
        return result.ToString();
    }
}
