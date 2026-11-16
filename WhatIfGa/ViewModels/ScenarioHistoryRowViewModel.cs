namespace WhatIfGa.ViewModels;

public sealed record ScenarioHistoryRowViewModel(string Name, string Settings, string CostText, string SavingsText)
{
    public string? PlanName { get; init; }
}
