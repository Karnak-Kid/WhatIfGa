using ReactiveUI;
using WhatIfGa.Core.Models;

namespace WhatIfGa.ViewModels;

public sealed class PlanRowViewModel(RatePlan plan) : ViewModelBase
{
    private string _costText = "—";

    public string PlanId => plan.PlanId;
    public string Name => plan.Name;
    public string Description => plan.Description;
    public bool IsImplemented => plan.IsImplemented;
    public string Status => IsImplemented ? "Available" : "Planned";
    public string CostText
    {
        get => _costText;
        internal set => this.RaiseAndSetIfChanged(ref _costText, value);
    }
}
