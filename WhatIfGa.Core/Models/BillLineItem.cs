namespace WhatIfGa.Core.Models;

/// <summary>Energy-only charge. Amount retains precision until its monthly total is rounded.</summary>
public sealed record BillLineItem(string Description, decimal KWh, decimal Rate, decimal Amount);
