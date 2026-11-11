namespace WhatIfGa.Core.Models;

/// <summary>
/// Reduces every interval by ReductionPercent, and moves ShiftPercent of selected hours by
/// ShiftHours within each local calendar day. A window whose end precedes its start crosses
/// midnight; equal start/end means the full day. SourceEndHour is exclusive.
/// </summary>
public sealed class UsageScenario
{
    public string Name { get; set; } = "My scenario";
    public decimal ReductionPercent { get; set; }
    public int ShiftHours { get; set; }
    public decimal ShiftPercent { get; set; }
    public int SourceStartHour { get; set; } = 14;
    public int SourceEndHour { get; set; } = 19;
    public IReadOnlyList<BillEstimate> OriginalCosts { get; set; } = Array.Empty<BillEstimate>();
    public IReadOnlyList<BillEstimate> ModifiedCosts { get; set; } = Array.Empty<BillEstimate>();

    public IReadOnlyList<UsageData> Apply(IReadOnlyList<UsageData> data)
        => Apply(data, CancellationToken.None);

    public IReadOnlyList<UsageData> Apply(IReadOnlyList<UsageData> data, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (ReductionPercent is < 0m or > 100m || ShiftPercent is < 0m or > 100m)
            throw new ArgumentOutOfRangeException(nameof(ReductionPercent), "Reduction and shift percentages must be between 0 and 100.");
        if (ShiftHours is < -23 or > 23)
            throw new ArgumentOutOfRangeException(nameof(ShiftHours), "Shift hours must be between -23 and 23.");
        if (SourceStartHour is < 0 or > 23 || SourceEndHour is < 0 or > 24)
            throw new ArgumentOutOfRangeException(nameof(SourceStartHour), "The source window must use hours from 0 to 23 (end may be 24).");

        var totals = new SortedDictionary<DateTime, (decimal KWh, decimal? Temp)>();
        var scale = 1m - ReductionPercent / 100m;
        foreach (var interval in data)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (interval.KWh < 0)
                throw new ArgumentException("Usage cannot be negative.", nameof(data));
            var usage = interval.KWh * scale;
            var shifted = IsInWindow(interval.Hour) ? usage * ShiftPercent / 100m : 0m;
            Add(interval.Timestamp, usage - shifted, interval.Temp);
            if (shifted != 0)
            {
                var targetHour = ((interval.Hour + ShiftHours) % 24 + 24) % 24;
                Add(interval.Timestamp.Date.AddHours(targetHour), shifted, null);
            }
        }
        return totals.Select(pair => new UsageData(pair.Key, pair.Value.KWh, pair.Value.Temp)).ToArray();

        void Add(DateTime timestamp, decimal amount, decimal? temperature)
        {
            var existing = totals.GetValueOrDefault(timestamp);
            totals[timestamp] = (existing.KWh + amount, temperature ?? existing.Temp);
        }
    }

    public decimal? GetCostDifference(string planId)
    {
        var original = OriginalCosts.FirstOrDefault(cost => cost.PlanId == planId);
        var modified = ModifiedCosts.FirstOrDefault(cost => cost.PlanId == planId);
        return original is null || modified is null ? null : original.TotalCost - modified.TotalCost;
    }

    private bool IsInWindow(int hour)
        => SourceStartHour == SourceEndHour ||
           (SourceStartHour < SourceEndHour
               ? hour >= SourceStartHour && hour < SourceEndHour
               : hour >= SourceStartHour || hour < SourceEndHour);
}
