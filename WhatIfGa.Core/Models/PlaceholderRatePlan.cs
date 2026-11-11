namespace WhatIfGa.Core.Models;

/// <summary>Visible in the plan catalog but never treated as a zero-dollar estimate.</summary>
public sealed class PlaceholderRatePlan(string planId, string name, string description) : RatePlan
{
    public override string PlanId => planId;
    public override string Name => name;
    public override string Description => description;
    public override bool IsImplemented => false;

    public override BillEstimate CalculateCost(IReadOnlyList<UsageData> data, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException($"{Name} calculations are not implemented. No estimate or recommendation is available for this plan.");
    }
}
