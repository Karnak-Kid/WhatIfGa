namespace WhatIfGa.Core.Models;

/// <summary>An energy or demand charge. Amount retains precision until its billing-period total is rounded.</summary>
public sealed record BillLineItem(string Description, decimal KWh, decimal Rate, decimal Amount)
{
    public decimal? DemandKW { get; private init; }
    public decimal Quantity => DemandKW ?? KWh;
    public string Unit => DemandKW.HasValue ? "kW" : "kWh";

    public static BillLineItem ForDemand(string description, decimal kilowatts, decimal rate) =>
        new(description, 0m, rate, kilowatts * rate) { DemandKW = kilowatts };
}
