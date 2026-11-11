using WhatIfGa.Core.Models;
using WhatIfGa.Core.Services;
using WhatIfGa.Services;
using WhatIfGa.ViewModels;

internal static class BillingRegressionTests
{
    public static void EasternCoverage()
    {
        Equal(23, UsageCoverage.CountExpectedHours(new DateOnly(2026, 3, 8), new DateOnly(2026, 3, 9)),
            "spring-forward day has 23 unique expected hour labels");
        Equal(24, UsageCoverage.CountExpectedHours(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 2)),
            "fall-back day expects each unique local hour label once");

        var marchStart = new DateTime(2026, 3, 1);
        var skippedClockHour = new DateTime(2026, 3, 8, 2, 0, 0);
        var completeMarch = Enumerable.Range(0, 31 * 24)
            .Select(offset => marchStart.AddHours(offset))
            .Where(timestamp => timestamp != skippedClockHour)
            .Select(timestamp => new UsageData(timestamp, 1m))
            .ToArray();
        var completeNotices = DataManager.GetCoverageWarnings(completeMarch);
        Check(!completeNotices.Any(MissingIntervalNotice), "the spring-forward gap cannot count as lost usage");
        Check(!completeNotices.Any(IncompleteMonthNotice), "743 expected March rows form a complete calendar month");

        var lostReading = new DateTime(2026, 3, 8, 4, 0, 0);
        var incompleteMarch = completeMarch.Where(row => row.Timestamp != lostReading).ToArray();
        var incompleteNotices = DataManager.GetCoverageWarnings(incompleteMarch);
        Check(incompleteNotices.Any(notice => notice.StartsWith("1 hourly interval(s) are missing", StringComparison.Ordinal)),
            "the actual missing hour beside the DST transition must still be reported");
        Check(incompleteNotices.Any(IncompleteMonthNotice), "a real missing hour makes March incomplete");
    }

    public static void ActualPeriods()
    {
        var june = new BillingPeriod(new DateOnly(2025, 5, 20), new DateOnly(2025, 6, 20), new DateOnly(2025, 6, 1));
        var july = new BillingPeriod(june.End, new DateOnly(2025, 7, 20), new DateOnly(2025, 7, 1));
        var schedule = new BillingSchedule(BillingMode.Actual, 1, [july, june]);
        UsageData[] usage =
        [
            new(new DateTime(2025, 5, 19, 23, 0, 0), 999m),
            new(new DateTime(2025, 5, 20), 200m),
            new(new DateTime(2025, 6, 19, 23, 0, 0), 500m),
            new(new DateTime(2025, 6, 20), 650m),
            new(new DateTime(2025, 7, 19, 23, 0, 0), 200m),
            new(new DateTime(2025, 7, 20), 900m),
        ];
        var calculator = new RateCalculator();
        var estimate = calculator.Compare(usage, schedule).Single();
        Equal(2, estimate.Months.Count, "actual periods produce two distinct bills");
        Equal(2, estimate.UnassignedHours, "rows before and at the last exclusive end are outside the schedule");
        Equal(june, estimate.Months[0].Period, "earlier period is billed first");
        Equal(700m, estimate.Months[0].KWh, "period includes its first hour and excludes next period's first hour");
        Equal(64.50m, estimate.Months[0].TotalCost, "June billing month selects summer rates for its May usage");
        Equal(july, estimate.Months[1].Period, "the boundary hour belongs to the July period");
        Equal(850m, estimate.Months[1].KWh, "the boundary hour is included exactly once");
        Equal(86.40m, estimate.Months[1].TotalCost, "summer tiers restart each billing period");
        Equal(150.90m, estimate.TotalCost, "bill total excludes uncovered usage");

        var reduced = new UsageScenario { ReductionPercent = 10m }.Apply(usage);
        var modifiedEstimate = calculator.Compare(reduced, schedule).Single();
        Equal(129.43m, modifiedEstimate.TotalCost, "scenario compares the same two actual periods");
        Equal(2, modifiedEstimate.UnassignedHours, "reduction preserves coverage of the original schedule");
    }

    public static void EstimatedClosingDates()
    {
        UsageData[] usage = [new(new DateTime(2025, 1, 31), 1m), new(new DateTime(2025, 2, 28), 1m)];
        var schedule = new BillingSchedule(BillingMode.Estimated, 31, []);
        var periods = BillingCalendar.Build(usage, schedule);
        Equal(2, periods.Count, "data spans two estimated periods");
        Equal(new BillingPeriod(new DateOnly(2025, 1, 31), new DateOnly(2025, 2, 28), new DateOnly(2025, 2, 1)),
            periods[0], "February close clamps to the last day of February");
        Equal(new BillingPeriod(new DateOnly(2025, 2, 28), new DateOnly(2025, 3, 31), new DateOnly(2025, 3, 1)),
            periods[1], "next period resumes normal closing day after February");
    }

    public static void BillingCoverage()
    {
        var spring = new DateOnly(2026, 3, 8);
        var period = new BillingPeriod(spring, spring.AddDays(1), new DateOnly(2026, 3, 1));
        var schedule = new BillingSchedule(BillingMode.Actual, 1, [period]);
        var complete = Enumerable.Range(0, 24).Where(hour => hour != 2)
            .Select(hour => new UsageData(spring.ToDateTime(new TimeOnly(hour, 0)), 1m))
            .ToArray();
        var calculator = new RateCalculator();
        var completeBill = calculator.Compare(complete, schedule).Single().Months.Single();
        Equal(23, completeBill.ExpectedHours, "actual billing period expected coverage accounts for DST");
        Equal(23, completeBill.RecordedHours, "all expected local hours were recorded");
        Check(!completeBill.IsPartial, "a complete spring-forward day is not marked partial");

        var incomplete = complete.Where(row => row.Hour != 4)
            .Append(new UsageData(spring.ToDateTime(new TimeOnly(2, 0)), 1m)).ToArray();
        var incompleteBill = calculator.Compare(incomplete, schedule).Single().Months.Single();
        Equal(22, incompleteBill.RecordedHours, "an invalid spring-forward label cannot fill a missing expected hour");
        Equal(23m, incompleteBill.KWh, "stored energy remains included in the estimate");
        Check(incompleteBill.IsPartial, "one expected reading is missing even with 23 stored rows");
    }

    public static async Task ViewModelPlanSelection()
    {
        using var files = new TestFiles();
        var path = files.Csv("july.csv", "Hour,kWh\n2025-07-01 17:00,1200\n");
        using var vm = new MainViewModel(() => Task.FromResult<string?>(path),
            new AppController(new DataManager(files.StorageDirectory), new RateCalculator()));
        await vm.ImportCommand.ExecuteAsync();
        var residential = vm.Plans.Single(plan => plan.PlanId == "residential-service");
        var unavailable = vm.Plans.Single(plan => plan.PlanId == "smart-usage");
        vm.SelectedPlan = unavailable;
        Equal("—", vm.EstimatedCostText, "unavailable plans have no estimate");
        Check(vm.MonthlyBills.Count == 0 && vm.SelectedMonth is null, "previous plan's bill cannot remain selected");
        Check(!string.IsNullOrWhiteSpace(vm.PlanSelectionMessage), "unavailable plan has an explanation");
        vm.SelectedPlan = residential;
        Equal("$138.50", vm.EstimatedCostText, "switching back restores the Residential Service estimate");
        Equal(1, vm.MonthlyBills.Count, "switching back restores bill details");
    }

    public static async Task ViewModelBillingSettings()
    {
        using var files = new TestFiles();
        var manager = new DataManager(files.StorageDirectory);
        await manager.SaveAsync([new UsageData(new DateTime(2025, 5, 25), 1200m)]);

        using (var vm = OpenViewModel())
        {
            await vm.InitializeAsync();
            Equal("$98.40", vm.EstimatedCostText, "default mode uses calendar May winter rates");
            vm.BillingModeIndex = 1;
            vm.BillingClosingDay = 15;
            await vm.ApplyBillingScheduleCommand.ExecuteAsync();
            Check(!vm.IsStatusError, vm.StatusMessage);
            Equal("$138.50", vm.EstimatedCostText, "May 25 belongs to estimated June billing period");
            vm.ReductionPercent = 10m;
            vm.ShiftPercent = 0m;
            await vm.RunScenarioCommand.ExecuteAsync();
            Equal("$120.38", vm.ScenarioCostText, "scenario uses the applied estimated cycle and summer tiers");
            Equal("$18.12", vm.ScenarioSavingsText, "savings compare against the same billing period");
        }

        using (var vm = OpenViewModel())
        {
            await vm.InitializeAsync();
            Equal(1, vm.BillingModeIndex, "estimated mode survives app restart");
            Equal(15, vm.BillingClosingDay, "estimated closing day survives app restart");
            Equal("$138.50", vm.EstimatedCostText, "restored closing date drives the calculation");
            vm.BillingModeIndex = 2;
            vm.BillingPeriodText = "2025-05-20,2025-06-20,2025-05";
            await vm.ApplyBillingScheduleCommand.ExecuteAsync();
            Check(!vm.IsStatusError, vm.StatusMessage);
            Equal("$98.40", vm.EstimatedCostText, "explicit May billing month selects winter rates");
        }

        using (var vm = OpenViewModel())
        {
            await vm.InitializeAsync();
            Equal(2, vm.BillingModeIndex, "actual mode survives app restart");
            Check(vm.BillingPeriodText.Contains("2025-05-20,2025-06-20,2025-05", StringComparison.Ordinal),
                "actual dates and billing month survive app restart");
            Equal("$98.40", vm.EstimatedCostText, "actual billing month remains effective after restart");

            vm.BillingPeriodText += "\n2025-06-01,2025-07-01,2025-07";
            await vm.ApplyBillingScheduleCommand.ExecuteAsync();
            Check(vm.IsStatusError, "an overlapping edit should be rejected");
            Equal("$98.40", vm.EstimatedCostText, "a rejected schedule leaves the previous estimate available");

            vm.BillingModeIndex = 0;
            await vm.ApplyBillingScheduleCommand.ExecuteAsync();
            Check(!vm.IsStatusError, vm.StatusMessage);
            Equal("2025-05-20,2025-06-20,2025-05", vm.BillingPeriodText, "calendar mode keeps saved actual dates");
        }

        MainViewModel OpenViewModel() => new(() => Task.FromResult<string?>(null),
            new AppController(new DataManager(files.StorageDirectory), new RateCalculator()));
    }

    private static bool MissingIntervalNotice(string notice) => notice.Contains("are missing between", StringComparison.Ordinal);
    private static bool IncompleteMonthNotice(string notice) => notice.StartsWith("Incomplete calendar month", StringComparison.Ordinal);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message}: expected {expected}, got {actual}");
    }
}
