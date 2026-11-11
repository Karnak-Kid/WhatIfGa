namespace WhatIfGa.Core.Models;

public abstract class RatePlan
{
    public abstract string PlanId { get; }
    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract bool IsImplemented { get; }
    public abstract BillEstimate CalculateCost(IReadOnlyList<UsageData> data, CancellationToken cancellationToken = default);

    public virtual BillEstimate CalculateCost(IReadOnlyList<UsageData> data, IReadOnlyList<BillingPeriod> periods,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException($"{Name} has no billing-period calculation.");
    }
}
