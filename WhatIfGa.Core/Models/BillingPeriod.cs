namespace WhatIfGa.Core.Models;

/// <summary>A local date window with an inclusive start and an exclusive end.</summary>
public sealed record BillingPeriod(DateOnly Start, DateOnly End, DateOnly BillingMonth)
{
    // DateOnly cannot express the exclusive day after December 31, 9999.
    internal bool IncludesFinalDate { get; init; }

    public int Days => End.DayNumber - Start.DayNumber + (IncludesFinalDate ? 1 : 0);

    public bool Contains(DateOnly date) => date >= Start && (date < End || IncludesFinalDate && date == End);
}
