namespace WhatIfGa.Controls;

/// <summary>A display-ready point shared by the usage and estimated-cost charts.</summary>
public sealed record ChartPoint(string Label, double Original, double? Modified = null, bool OriginalAvailable = true);
