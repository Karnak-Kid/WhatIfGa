using WhatIfGa.Core.Models;

namespace WhatIfGa.Core.Services;

public sealed class RateCalculator
{
    public IReadOnlyList<RatePlan> Plans { get; } = Array.AsReadOnly<RatePlan>(
    [
        new ResidentialServicePlan(),
        new PlaceholderRatePlan("smart-usage", "Smart Usage", "Time-of-use energy and demand pricing. Calculation coming soon."),
        new PlaceholderRatePlan("overnight-advantage", "Overnight Advantage", "Time-of-use pricing with overnight hours. Calculation coming soon."),
        new PlaceholderRatePlan("nights-weekends", "Nights & Weekends", "Time-of-use pricing for nights and weekends. Calculation coming soon."),
        new PlaceholderRatePlan("flatbill", "FlatBill", "Requires a personalized utility quote. Calculation unavailable."),
        new PlaceholderRatePlan("prepay", "PrePay", "Prepaid billing program. Applicable pricing is not implemented."),
        new PlaceholderRatePlan("pay-by-day", "Pay by Day", "Requires a personalized fixed daily utility quote. Calculation unavailable.")
    ]);

    public IReadOnlyList<BillEstimate> Compare(IReadOnlyList<UsageData> data, CancellationToken cancellationToken = default)
        => Compare(data, BillingSchedule.Default, cancellationToken);

    public IReadOnlyList<BillEstimate> Compare(IReadOnlyList<UsageData> data, BillingSchedule schedule,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Compare(data, BillingCalendar.Build(data, schedule), cancellationToken);
    }

    public IReadOnlyList<BillEstimate> Compare(IReadOnlyList<UsageData> data, IReadOnlyList<BillingPeriod> periods,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(periods);
        var result = new List<BillEstimate>();
        foreach (var plan in Plans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!plan.IsImplemented) continue;

            var estimate = plan.CalculateCost(data, periods, cancellationToken);
            if (estimate.Months.Count > 0) result.Add(estimate);
        }
        return result.OrderBy(estimate => estimate.TotalCost).ToArray();
    }

    /// <summary>Returns the cheapest available implementation only; this is not a seven-plan recommendation.</summary>
    public BillEstimate? Recommend(IReadOnlyList<BillEstimate> estimates)
    {
        ArgumentNullException.ThrowIfNull(estimates);
        return estimates.Where(estimate => estimate.Months.Count > 0)
            .OrderBy(estimate => estimate.TotalCost).FirstOrDefault();
    }

    /// <summary>
    /// Replays recorded usage exactly under the supplied rates as a historical projection.
    /// It does not extend incomplete history or predict weather, behavior, or future tariffs.
    /// </summary>
    public IReadOnlyList<BillEstimate> ForecastCosts(IReadOnlyList<UsageData> data, CancellationToken cancellationToken = default)
        => Compare(data, cancellationToken);

    public IReadOnlyList<BillEstimate> CompareScenario(IReadOnlyList<UsageData> data, UsageScenario scenario, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var periods = BillingCalendar.Build(data, BillingSchedule.Default);
        scenario.OriginalCosts = Compare(data, periods, cancellationToken);
        scenario.ModifiedCosts = Compare(scenario.Apply(data, cancellationToken), periods, cancellationToken);
        return scenario.ModifiedCosts;
    }
}
