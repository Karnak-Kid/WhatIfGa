using System.Globalization;
using ReactiveUI;
using WhatIfGa.Commands;
using WhatIfGa.Core.Models;

namespace WhatIfGa.ViewModels;

public sealed partial class MainViewModel
{
    private int _billingModeIndex, _billingClosingDay;
    private string _billingPeriodText = "", _billingLoadNotice = "", _billingImportMessage = "";

    public AsyncCommand ApplyBillingScheduleCommand { get; }
    public AsyncCommand ImportBillHistoryCommand { get; }
    public string BillingImportMessage
    {
        get => _billingImportMessage;
        private set => this.RaiseAndSetIfChanged(ref _billingImportMessage, value);
    }

    public int BillingModeIndex
    {
        get => _billingModeIndex;
        set
        {
            this.RaiseAndSetIfChanged(ref _billingModeIndex, value);
            this.RaisePropertyChanged(nameof(IsEstimatedBilling));
            this.RaisePropertyChanged(nameof(IsActualBilling));
        }
    }

    public int BillingClosingDay
    {
        get => _billingClosingDay;
        set => this.RaiseAndSetIfChanged(ref _billingClosingDay, value);
    }

    public string BillingPeriodText
    {
        get => _billingPeriodText;
        set => this.RaiseAndSetIfChanged(ref _billingPeriodText, value ?? "");
    }

    public bool IsEstimatedBilling => BillingModeIndex == (int)BillingMode.Estimated;
    public bool IsActualBilling => BillingModeIndex == (int)BillingMode.Actual;

    public string BillingScheduleSummary
    {
        get
        {
            var schedule = _controller.CurrentBillingSchedule;
            return schedule.Mode switch
            {
                BillingMode.Actual when schedule.Periods.Count == 1 => "Actual billing dates · 1 saved period",
                BillingMode.Actual => $"Actual billing dates · {schedule.Periods.Count:N0} saved periods",
                BillingMode.Estimated => $"Estimated monthly periods · closing day {schedule.ClosingDay}",
                _ => "Calendar-month estimate"
            };
        }
    }

    public bool HasBillingNotice => BillingNotice.Length > 0;
    public string BillingNotice
    {
        get
        {
            var notices = new List<string>();
            if (_billingLoadNotice.Length > 0) notices.Add(_billingLoadNotice);
            if (HasData && _comparison is not null)
            {
                var estimate = _comparison.Recommendation;
                var unassigned = estimate?.UnassignedHours ?? _controller.CurrentData.Count;
                if (unassigned > 0)
                    notices.Add($"{unassigned:N0} recorded hours fall outside the applied periods and are excluded from the estimate.");
                var partial = estimate?.Months.Count(bill => bill.IsPartial) ?? 0;
                if (partial > 0)
                    notices.Add($"{partial:N0} periods have incomplete hourly coverage. Estimates use recorded consumption.");
                var schedule = _controller.CurrentBillingSchedule;
                if (schedule.Mode == BillingMode.Actual)
                {
                    var empty = schedule.Periods.Count - (estimate?.Months.Count ?? 0);
                    if (empty == 1) notices.Add("One saved billing period has no recorded readings.");
                    else if (empty > 1) notices.Add($"{empty:N0} saved billing periods have no recorded readings.");
                }
            }
            return string.Join(" ", notices);
        }
    }

    private async Task ApplyBillingScheduleAsync()
    {
        await RunBusyAsync(async () =>
        {
            var mode = (BillingMode)BillingModeIndex;
            var periods = mode == BillingMode.Actual ? ParseBillingPeriods(BillingPeriodText)
                : _controller.CurrentBillingSchedule.Periods;
            StatusMessage = "Applying the billing schedule…";
            var result = await _controller.ApplyBillingScheduleAsync(new BillingSchedule(mode, BillingClosingDay, periods));
            _billingLoadNotice = "";
            _scenario = null;
            ScenarioHistory.Clear();
            SyncBillingEditor();
            BillingImportMessage = "";
            UpdateComparison(result);
            StatusMessage = "Billing schedule saved. Estimates use the applied periods.";
        });
    }

    private Task ImportBillHistoryAsync(Func<Task<string?>> selectBillHistoryFile) => RunBusyAsync(async () =>
    {
        var path = await selectBillHistoryFile();
        if (path is null) return;

        StatusMessage = "Reading bill history…";
        var result = await _controller.ReadBillHistoryAsync(path);
        BillingPeriodText = FormatBillingPeriods(result.Periods);
        BillingModeIndex = (int)BillingMode.Actual;
        var periodCount = result.Periods.Count;
        var periodLabel = periodCount == 1 ? "period" : "periods";
        BillingImportMessage = $"Filled {periodCount:N0} {periodLabel} from {result.BillDateCount:N0} bill dates. " +
            $"Skipped {result.IgnoredTransactionCount:N0} non-bill rows. " +
            $"The period ending {result.OldestBillDate.ToString("MMM d, yyyy", Us)} needs an earlier bill date.";
        StatusMessage = "Billing periods filled in. Review the dates and apply the schedule.";
    });

    private void SyncBillingEditor()
    {
        var schedule = _controller.CurrentBillingSchedule;
        BillingModeIndex = (int)schedule.Mode;
        BillingClosingDay = schedule.ClosingDay;
        BillingPeriodText = FormatBillingPeriods(schedule.Periods);
    }

    private static string FormatBillingPeriods(IReadOnlyList<BillingPeriod> periods) =>
        string.Join(Environment.NewLine, periods.Select(period =>
            $"{period.Start.ToString("yyyy-MM-dd", Us)},{period.End.ToString("yyyy-MM-dd", Us)},{period.BillingMonth.ToString("yyyy-MM", Us)}"));

    private static IReadOnlyList<BillingPeriod> ParseBillingPeriods(string input)
    {
        if (input.Length > 16384) throw new ArgumentException("Billing periods are limited to 16,384 characters.");

        var periods = new List<BillingPeriod>();
        var rows = input.Split('\n');
        for (var index = 0; index < rows.Length; index++)
        {
            var row = rows[index].Trim();
            if (row.Length == 0) continue;
            if (periods.Count == 0 && row.Equals("BillingStart,BillingEnd,BillingMonth", StringComparison.OrdinalIgnoreCase)) continue;
            var fields = row.Split(',', StringSplitOptions.TrimEntries);
            if (fields.Length != 3 ||
                !DateOnly.TryParseExact(fields[0], "yyyy-MM-dd", Us, DateTimeStyles.None, out var start) ||
                !DateOnly.TryParseExact(fields[1], "yyyy-MM-dd", Us, DateTimeStyles.None, out var end) ||
                fields[2].Length != 7 ||
                !DateOnly.TryParseExact(fields[2] + "-01", "yyyy-MM-dd", Us, DateTimeStyles.None, out var month))
            {
                throw new ArgumentException($"Billing period row {index + 1} must use yyyy-MM-dd,yyyy-MM-dd,yyyy-MM.");
            }
            periods.Add(new BillingPeriod(start, end, month));
        }
        return periods;
    }
}
