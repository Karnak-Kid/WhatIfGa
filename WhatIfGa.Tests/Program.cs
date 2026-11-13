using System.Diagnostics;
using System.Globalization;
using WhatIfGa.Core.Models;
using WhatIfGa.Core.Services;
using WhatIfGa.Services;
using WhatIfGa.ViewModels;

// Dependency-free regression suite: exits nonzero when an assertion fails.
var tests = new (string Name, Func<Task> Run)[]
{
    ("Summer tier boundaries and winter flat price", () => RunSync(TestRateBoundaries)),
    ("Calendar months reset tiers and select seasons", () => RunSync(TestMonthBoundaries)),
    ("Monthly currency rounding preserves sub-cent intervals", () => RunSync(TestRounding)),
    ("All seven plans are registered; placeholders cannot win", () => RunSync(TestRegistry)),
    ("Scenario reduction and shifting conserve expected energy", () => RunSync(TestScenario)),
    ("Shifting merges collisions and preserves calendar days", () => RunSync(TestShiftCollisions)),
    ("Malformed scenario settings are rejected", () => RunSync(TestInvalidScenario)),
    ("Both CSV layouts and quoted fields import correctly", TestCsvLayouts),
    ("Invalid CSV rows are rejected with useful errors", TestInvalidCsv),
    ("Missing intervals warn without inventing energy", TestCsvGaps),
    ("CSV numbers are independent of the OS culture", TestCsvCulture),
    ("Local persistence roundtrips and rejects corrupt data", TestPersistence),
    ("Import and calculations honor cancellation", TestCancellation),
    ("First-run demo remains unsaved and initializes the view model", TestViewModelDemo),
    ("GUI import replaces valid history and rejects invalid history safely", TestViewModelImport),
    ("GUI scenarios retain independent savings and session history", TestViewModelScenarios),
    ("Sparse scenario charts expose newly created hours", TestSparseScenarioChart),
    ("Year 9999 forecast falls back without throwing", TestForecastUpperDate),
    ("Reloaded partial history restores coverage notices", TestReloadedCoverageWarnings),
    ("Georgia Power CSV preambles and Hour timestamps import safely", ImportRegressionTests.GeorgiaPowerCsv),
    ("Georgia Power row errors preserve original line numbers", ImportRegressionTests.GeorgiaPowerRowErrors),
    ("XLSX inline/shared strings and worksheet relationships import", ImportRegressionTests.XlsxStringCells),
    ("Excel serial dates use the correct workbook date system", ImportRegressionTests.XlsxSerialDates),
    ("Invalid or executable workbook content is rejected", ImportRegressionTests.XlsxInvalidContent),
    ("XLSX cancellation and rejected imports preserve local history", ImportRegressionTests.XlsxPersistence),
    ("GUI imports XLSX through the normal comparison workflow", ImportRegressionTests.ViewModelXlsx),
    ("Eastern coverage skips the spring-forward hour and reports real gaps", () => RunSync(BillingRegressionTests.EasternCoverage)),
    ("Actual billing periods use exclusive ends and billing-month rates", () => RunSync(BillingRegressionTests.ActualPeriods)),
    ("Estimated closing dates clamp to short months", () => RunSync(BillingRegressionTests.EstimatedClosingDates)),
    ("Billing coverage distinguishes expected hours from stored energy", () => RunSync(BillingRegressionTests.BillingCoverage)),
    ("Selecting an unavailable plan clears the bill detail", BillingRegressionTests.ViewModelPlanSelection),
    ("Billing schedules persist and drive scenario estimates", BillingRegressionTests.ViewModelBillingSettings),
    ("CSV bill history sorts unique bill dates and skips payments", () => RunSync(BillHistoryRegressionTests.CsvPeriods)),
    ("Bill history rejects invalid dates and insufficient bill dates", () => RunSync(BillHistoryRegressionTests.RejectsInvalidHistory)),
    ("XLSX bill history accepts Excel date cells", () => RunSync(BillHistoryRegressionTests.XlsxPeriods)),
    ("Bill history import stages dates before applying the schedule", BillHistoryRegressionTests.ViewModelStagesHistory),
    ("A full year compares within five seconds", () => RunSync(TestAnnualPerformance)),
};

var failed = 0;
foreach (var (name, run) in tests)
{
    try
    {
        await run();
        Console.WriteLine($"PASS  {name}");
    }
    catch (Exception exception)
    {
        failed++;
        Console.Error.WriteLine($"FAIL  {name}: {exception}");
    }
}

var regressionFailures = failed;
if (args.Length > 0)
{
    if (args.Length == 3 && args[0] == "--verify-georgia-power")
    {
        try { await ImportRegressionTests.VerifySuppliedFiles(args[1], args[2]); }
        catch (Exception error)
        {
            failed++;
            Console.Error.WriteLine("FAIL  Supplied-file parity: " + error.Message);
        }
    }
    else if (args.Length == 2 && args[0] == "--verify-bill-history")
    {
        try { BillHistoryRegressionTests.VerifySuppliedFile(args[1]); }
        catch (Exception error)
        {
            failed++;
            Console.Error.WriteLine("FAIL  Supplied bill history: " + error.Message);
        }
    }
    else
    {
        failed++;
        Console.Error.WriteLine("Usage: WhatIfGa.Tests [--verify-georgia-power <csv-path> <xlsx-path> | --verify-bill-history <path>]");
    }
}
Console.WriteLine($"\n{tests.Length - regressionFailures}/{tests.Length} checks passed.");
return failed == 0 ? 0 : 1;

static Task RunSync(Action action)
{
    action();
    return Task.CompletedTask;
}

static void TestRateBoundaries()
{
    var plan = new ResidentialServicePlan();
    foreach (var (usage, expected) in new (decimal Usage, decimal Expected)[]
             { (0m, 0m), (650m, 57.20m), (1000m, 108.30m), (1200m, 138.50m) })
    {
        var bill = plan.CalculateCost([new(new DateTime(2025, 7, 1), usage)]);
        Equal(expected, bill.TotalCost, $"summer {usage} kWh");
        Equal(usage, bill.TotalKWh, "bill energy");
        Equal(expected, bill.Months.Sum(month => month.TotalCost), "monthly totals");
    }

    Equal(98.40m, plan.CalculateCost([new(new DateTime(2025, 1, 1), 1200m)]).TotalCost,
        "winter has no summer tiers");
    var tierBill = plan.CalculateCost([new(new DateTime(2025, 8, 1), 1200m)]);
    var positiveLines = tierBill.Months.Single().LineItems.Where(line => line.KWh > 0m).ToArray();
    Equal(3, positiveLines.Length, "summer breakdown has three populated tiers");
    Equal(650m, positiveLines[0].KWh, "first tier quantity");
    Equal(350m, positiveLines[1].KWh, "second tier quantity");
    Equal(200m, positiveLines[2].KWh, "third tier quantity");
    Check(tierBill.GetBreakdown().Length > 0, "human-readable breakdown is empty");
}

static void TestMonthBoundaries()
{
    var plan = new ResidentialServicePlan();
    var summerMonths = plan.CalculateCost([
        new(new DateTime(2025, 6, 30, 23, 0, 0), 600m),
        new(new DateTime(2025, 7, 1, 0, 0, 0), 600m)]);
    Equal(105.60m, summerMonths.TotalCost, "tiers restart each calendar month");
    Equal(2, summerMonths.Months.Count, "two billing months");

    var summerStart = plan.CalculateCost([
        new(new DateTime(2025, 5, 31, 23, 0, 0), 600m),
        new(new DateTime(2025, 6, 1, 0, 0, 0), 1200m)]);
    Equal(187.70m, summerStart.TotalCost, "May winter / June summer");
    var winterStart = plan.CalculateCost([
        new(new DateTime(2025, 9, 30, 23, 0, 0), 1000m),
        new(new DateTime(2025, 10, 1, 0, 0, 0), 1000m)]);
    Equal(190.30m, winterStart.TotalCost, "September summer / October winter");
}

static void TestRounding()
{
    var plan = new ResidentialServicePlan();
    Equal(0.21m, plan.CalculateCost([new(new DateTime(2025, 1, 1), 2.5m)]).TotalCost,
        "midpoint rounds away from zero");
    Equal(0.01m, plan.CalculateCost([
        new(new DateTime(2025, 1, 1), 0.05m),
        new(new DateTime(2025, 1, 1, 1, 0, 0), 0.05m)]).TotalCost,
        "round after month aggregation, not each interval");
}

static void TestRegistry()
{
    var calculator = new RateCalculator();
    Equal(7, calculator.Plans.Count, "plan registry count");
    Equal(7, calculator.Plans.Select(plan => plan.PlanId).Distinct().Count(), "unique plan ids");
    Equal(1, calculator.Plans.Count(plan => plan.IsImplemented), "implemented plan count");
    var data = new UsageData[] { new(new DateTime(2025, 7, 1), 1200m) };
    var estimates = calculator.Compare(data);
    Equal(1, estimates.Count, "placeholders excluded from comparison");
    Equal("Residential Service", calculator.Recommend(estimates)!.PlanName, "eligible recommendation");
    Check(calculator.Recommend(Array.Empty<BillEstimate>()) is null, "empty estimates should not recommend");
    foreach (var placeholder in calculator.Plans.Where(plan => !plan.IsImplemented))
        Throws<NotSupportedException>(() => placeholder.CalculateCost(data), placeholder.Name);
}

static void TestScenario()
{
    var data = new UsageData[]
    {
        new(new DateTime(2025, 7, 1, 17, 0, 0), 10m),
        new(new DateTime(2025, 7, 1, 18, 0, 0), 20m),
        new(new DateTime(2025, 7, 1, 20, 0, 0), 5m),
    };
    var scenario = new UsageScenario
    {
        Name = "Evening savings", ReductionPercent = 20m, ShiftPercent = 50m,
        ShiftHours = 4, SourceStartHour = 17, SourceEndHour = 20,
    };
    var changed = scenario.Apply(data);
    Equal(28m, changed.Sum(row => row.KWh), "20 percent reduction");
    Equal(35m, data.Sum(row => row.KWh), "original data remains untouched");
    Equal(4m, changed.Single(row => row.Timestamp.Hour == 17).KWh, "retained source energy");
    Equal(4m, changed.Single(row => row.Timestamp.Hour == 21).KWh, "shifted reduced energy");
    Equal(4m, changed.Single(row => row.Timestamp.Hour == 20).KWh, "exclusive window end");
    Check(changed.Zip(changed.Skip(1)).All(pair => pair.First.Timestamp < pair.Second.Timestamp),
        "scenario output must be sorted and unique");

    var plan = new ResidentialServicePlan();
    var shiftOnly = new UsageScenario
    {
        ReductionPercent = 0m, ShiftPercent = 100m, ShiftHours = 6,
        SourceStartHour = 17, SourceEndHour = 20,
    }.Apply(data);
    Equal(plan.CalculateCost(data).TotalCost, plan.CalculateCost(shiftOnly).TotalCost,
        "Residential Service has no time-of-day savings");
}

static void TestShiftCollisions()
{
    var data = new UsageData[]
    {
        new(new DateTime(2025, 12, 31, 18, 0, 0), 10m),
        new(new DateTime(2025, 12, 31, 0, 0, 0), 3m),
    };
    var changed = new UsageScenario
    {
        ReductionPercent = 0m, ShiftPercent = 100m, ShiftHours = 6,
        SourceStartHour = 17, SourceEndHour = 20,
    }.Apply(data);
    Equal(13m, changed.Sum(row => row.KWh), "shift conservation");
    Equal(13m, changed.Single(row => row.Timestamp.Hour == 0).KWh, "collision merged");
    Check(changed.All(row => row.Timestamp.Date == new DateTime(2025, 12, 31)),
        "shifting must preserve the original calendar day and year");
}

static void TestInvalidScenario()
{
    UsageData[] data = [new(new DateTime(2025, 1, 1), 1m)];
    Throws<ArgumentException>(() => new UsageScenario { ReductionPercent = 101m }.Apply(data), "reduction above 100");
    Throws<ArgumentException>(() => new UsageScenario { ReductionPercent = -1m }.Apply(data), "negative reduction");
    Throws<ArgumentException>(() => new UsageScenario { ShiftPercent = 101m }.Apply(data), "shift above 100");
    Throws<ArgumentException>(() => new UsageScenario { SourceStartHour = 25 }.Apply(data), "invalid source hour");
}

static async Task TestCsvLayouts()
{
    using var files = new TestFiles();
    var manager = new DataManager(files.StorageDirectory);
    var timestamp = await manager.ImportCsvAsync(files.Csv("timestamp.csv",
        "Timestamp,kWh,Temp\n\"2025-06-01 00:00\",1.25,73\n2025-06-01 01:00,0.75,\n"));
    Check(timestamp.IsValid, string.Join("; ", timestamp.Errors));
    Equal(2, timestamp.Data.Count, "timestamp record count");
    Equal(2m, timestamp.Data.Sum(row => row.KWh), "timestamp usage");
    Equal<decimal?>(null, timestamp.Data[1].Temp, "optional temperature");

    var components = manager.ValidateCsv(files.Csv("parts.csv",
        "Year,Month,Day,Hour,kWh,Temp\n2025,10,1,0,1.25,63\n2025,10,1,1,0.75,62\n"));
    Check(components.IsValid, string.Join("; ", components.Errors));
    Equal(timestamp.Data[0].KWh, components.Data[0].KWh, "both layouts preserve decimals");
    Equal(new DateTime(2025, 10, 1), components.Data[0].Timestamp, "component date");
}

static async Task TestInvalidCsv()
{
    using var files = new TestFiles();
    var manager = new DataManager(files.StorageDirectory);
    var invalid = new Dictionary<string, string>
    {
        ["negative"] = "Timestamp,kWh\n2025-01-01 00:00,-1\n",
        ["nan"] = "Timestamp,kWh\n2025-01-01 00:00,NaN\n",
        ["infinity"] = "Timestamp,kWh\n2025-01-01 00:00,Infinity\n",
        ["duplicate"] = "Timestamp,kWh\n2025-01-01 00:00,1\n2025-01-01 00:00,2\n",
        ["missing kWh"] = "Timestamp,Temp\n2025-01-01 00:00,70\n",
        ["invalid date"] = "Year,Month,Day,Hour,kWh\n2025,2,30,0,1\n",
        ["invalid hour"] = "Year,Month,Day,Hour,kWh\n2025,1,1,24,1\n",
        ["missing field"] = "Timestamp,kWh\n2025-01-01 00:00\n",
        ["unterminated quote"] = "Timestamp,kWh\n\"2025-01-01 00:00,1\n",
        ["empty file"] = "",
        ["header only"] = "Timestamp,kWh\n",
        ["over a year"] = "Timestamp,kWh\n2024-01-01 00:00,1\n2025-02-01 00:00,1\n",
    };
    foreach (var (name, csv) in invalid)
    {
        var result = await manager.ImportCsvAsync(files.Csv(Guid.NewGuid() + ".csv", csv));
        Check(!result.IsValid, $"{name} was incorrectly accepted");
        Check(result.Errors.Count > 0, $"{name} has no error explanation");
    }
    var nonexistent = manager.ValidateCsv(Path.Combine(files.Root, "missing.csv"));
    Check(!nonexistent.IsValid && nonexistent.Errors.Count > 0, "missing file must be a validation error");
}

static Task TestCsvGaps()
{
    using var files = new TestFiles();
    var result = new DataManager(files.StorageDirectory).ValidateCsv(files.Csv("gaps.csv",
        "Timestamp,kWh\n2025-01-01 00:00,1\n2025-01-01 02:00,2\n"));
    Check(result.IsValid, string.Join("; ", result.Errors));
    Check(result.Warnings.Count > 0, "missing intervals should be visible");
    Equal(2, result.Data.Count, "no fabricated interval");
    Equal(3m, result.Data.Sum(row => row.KWh), "no fabricated energy");
    return Task.CompletedTask;
}

static Task TestCsvCulture()
{
    using var files = new TestFiles();
    var originalCulture = CultureInfo.CurrentCulture;
    try
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        var manager = new DataManager(files.StorageDirectory);
        var result = manager.ValidateCsv(files.Csv("invariant.csv",
            "Timestamp,kWh\n2025-01-01 00:00,1.25\n"));
        Check(result.IsValid, string.Join("; ", result.Errors));
        Equal(1.25m, result.Data.Single().KWh, "CSV decimal point is invariant");
        var ambiguous = manager.ValidateCsv(files.Csv("ambiguous.csv",
            "Timestamp,kWh\n2025-01-01 00:00,\"1,25\"\n"));
        Check(!ambiguous.IsValid, "ambiguous decimal comma cannot silently become 125 kWh");
    }
    finally
    {
        CultureInfo.CurrentCulture = originalCulture;
    }
    return Task.CompletedTask;
}

static async Task TestPersistence()
{
    using var files = new TestFiles();
    var manager = new DataManager(files.StorageDirectory);
    UsageData[] original = [new(new DateTime(2025, 1, 1), 1.25m, 63m), new(new DateTime(2025, 1, 1, 1, 0, 0), 0.75m)];
    await manager.SaveAsync(original);
    var loaded = await new DataManager(files.StorageDirectory).LoadAsync();
    Check(original.SequenceEqual(loaded), "stored usage must roundtrip exactly");
    Check(File.Exists(manager.DatabasePath), "local data file not created");
    var stored = await File.ReadAllTextAsync(manager.DatabasePath);
    var invalidImport = await manager.ImportCsvAsync(files.Csv("invalid.csv", "Timestamp,kWh\n2025-01-01 00:00,-1\n"));
    Check(!invalidImport.IsValid, "invalid import must be rejected");
    Equal(stored, await File.ReadAllTextAsync(manager.DatabasePath), "invalid import preserves saved history");
    await File.WriteAllTextAsync(manager.DatabasePath, "{broken json");
    await ThrowsAsync<InvalidDataException>(async () => await manager.LoadAsync(), "corrupt storage is not valid usage");
    foreach (var (name, json) in new Dictionary<string, string>
    {
        ["unsupported version"] = "{\"schemaVersion\":99,\"records\":[{\"timestamp\":\"2025-01-01T00:00:00\",\"kWh\":1}]}",
        ["missing version"] = "{\"records\":[{\"timestamp\":\"2025-01-01T00:00:00\",\"kWh\":1}]}",
        ["negative persisted usage"] = "{\"schemaVersion\":1,\"records\":[{\"timestamp\":\"2025-01-01T00:00:00\",\"kWh\":-1}]}",
        ["duplicate persisted timestamp"] = "{\"schemaVersion\":1,\"records\":[{\"timestamp\":\"2025-01-01T00:00:00\",\"kWh\":1},{\"timestamp\":\"2025-01-01T00:00:00\",\"kWh\":2}]}",
    })
    {
        await File.WriteAllTextAsync(manager.DatabasePath, json);
        await ThrowsAsync<InvalidDataException>(async () => await manager.LoadAsync(), name);
    }
}

static async Task TestCancellation()
{
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    using var files = new TestFiles();
    var manager = new DataManager(files.StorageDirectory);
    var path = files.Csv("cancelled.csv", "Timestamp,kWh\n2025-01-01 00:00,1\n");
    await ThrowsAsync<OperationCanceledException>(async () => await manager.ImportCsvAsync(path, cancellation.Token), "cancelled import");
    Throws<OperationCanceledException>(() => new RateCalculator().Compare(SampleDataService.CreateYear(), cancellation.Token),
        "cancelled comparison");
    await ThrowsAsync<OperationCanceledException>(async () => await manager.SaveAsync([new(new DateTime(2025, 1, 1), 1m)], cancellation.Token),
        "cancelled save");
}

static async Task TestViewModelDemo()
{
    using var files = new TestFiles();
    var manager = new DataManager(files.StorageDirectory);
    var controller = new AppController(manager, new RateCalculator());
    using var vm = new MainViewModel(() => Task.FromResult<string?>(null), controller);
    await vm.InitializeAsync();
    Check(vm.HasData && !vm.IsBusy && !vm.IsStatusError, "first-run initialization must finish with usable data");
    Equal(8760, controller.CurrentData.Count, "first-run synthetic year");
    Equal(12, vm.MonthlyBills.Count, "first-run monthly estimates");
    Check(vm.DataSource.Contains("demo", StringComparison.OrdinalIgnoreCase), "synthetic data must be labeled");
    Check(!File.Exists(manager.DatabasePath), "demo data must not be persisted as personal history");
    await vm.InitializeAsync();
    Equal(8760, controller.CurrentData.Count, "initialization is idempotent");
}

static async Task TestViewModelImport()
{
    using var files = new TestFiles();
    var nextPath = files.Csv("july.csv", "Timestamp,kWh\n2025-07-01 17:00,1200\n");
    var controller = new AppController(new DataManager(files.StorageDirectory), new RateCalculator());
    using var vm = new MainViewModel(() => Task.FromResult<string?>(nextPath), controller);
    await vm.ImportCommand.ExecuteAsync();
    Check(vm.HasData && !vm.IsStatusError && !vm.IsBusy, "valid GUI import must complete");
    Equal("$138.50", vm.EstimatedCostText, "imported baseline estimate");
    vm.ReductionPercent = 10m;
    vm.ShiftPercent = 0m;
    await vm.RunScenarioCommand.ExecuteAsync();
    Check(vm.HasScenario && vm.ScenarioHistory.Count == 1, "scenario should exist before replacing data");

    nextPath = files.Csv("october.csv", "Timestamp,kWh\n2025-10-01 00:00,1000\n");
    await vm.ImportCommand.ExecuteAsync();
    Equal("$82.00", vm.EstimatedCostText, "new import replaces the baseline");
    Equal(1000m, controller.CurrentData.Sum(row => row.KWh), "new import energy");
    Check(!vm.HasScenario && vm.ScenarioHistory.Count == 0, "new data must clear scenarios from old data");
    Equal(1, vm.MonthlyBills.Count, "old months must be cleared");
    var source = vm.DataSource;

    vm.SelectedTabIndex = 2;
    nextPath = files.Csv("invalid.csv", "Timestamp,kWh\n2025-10-01 00:00,-1000\n");
    await vm.ImportCommand.ExecuteAsync();
    Check(vm.IsStatusError && !vm.IsBusy, "invalid GUI import should be reported without leaving a busy UI");
    Equal("$82.00", vm.EstimatedCostText, "invalid import retains the last estimate");
    Equal(source, vm.DataSource, "invalid import retains data provenance");
    Equal(1000m, controller.CurrentData.Sum(row => row.KWh), "invalid import retains original data");
    Check(vm.ValidationMessages.Any(message => message.StartsWith("Error", StringComparison.Ordinal)), "validation errors are visible");
    Check(vm.IsImportReportOpen, "a rejected import opens its report without navigating away");
    Equal(2, vm.SelectedTabIndex, "a rejected import preserves the current tab");
}

static async Task TestViewModelScenarios()
{
    using var files = new TestFiles();
    var path = files.Csv("july.csv", "Timestamp,kWh\n2025-07-01 17:00,1200\n");
    var controller = new AppController(new DataManager(files.StorageDirectory), new RateCalculator());
    using var vm = new MainViewModel(() => Task.FromResult<string?>(path), controller);
    await vm.ImportCommand.ExecuteAsync();
    vm.ScenarioName = "Ten percent";
    vm.ReductionPercent = 10m;
    vm.ShiftPercent = 0m;
    await vm.RunScenarioCommand.ExecuteAsync();
    Equal("$120.38", vm.ScenarioCostText, "first scenario modified cost");
    Equal("$18.12", vm.ScenarioSavingsText, "first scenario savings");
    vm.ScenarioName = "Twenty percent";
    vm.ReductionPercent = 20m;
    await vm.RunScenarioCommand.ExecuteAsync();
    Equal("$102.46", vm.ScenarioCostText, "second scenario uses original, not previously reduced data");
    Equal("$36.04", vm.ScenarioSavingsText, "second scenario savings");
    Equal("$138.50", vm.EstimatedCostText, "scenario preserves original estimate");
    Equal(1200m, controller.CurrentData.Sum(row => row.KWh), "scenario preserves imported history");
    Equal(2, vm.ScenarioHistory.Count, "multiple scenarios retained this session");
    Equal("Twenty percent", vm.ScenarioHistory[0].Name, "latest scenario first");
    Equal("$120.38", vm.ScenarioHistory[1].CostText, "prior scenario result remains intact");

    vm.ReductionPercent = 101m;
    await vm.RunScenarioCommand.ExecuteAsync();
    Check(vm.IsStatusError && !vm.IsBusy, "invalid scenario is reported without crashing");
    Equal(2, vm.ScenarioHistory.Count, "invalid scenario is not added to history");
    Equal("$102.46", vm.ScenarioCostText, "invalid scenario retains previous successful result");
}

static async Task TestSparseScenarioChart()
{
    using var files = new TestFiles();
    var path = files.Csv("sparse.csv", "Timestamp,kWh\n2025-07-01 17:00,10\n");
    var controller = new AppController(new DataManager(files.StorageDirectory), new RateCalculator());
    using var vm = new MainViewModel(() => Task.FromResult<string?>(path), controller);
    await vm.ImportCommand.ExecuteAsync();
    vm.ReductionPercent = 0m;
    vm.ShiftPercent = 100m;
    vm.ShiftHours = 6;
    vm.SourceStartHour = 17;
    vm.SourceEndHour = 18;
    await vm.RunScenarioCommand.ExecuteAsync();
    vm.ChartModeIndex = 1;
    var destination = vm.ChartPoints.Single(point => point.Label == "23:00");
    Check(!destination.OriginalAvailable, "newly populated scenario hours must not imply a recorded original reading");
    Equal<double?>(10d, destination.Modified, "shift destination must appear in the chart");
    var source = vm.ChartPoints.Single(point => point.Label == "17:00");
    Check(source.OriginalAvailable, "recorded source hour remains available");
    Equal(10d, source.Original, "original source usage");
    Equal<double?>(0d, source.Modified, "source shifted to zero");
    Equal("$0.00", vm.ScenarioSavingsText, "shift-only Residential Service savings");
}

static async Task TestForecastUpperDate()
{
    using var files = new TestFiles();
    var path = files.Csv("last-year.csv", "Timestamp,kWh\n9999-12-01 00:00,1.25\n");
    var controller = new AppController(new DataManager(files.StorageDirectory), new RateCalculator());
    using var vm = new MainViewModel(() => Task.FromResult<string?>(path), controller);
    await vm.ImportCommand.ExecuteAsync();
    Check(!vm.IsStatusError, vm.StatusMessage);
    vm.ChartModeIndex = 3;
    Equal(1, vm.ChartPoints.Count, "upper-date forecast retains a usable estimate");
    Check(vm.ChartSubtitle.Contains("9999", StringComparison.Ordinal), "upper-date fallback should be explained");
    Equal("$0.10", vm.EstimatedCostText, "upper-date original cost remains valid");
}

static async Task TestReloadedCoverageWarnings()
{
    using var files = new TestFiles();
    var manager = new DataManager(files.StorageDirectory);
    await manager.SaveAsync([
        new(new DateTime(2025, 1, 1, 0, 0, 0), 1m),
        new(new DateTime(2025, 1, 1, 2, 0, 0), 2m)]);
    var controller = new AppController(manager, new RateCalculator());
    using var vm = new MainViewModel(() => Task.FromResult<string?>(null), controller);
    await vm.InitializeAsync();
    Equal(2, controller.CurrentData.Count, "saved partial readings are restored");
    Check(!vm.IsStatusError, vm.StatusMessage);
    Check(vm.ValidationMessages.Any(message => message.Contains("missing", StringComparison.OrdinalIgnoreCase)),
        "reloading must not lose missing-hour warnings");
    Check(vm.ValidationMessages.Any(message => message.Contains("incomplete", StringComparison.OrdinalIgnoreCase)),
        "reloading must not lose partial-month warnings");
    Check(vm.DataSource.Contains("restored", StringComparison.OrdinalIgnoreCase), "restored provenance");
}

static void TestAnnualPerformance()
{
    var year = SampleDataService.CreateYear();
    Equal(8760, year.Count, "non-leap demo year hourly count");
    var calculator = new RateCalculator();
    calculator.Compare(year); // JIT warmup; the measured operation is calculation, not SDK startup.
    var watch = Stopwatch.StartNew();
    var estimates = calculator.Compare(year);
    watch.Stop();
    Equal(1, estimates.Count, "implemented annual estimate count");
    Equal(12, estimates.Single().Months.Count, "annual monthly bill count");
    Equal(year.Sum(row => row.KWh), estimates.Single().TotalKWh, "annual usage conservation");
    Check(watch.Elapsed < TimeSpan.FromSeconds(5), $"comparison took {watch.Elapsed.TotalSeconds:F3}s");
    Console.WriteLine($"      Annual comparison: {watch.Elapsed.TotalMilliseconds:F1} ms; 8,760 records; 1 implemented / 7 registered plans.");
}

static void Equal<T>(T expected, T actual, string context)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{context}: expected {expected}, got {actual}");
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void Throws<T>(Action action, string context) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"{context}: expected {typeof(T).Name}");
}

static async Task ThrowsAsync<T>(Func<Task> action, string context) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"{context}: expected {typeof(T).Name}");
}

sealed class TestFiles : IDisposable
{
    private readonly string _parent = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), ".build", "tests"));
    public string Root { get; }
    public string StorageDirectory => Path.Combine(Root, "storage");

    public TestFiles()
    {
        Root = Path.Combine(_parent, "WhatIfGa-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Csv(string name, string contents)
    {
        var path = Path.Combine(Root, name);
        File.WriteAllText(path, contents);
        return path;
    }

    public void Dispose()
    {
        var root = Path.GetFullPath(Root);
        var parent = _parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (root.StartsWith(parent, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(root).StartsWith("WhatIfGa-tests-", StringComparison.Ordinal))
            Directory.Delete(root, recursive: true);
    }
}
