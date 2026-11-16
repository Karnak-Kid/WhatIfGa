using WhatIfGa.Core.Models;
using WhatIfGa.Core.Services;
using WhatIfGa.Services;
using WhatIfGa.ViewModels;

internal static class RatePlanRegressionTests
{
    public static void SmartUsage()
    {
        var first = new BillingPeriod(new DateOnly(2025, 5, 20), new DateOnly(2025, 7, 5), new DateOnly(2025, 7, 1));
        var second = new BillingPeriod(first.End, new DateOnly(2025, 9, 3), new DateOnly(2025, 9, 1));
        var schedule = new BillingSchedule(BillingMode.Actual, 1, [first, second]);
        UsageData[] usage =
        [
            new(new DateTime(2025, 5, 19, 23, 0, 0), 99m),
            new(new DateTime(2025, 5, 30, 14, 0, 0), 7m),
            new(new DateTime(2025, 6, 3, 13, 0, 0), 1m),
            new(new DateTime(2025, 6, 3, 14, 0, 0), 2m),
            new(new DateTime(2025, 6, 3, 18, 0, 0), 3m),
            new(new DateTime(2025, 6, 3, 19, 0, 0), 4m),
            new(new DateTime(2025, 6, 7, 15, 0, 0), 5m),
            new(new DateTime(2025, 7, 4, 15, 0, 0), 6m),
            new(new DateTime(2025, 7, 5, 15, 0, 0), 1m),
            new(new DateTime(2025, 9, 1, 15, 0, 0), 5m),
            new(new DateTime(2025, 9, 2, 15, 0, 0), 4m),
            new(new DateTime(2025, 9, 3, 15, 0, 0), 99m),
        ];

        var estimate = new RateCalculator().Compare(usage, schedule).Single(plan => plan.PlanId == "smart-usage");
        Equal(2, estimate.Months.Count, "two actual bills");
        Equal(2, estimate.UnassignedHours, "out-of-period readings are excluded");
        Equal(38m, estimate.TotalKWh, "demand does not add to billed energy");

        var firstBill = estimate.Months[0];
        Equal(5m, firstBill.LineItems.Single(line => line.Rate == 0.146m).KWh, "June weekday 14:00–19:00 peak energy");
        Equal(23m, firstBill.LineItems.Single(line => line.Rate == 0.016m).KWh, "May readings and July 4 remain off-peak in a July bill");
        var demand = firstBill.LineItems.Single(line => line.DemandKW.HasValue);
        Equal<decimal?>(7m, demand.DemandKW, "maximum demand includes off-peak readings");
        Equal("kW", demand.Unit, "demand uses power units");
        Equal(0m, demand.KWh, "demand line contains no energy");
        Equal(12.44m, demand.Rate, "demand rate");
        Equal(88.18m, firstBill.TotalCost, "first billing period total");

        var secondBill = estimate.Months[1];
        Equal(4m, secondBill.LineItems.Single(line => line.Rate == 0.146m).KWh, "Labor Day stays off-peak");
        Equal<decimal?>(5m, secondBill.LineItems.Single(line => line.DemandKW.HasValue).DemandKW, "demand resets for each billing period");
        Equal(62.88m, secondBill.TotalCost, "second billing period total");
    }

    public static void OvernightAdvantage()
    {
        UsageData[] usage =
        [
            new(new DateTime(2025, 6, 2, 6, 0, 0), 1m),
            new(new DateTime(2025, 6, 2, 7, 0, 0), 2m),
            new(new DateTime(2025, 6, 2, 13, 0, 0), 3m),
            new(new DateTime(2025, 6, 2, 14, 0, 0), 4m),
            new(new DateTime(2025, 6, 2, 18, 0, 0), 5m),
            new(new DateTime(2025, 6, 2, 19, 0, 0), 6m),
            new(new DateTime(2025, 6, 2, 22, 0, 0), 7m),
            new(new DateTime(2025, 6, 2, 23, 0, 0), 8m),
            new(new DateTime(2025, 6, 7, 14, 0, 0), 9m),
            new(new DateTime(2025, 7, 4, 14, 0, 0), 10m),
            new(new DateTime(2025, 10, 1, 0, 0, 0), 12m),
            new(new DateTime(2025, 10, 1, 14, 0, 0), 11m),
        ];

        var estimate = new RateCalculator().Compare(usage).Single(plan => plan.PlanId == "overnight-advantage");
        Equal(3, estimate.Months.Count, "three months with recorded usage");
        var june = estimate.Months[0];
        Equal(9m, june.LineItems.Single(line => line.Rate == 0.023m).KWh, "23:00 and 06:00 are super off-peak");
        Equal(27m, june.LineItems.Single(line => line.Rate == 0.104m).KWh, "07:00, midday, evening, and weekend energy is off-peak");
        Equal(9m, june.LineItems.Single(line => line.Rate == 0.303m).KWh, "14:00 and 18:00 summer weekday energy is on-peak");
        Equal(5.74m, june.TotalCost, "June monthly rounding");
        Equal(3.03m, estimate.Months[1].TotalCost, "the Overnight console schedule charges the regular peak rate on July 4");
        Equal(1.42m, estimate.Months[2].TotalCost, "October weekday afternoon is off-peak and midnight is super off-peak");
    }

    public static async Task ViewModelScenarios()
    {
        using var files = new TestFiles();
        var path = files.Csv("shift.csv", "Hour,kWh\n2025-07-01 17:00,8\n2025-07-01 23:00,2\n");
        using var vm = new MainViewModel(() => Task.FromResult<string?>(path),
            new AppController(new DataManager(files.StorageDirectory), new RateCalculator()));
        await vm.ImportCommand.ExecuteAsync();

        vm.SelectedPlan = vm.Plans.Single(plan => plan.PlanId == "smart-usage");
        Equal("$100.72", vm.EstimatedCostText, "selected Smart Usage bill includes demand");
        if (!vm.SelectedBillBreakdown.Contains("$12.44/kW ", StringComparison.Ordinal))
            throw new InvalidOperationException("Smart Usage breakdown should show the demand rate per kW.");

        vm.ReductionPercent = 0m;
        vm.ShiftPercent = 50m;
        vm.ShiftHours = 6;
        await vm.RunScenarioCommand.ExecuteAsync();
        Equal("$75.32", vm.ScenarioCostText, "Smart Usage scenario recalculates the maximum hourly demand");
        Equal("$25.40", vm.ScenarioSavingsText, "Smart Usage savings include reduced demand");

        vm.SelectedPlan = vm.Plans.Single(plan => plan.PlanId == "overnight-advantage");
        Equal("$2.47", vm.EstimatedCostText, "selected Overnight Advantage bill uses energy tiers");
        if (!vm.SelectedBillBreakdown.Contains("$2.424", StringComparison.Ordinal))
            throw new InvalidOperationException("The breakdown should show sub-cent line amounts before the subtotal is rounded.");
        Equal("$1.35", vm.ScenarioCostText, "same scenario is priced under Overnight Advantage");
        Equal("$1.12", vm.ScenarioSavingsText, "shifting to the overnight window saves energy charges");
        Equal("Smart Usage", vm.ScenarioHistory.Single().PlanName, "scenario history names the plan used for its saved cost");
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message}: expected {expected}, got {actual}");
    }
}
