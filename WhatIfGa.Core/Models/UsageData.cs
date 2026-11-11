namespace WhatIfGa.Core.Models;

/// <summary>An hourly interval starting at Timestamp, interpreted as local wall-clock time.</summary>
public sealed record UsageData(DateTime Timestamp, decimal KWh, decimal? Temp = null)
{
    public int Year => Timestamp.Year;
    public int Month => Timestamp.Month;
    public int Day => Timestamp.Day;
    public int Hour => Timestamp.Hour;
}
