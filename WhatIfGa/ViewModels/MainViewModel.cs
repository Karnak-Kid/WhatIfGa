using System.Collections.ObjectModel;
using System.Globalization;
using ReactiveUI;
using WhatIfGa.Commands;
using WhatIfGa.Controls;
using WhatIfGa.Core.Models;
using WhatIfGa.Core.Services;
using WhatIfGa.Services;

namespace WhatIfGa.ViewModels;

public sealed partial class MainViewModel : ViewModelBase, IDisposable
{
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");
    private readonly AppController _controller;
    private readonly Func<Task<string?>>? _selectUsageFile;
    private ComparisonResult? _comparison;
    private ScenarioResult? _scenario;
    private bool _isBusy, _isStatusError, _initialized, _isImportReportOpen;
    private string _statusMessage = "Load a CSV or Excel workbook, or explore a year of sample usage.", _dataSource = "No usage loaded";
    private string _validationSummary = "No file imported yet.";
    private string _scenarioName = "My energy strategy";
    private decimal _reductionPercent = 10, _shiftPercent = 50;
    private int _shiftHours = 5, _sourceStartHour = 14, _sourceEndHour = 19, _chartModeIndex, _selectedTabIndex;
    private MonthlyBillRowViewModel? _selectedMonth;
    private PlanRowViewModel? _selectedPlan;

    public MainViewModel() : this(null) { }

    public MainViewModel(Func<Task<string?>>? selectCsv, AppController? controller = null,
        Func<Task<string?>>? selectBillHistoryFile = null)
    {
        _selectUsageFile = selectCsv;
        _controller = controller ?? new AppController(new DataManager(), new RateCalculator());
        ImportCommand = new AsyncCommand(ImportAsync, () => !IsBusy);
        LoadDemoCommand = new AsyncCommand(LoadDemoAsync, () => !IsBusy);
        CompareCommand = new AsyncCommand(() => RunBusyAsync(CompareAsync), () => !IsBusy && HasData);
        RunScenarioCommand = new AsyncCommand(RunScenarioAsync, () => !IsBusy && HasData);
        ApplyBillingScheduleCommand = new AsyncCommand(ApplyBillingScheduleAsync, () => !IsBusy);
        ImportBillHistoryCommand = new AsyncCommand(() => ImportBillHistoryAsync(selectBillHistoryFile!),
            () => !IsBusy && selectBillHistoryFile is not null);
        CancelCommand = new RelayCommand(_controller.Cancel, () => IsBusy);
        ResetScenarioCommand = new RelayCommand(ResetScenario, () => !IsBusy);
        foreach (var plan in _controller.Plans) Plans.Add(new PlanRowViewModel(plan));
        _selectedPlan = Plans.FirstOrDefault(plan => plan.IsImplemented);
        SyncBillingEditor();
    }

    public AsyncCommand ImportCommand { get; }
    public AsyncCommand LoadDemoCommand { get; }
    public AsyncCommand CompareCommand { get; }
    public AsyncCommand RunScenarioCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ResetScenarioCommand { get; }
    public ObservableCollection<PlanRowViewModel> Plans { get; } = [];
    public ObservableCollection<MonthlyBillRowViewModel> MonthlyBills { get; } = [];
    public ObservableCollection<string> ValidationMessages { get; } = [];
    public ObservableCollection<ChartPoint> ChartPoints { get; } = [];
    public ObservableCollection<ScenarioHistoryRowViewModel> ScenarioHistory { get; } = [];
    public bool HasData => _controller.CurrentData.Count > 0;
    public bool HasScenario => _scenario is not null;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isBusy, value);
            foreach (var command in new[] { ImportCommand, LoadDemoCommand, CompareCommand, RunScenarioCommand, ApplyBillingScheduleCommand })
                command.NotifyCanExecuteChanged();
            ImportBillHistoryCommand.NotifyCanExecuteChanged();
            CancelCommand.NotifyCanExecuteChanged();
            ResetScenarioCommand.NotifyCanExecuteChanged();
        }
    }
    public bool IsStatusError { get => _isStatusError; private set => this.RaiseAndSetIfChanged(ref _isStatusError, value); }
    public string StatusMessage { get => _statusMessage; private set => this.RaiseAndSetIfChanged(ref _statusMessage, value); }
    public string DataSource { get => _dataSource; private set => this.RaiseAndSetIfChanged(ref _dataSource, value); }
    public string ValidationSummary { get => _validationSummary; private set => this.RaiseAndSetIfChanged(ref _validationSummary, value); }
    public bool IsImportReportOpen { get => _isImportReportOpen; set => this.RaiseAndSetIfChanged(ref _isImportReportOpen, value); }
    public string StoragePath => _controller.DatabasePath;
    public string DataSummary => !HasData ? "Hourly data · up to one year" :
        $"{_controller.CurrentData.Count:N0} hourly records · {_controller.CurrentData[0].Timestamp:MMM d, yyyy} – {_controller.CurrentData[^1].Timestamp:MMM d, yyyy}";
    public string TotalUsageText => HasData ? $"{_controller.CurrentData.Sum(x => x.KWh):N0} kWh" : "—";
    private BillEstimate? SelectedEstimate => _comparison?.Estimates.FirstOrDefault(bill => bill.PlanId == SelectedPlan?.PlanId);
    public PlanRowViewModel? SelectedPlan
    {
        get => _selectedPlan;
        set
        {
            if (ReferenceEquals(_selectedPlan, value)) return;
            this.RaiseAndSetIfChanged(ref _selectedPlan, value);
            RefreshBillRows();
            RefreshData();
        }
    }
    public string EstimatedCostText => SelectedEstimate is { } bill ? Money(bill.TotalCost) : "—";
    public string AverageMonthlyText => SelectedEstimate is { Months.Count: > 0 } bill ? Money(bill.TotalCost / bill.Months.Count) : "—";
    public string PlanSelectionMessage
    {
        get
        {
            if (SelectedPlan is null) return "Select a rate plan.";
            if (!SelectedPlan.IsImplemented) return SelectedPlan.Description;
            if (!HasData) return "Import usage to calculate an estimate.";
            if (_comparison is not null && SelectedEstimate is null) return "No readings fall within the applied billing periods.";
            return "";
        }
    }
    public string RecommendationText => _comparison?.Recommendation?.PlanName ?? "Import to estimate";
    public string RecommendationDetail => _comparison is null
        ? $"Seven plans registered. {_controller.Plans.Count(plan => plan.IsImplemented)} calculations available."
        : $"Lowest estimated cost among {_comparison.Estimates.Count} implemented plans; other plan prices are not available.";
    public string ComparisonTimingText => _comparison is null ? "Energy charges only" :
        $"{_controller.CurrentData.Count:N0} records · {_comparison.Elapsed.TotalMilliseconds:N0} ms · {_comparison.Estimates.Count} of 7 plans priced";
    private BillEstimate? ModifiedEstimate => _scenario?.ModifiedCosts.FirstOrDefault(bill => bill.PlanId == SelectedPlan?.PlanId);
    private decimal? ScenarioSavings => SelectedPlan is null ? null : _scenario?.Scenario.GetCostDifference(SelectedPlan.PlanId);
    public string ScenarioCostText => ModifiedEstimate is { } bill ? Money(bill.TotalCost) : "—";
    public string ScenarioSavingsText => ScenarioSavings is { } savings ? Money(savings) : "—";
    public string ScenarioSummary
    {
        get
        {
            if (_scenario is null) return "Adjust usage, then run your scenario. Original history stays intact.";
            if (ModifiedEstimate is not { } modified || ScenarioSavings is not { } savings)
                return $"{_scenario.Scenario.Name} · {_scenario.Data.Sum(x => x.KWh):N1} kWh. {PlanSelectionMessage}";

            var originalCost = modified.TotalCost + savings;
            var change = originalCost > 0 ? -100m * savings / originalCost : 0m;
            return $"{_scenario.Scenario.Name} · {modified.TotalKWh:N1} kWh in applied periods · {change:+0.0;-0.0;0.0}% energy cost change · {modified.PlanName}";
        }
    }
    public string ScenarioName { get => _scenarioName; set => this.RaiseAndSetIfChanged(ref _scenarioName, value); }
    public decimal ReductionPercent { get => _reductionPercent; set => this.RaiseAndSetIfChanged(ref _reductionPercent, value); }
    public decimal ShiftPercent { get => _shiftPercent; set => this.RaiseAndSetIfChanged(ref _shiftPercent, value); }
    public int ShiftHours { get => _shiftHours; set => this.RaiseAndSetIfChanged(ref _shiftHours, value); }
    public int SourceStartHour { get => _sourceStartHour; set => this.RaiseAndSetIfChanged(ref _sourceStartHour, value); }
    public int SourceEndHour { get => _sourceEndHour; set => this.RaiseAndSetIfChanged(ref _sourceEndHour, value); }
    public int SelectedTabIndex { get => _selectedTabIndex; set => this.RaiseAndSetIfChanged(ref _selectedTabIndex, value); }
    public int ChartModeIndex
    {
        get => _chartModeIndex;
        set { if (_chartModeIndex == value) return; this.RaiseAndSetIfChanged(ref _chartModeIndex, value); RefreshChart(); }
    }
    public string ChartTitle => ChartModeIndex switch { 1 => "Your daily rhythm", 2 => "Estimated billing-period costs", 3 => "Same-period cost forecast", _ => "Your usage over time" };
    public string ChartSubtitle => ChartModeIndex switch
    {
        1 => "Average kWh per recorded hour of day · missing hours are excluded",
        2 => SelectedEstimate is null ? PlanSelectionMessage : $"{SelectedPlan!.Name} energy charges by billing period",
        3 => SelectedEstimate is null ? PlanSelectionMessage : CanReplayCosts
            ? "Recorded billing-period usage replayed one year later at the same rates"
            : "Year 9999 cannot be projected further. Showing recorded-period costs at the same rates.",
        _ => "Monthly usage · hover to explore your original and modified consumption"
    };
    public string ChartUnit => ChartModeIndex >= 2 ? "$" : "kWh";
    private bool CanReplayCosts => SelectedEstimate?.Months.All(bill => bill.Period?.End.Year != 9999 && bill.Month.Year != 9999) == true;
    public MonthlyBillRowViewModel? SelectedMonth
    {
        get => _selectedMonth;
        set { this.RaiseAndSetIfChanged(ref _selectedMonth, value); this.RaisePropertyChanged(nameof(SelectedBillBreakdown)); }
    }
    public string SelectedBillBreakdown => SelectedMonth?.Breakdown ??
        (PlanSelectionMessage.Length > 0 ? PlanSelectionMessage : "Select a billing period to see its energy charges.");

    public Task InitializeAsync()
    {
        if (_initialized) return Task.CompletedTask;
        _initialized = true;
        return RunBusyAsync(async () =>
        {
            try { await _controller.LoadBillingScheduleAsync(); }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
            {
                _billingLoadNotice = "Saved billing settings could not be loaded. Calendar months are being used. Apply a schedule to save new settings.";
            }
            SyncBillingEditor();
            if (await _controller.LoadAsync())
            {
                DataSource = "Saved locally · restored history";
                ValidationMessages.Clear();
                foreach (var warning in DataManager.GetCoverageWarnings(_controller.CurrentData)) ValidationMessages.Add("Notice · " + warning);
                ValidationSummary = $"Validated and restored {_controller.CurrentData.Count:N0} locally saved records.";
                RefreshData();
                await CompareAsync();
                StatusMessage = "Your locally saved history is ready.";
            }
            else await LoadDemoCoreAsync();
        });
    }

    private Task ImportAsync() => RunBusyAsync(async () =>
    {
        if (_selectUsageFile is null) { StatusMessage = "A desktop file picker is required to import usage data."; return; }
        var path = await _selectUsageFile();
        if (path is null) { StatusMessage = "Import cancelled. Your current data is unchanged."; return; }
        StatusMessage = "Validating and saving your usage file locally…";
        var result = await _controller.ImportFileAsync(path);
        ValidationMessages.Clear();
        foreach (var error in result.Errors) ValidationMessages.Add("Error · " + error);
        foreach (var warning in result.Warnings) ValidationMessages.Add("Notice · " + warning);
        ValidationSummary = result.Summary;
        if (!result.IsValid)
        {
            IsStatusError = true;
            StatusMessage = "Import rejected. Review the Import report for details.";
            IsImportReportOpen = true;
            return;
        }
        DataSource = Path.GetFileName(path) + " · saved on this device";
        ResetResultsForNewData();
        await CompareAsync();
        StatusMessage = result.Warnings.Count > 0 ? "Import complete. Coverage notices are available in the Import report." : "Usage data validated, saved locally, and compared.";
    });

    private Task LoadDemoAsync() => RunBusyAsync(LoadDemoCoreAsync);
    private async Task LoadDemoCoreAsync()
    {
        await _controller.UseDemoAsync();
        DataSource = "Demo · synthetic 2025 usage";
        ValidationSummary = "8,760 complete hourly demo records. Demo data is not saved over your imported history.";
        ValidationMessages.Clear();
        ValidationMessages.Add("Synthetic data is provided to explore the application; import your own CSV or Excel workbook for a personal estimate.");
        ResetResultsForNewData();
        await CompareAsync();
        StatusMessage = "Exploring demo data. Import a CSV or Excel workbook to analyze your own usage.";
    }

    private async Task CompareAsync()
    {
        StatusMessage = "Comparing available rate calculations…";
        UpdateComparison(await _controller.ComparePlans());
        StatusMessage = _comparison?.Recommendation is null ? "No recorded usage falls within the applied billing periods."
            : "Residential Service estimate ready. Six plans await pricing implementation.";
    }

    private Task RunScenarioAsync() => RunBusyAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(ScenarioName) || ScenarioName.Length > 80)
            throw new ArgumentException("Give your scenario a name between 1 and 80 characters.");
        StatusMessage = "Applying your strategy and recalculating…";
        var definition = new UsageScenario
        {
            Name = ScenarioName.Trim(), ReductionPercent = ReductionPercent, ShiftPercent = ShiftPercent,
            ShiftHours = ShiftHours, SourceStartHour = SourceStartHour, SourceEndHour = SourceEndHour
        };
        _scenario = await _controller.RunScenario(definition);
        ScenarioHistory.Insert(0, new ScenarioHistoryRowViewModel(definition.Name,
            $"Reduce {definition.ReductionPercent:N0}% · shift {definition.ShiftPercent:N0}% from {definition.SourceStartHour:00}:00–{definition.SourceEndHour:00}:00 by {definition.ShiftHours:+0;-0;0}h",
            ScenarioCostText, ScenarioSavingsText));
        if (ScenarioHistory.Count > 20) ScenarioHistory.RemoveAt(20);
        RefreshData();
        StatusMessage = "Scenario complete. Compare the chart overlay and the original estimate; run again to test another strategy.";
    });

    private void ResetScenario()
    {
        IsStatusError = false;
        _scenario = null;
        ReductionPercent = 10; ShiftPercent = 50; ShiftHours = 5; SourceStartHour = 14; SourceEndHour = 19;
        ScenarioName = "My energy strategy";
        RefreshData();
        StatusMessage = "Scenario overlay cleared. Earlier scenario results remain in this session’s history.";
    }

    private void ResetResultsForNewData()
    {
        _comparison = null;
        RefreshBillRows();
        UpdatePlanCosts();
        _scenario = null;
        ScenarioHistory.Clear();
        RefreshData();
    }

    private async Task RunBusyAsync(Func<Task> work)
    {
        if (IsBusy) return;
        IsBusy = true; IsStatusError = false;
        try { await work(); }
        catch (OperationCanceledException) { StatusMessage = "Operation cancelled. You can continue using the application."; }
        catch (Exception error)
        {
            IsStatusError = true;
            StatusMessage = error switch
            {
                UnauthorizedAccessException => "Access was denied. Select a readable usage file or check local storage permissions.",
                IOException => "The file could not be read or saved. Check that it exists and is not locked, then try again.",
                _ => "Unable to complete this operation: " + error.Message
            };
        }
        finally { IsBusy = false; RefreshData(); }
    }

    private void UpdateComparison(ComparisonResult comparison)
    {
        _comparison = comparison;
        UpdatePlanCosts();
        RefreshBillRows();
        RefreshData();
    }

    private void UpdatePlanCosts()
    {
        foreach (var plan in Plans)
        {
            var estimate = _comparison?.Estimates.FirstOrDefault(x => x.PlanId == plan.PlanId);
            plan.CostText = estimate is null ? "—" : Money(estimate.TotalCost);
        }
    }

    private void RefreshBillRows()
    {
        var previous = SelectedMonth?.Period;
        MonthlyBills.Clear();
        foreach (var bill in SelectedEstimate?.Months ?? []) MonthlyBills.Add(new MonthlyBillRowViewModel(bill));
        SelectedMonth = MonthlyBills.FirstOrDefault(row => row.Period == previous) ?? MonthlyBills.FirstOrDefault();
    }

    private void RefreshData()
    {
        foreach (var name in new[] { nameof(HasData), nameof(HasScenario), nameof(DataSummary), nameof(TotalUsageText),
            nameof(EstimatedCostText), nameof(AverageMonthlyText), nameof(RecommendationText), nameof(RecommendationDetail),
            nameof(ComparisonTimingText), nameof(ScenarioCostText), nameof(ScenarioSavingsText), nameof(ScenarioSummary),
            nameof(PlanSelectionMessage), nameof(SelectedBillBreakdown), nameof(BillingScheduleSummary), nameof(BillingNotice), nameof(HasBillingNotice) })
            this.RaisePropertyChanged(name);
        RefreshChart();
    }

    private void RefreshChart()
    {
        ChartPoints.Clear();
        if (ChartModeIndex >= 2)
        {
            var original = SelectedEstimate;
            var modified = ModifiedEstimate;
            var replay = ChartModeIndex == 3 && CanReplayCosts;
            foreach (var month in original?.Months ?? [])
            {
                var next = modified?.Months.FirstOrDefault(x => x.Period == month.Period && x.Month == month.Month);
                var label = CostChartLabel(month, replay);
                ChartPoints.Add(new ChartPoint(label, (double)month.TotalCost, next is null ? null : (double)next.TotalCost));
            }
        }
        else if (ChartModeIndex == 1)
        {
            var original = _controller.CurrentData.GroupBy(x => x.Hour).ToDictionary(x => x.Key, x => x.Average(y => y.KWh));
            var modified = _scenario?.Data.GroupBy(x => x.Hour).ToDictionary(x => x.Key, x => x.Average(y => y.KWh));
            for (var hour = 0; hour < 24 && HasData; hour++)
            {
                var available = original.TryGetValue(hour, out var amount);
                ChartPoints.Add(new ChartPoint($"{hour:00}:00", (double)amount,
                    modified is not null && modified.TryGetValue(hour, out var changed) ? (double)changed : null, available));
            }
        }
        else
        {
            var modified = _scenario?.Data.GroupBy(x => new DateTime(x.Year, x.Month, 1)).ToDictionary(x => x.Key, x => x.Sum(y => y.KWh));
            var original = _controller.CurrentData.GroupBy(x => new DateTime(x.Year, x.Month, 1)).ToDictionary(x => x.Key, x => x.Sum(y => y.KWh));
            if (HasData)
            {
                var month = original.Keys.Min();
                var last = original.Keys.Max();
                while (month <= last)
                {
                    var available = original.TryGetValue(month, out var amount);
                    ChartPoints.Add(new ChartPoint(month.ToString("MMM yy", Us), (double)amount,
                        modified is not null && modified.TryGetValue(month, out var changed) ? (double)changed : null, available));
                    if (month == last) break;
                    month = month.AddMonths(1);
                }
            }
        }
        this.RaisePropertyChanged(nameof(ChartTitle));
        this.RaisePropertyChanged(nameof(ChartSubtitle));
        this.RaisePropertyChanged(nameof(ChartUnit));
    }

    private string CostChartLabel(MonthlyBill bill, bool replay)
    {
        if (_controller.CurrentBillingSchedule.Mode != BillingMode.CalendarMonths && bill.Period is { } period)
        {
            var end = replay ? period.End.AddYears(1) : period.End;
            return end.ToString("MMM d, yy", Us);
        }
        return (replay ? bill.Month.AddYears(1) : bill.Month).ToString("MMM yy", Us);
    }

    private static string Money(decimal value) => value.ToString("C2", Us);
    public void Dispose() => _controller.Dispose();
}
