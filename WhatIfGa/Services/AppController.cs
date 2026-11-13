using System.Diagnostics;
using WhatIfGa.Core.Models;
using WhatIfGa.Core.Services;

namespace WhatIfGa.Services;

/// <summary>
/// Application use cases from the supplied class diagram. The view model calls this
/// service; it has no dependency on a Window, dispatcher, or visual control.
/// </summary>
public sealed class AppController(DataManager dataManager, RateCalculator rateCalculator) : IDisposable
{
    private CancellationTokenSource? _operation;
    private readonly BillingScheduleStore _billingStore = new(Path.GetDirectoryName(dataManager.DatabasePath)!);
    public IReadOnlyList<UsageData> CurrentData { get; private set; } = Array.Empty<UsageData>();
    public BillingSchedule CurrentBillingSchedule { get; private set; } = BillingSchedule.Default;
    public IReadOnlyList<RatePlan> Plans => rateCalculator.Plans;
    public string DatabasePath => dataManager.DatabasePath;

    public async Task<bool> LoadAsync()
    {
        var token = BeginOperation();
        CurrentData = await dataManager.LoadAsync(token);
        return CurrentData.Count > 0;
    }

    /// <summary>Legacy entry point retained for the class-diagram API.</summary>
    public Task<CsvValidationResult> ImportCsv(string path) => ImportFileAsync(path);

    public async Task<CsvValidationResult> ImportFileAsync(string path)
    {
        var token = BeginOperation();
        var validation = await dataManager.ImportFileAsync(path, token);
        if (validation.IsValid) CurrentData = validation.Data;
        return validation;
    }

    public async Task UseDemoAsync()
    {
        var token = BeginOperation();
        CurrentData = await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            return SampleDataService.CreateYear();
        }, token);
    }

    public Task<ComparisonResult> ComparePlans()
    {
        var token = BeginOperation();
        var snapshot = CurrentData;
        var schedule = CurrentBillingSchedule;
        return Task.Run(() => Compare(snapshot, schedule, token), token);
    }

    public async Task LoadBillingScheduleAsync()
    {
        CurrentBillingSchedule = await _billingStore.LoadAsync(BeginOperation());
    }

    public Task<BillHistoryImportResult> ReadBillHistoryAsync(string path)
    {
        var token = BeginOperation();
        return Task.Run(() => BillHistoryImporter.Read(path, token), token);
    }

    public async Task<ComparisonResult> ApplyBillingScheduleAsync(BillingSchedule schedule)
    {
        var token = BeginOperation();
        var validated = BillingCalendar.Validate(schedule);
        var snapshot = CurrentData;
        var comparison = await Task.Run(() => Compare(snapshot, validated, token), token);
        await _billingStore.SaveAsync(validated, token);
        CurrentBillingSchedule = validated;
        return comparison;
    }

    private ComparisonResult Compare(IReadOnlyList<UsageData> data, BillingSchedule schedule, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        var estimates = rateCalculator.Compare(data, schedule, token);
        return new ComparisonResult(estimates, rateCalculator.Recommend(estimates), watch.Elapsed);
    }

    public Task<ScenarioResult> RunScenario(UsageScenario scenario)
    {
        var token = BeginOperation();
        var snapshot = CurrentData;
        var schedule = CurrentBillingSchedule;
        return Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            var periods = BillingCalendar.Build(snapshot, schedule);
            var original = rateCalculator.Compare(snapshot, periods, token);
            var modifiedData = scenario.Apply(snapshot, token);
            token.ThrowIfCancellationRequested();
            var modified = rateCalculator.Compare(modifiedData, periods, token);
            scenario.OriginalCosts = original;
            scenario.ModifiedCosts = modified;
            return new ScenarioResult(scenario, modifiedData, original, modified);
        }, token);
    }

    public void Cancel() => _operation?.Cancel();

    private CancellationToken BeginOperation()
    {
        _operation?.Dispose();
        _operation = new CancellationTokenSource();
        return _operation.Token;
    }

    public void Dispose() { Cancel(); _operation?.Dispose(); }
}

public sealed record ComparisonResult(IReadOnlyList<BillEstimate> Estimates, BillEstimate? Recommendation, TimeSpan Elapsed);
public sealed record ScenarioResult(UsageScenario Scenario, IReadOnlyList<UsageData> Data,
    IReadOnlyList<BillEstimate> OriginalCosts, IReadOnlyList<BillEstimate> ModifiedCosts);
