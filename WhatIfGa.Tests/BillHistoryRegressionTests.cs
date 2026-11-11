using System.Globalization;
using WhatIfGa.Core.Models;
using WhatIfGa.Core.Services;
using WhatIfGa.Services;
using WhatIfGa.ViewModels;
using static ImportRegressionTests;

internal static class BillHistoryRegressionTests
{
    private const string Header = "Date,Description,Usage (kWh),Amount\n";
    private const string History = Header +
        "2025-07-12,Bill,450,31.50\n" +
        "2025-06-28,Payment,,22.75\n" +
        "6/14/2025,Bill,320,22.75\n" +
        "2025-06-14,Bill,320,22.75\n" +
        "2025-05-11,Bill,205,17.25\n";
    private static readonly BillingPeriod[] ExpectedPeriods =
    [
        new(new DateOnly(2025, 5, 11), new DateOnly(2025, 6, 14), new DateOnly(2025, 6, 1)),
        new(new DateOnly(2025, 6, 14), new DateOnly(2025, 7, 12), new DateOnly(2025, 7, 1)),
    ];

    public static void CsvPeriods()
    {
        using var files = new TestFiles();
        var history = BillHistoryImporter.Read(files.Csv("history.csv", History));
        Equal(3, history.BillDateCount, "duplicate dates count once after date parsing");
        Equal(1, history.IgnoredTransactionCount, "a payment does not define a billing boundary");
        Equal(ExpectedPeriods[0].Start, history.OldestBillDate, "the oldest bill date anchors the first known period");
        Check(history.Periods.SequenceEqual(ExpectedPeriods), "sorted adjacent bill dates determine periods and their closing months");
    }

    public static void RejectsInvalidHistory()
    {
        using var files = new TestFiles();
        var insufficient = files.Csv("insufficient.csv", Header +
            "2025-05-11,Bill,205,17.25\n" +
            "2025-05-11,Bill,205,17.25\n" +
            "2025-06-28,Payment,,17.25\n");
        ExpectInvalid(insufficient, "one distinct bill date cannot define a period");

        var invalidDate = files.Csv("invalid-date.csv", History + "2025-02-30,Bill,100,10.00\n");
        ExpectInvalid(invalidDate, "an invalid bill date must not be silently ignored");
    }

    public static void XlsxPeriods()
    {
        using var files = new TestFiles();
        var rows = Row(1, Inline("A1", "Date") + Inline("B1", "Description") +
                Inline("C1", "Usage (kWh)") + Inline("D1", "Amount")) +
            Row(2, Number("A2", SerialDate(2025, 7, 12), 1) + Inline("B2", "Bill") + Number("C2", "450")) +
            Row(3, Number("A3", SerialDate(2025, 6, 28), 1) + Inline("B3", "Payment")) +
            Row(4, Number("A4", SerialDate(2025, 6, 14), 1) + Inline("B4", "Bill") + Number("C4", "320")) +
            Row(5, Inline("A5", "2025-05-11") + Inline("B5", "Bill") + Number("C5", "205"));
        var history = BillHistoryImporter.Read(Workbook(files, "history.xlsx", rows));
        Equal(3, history.BillDateCount, "three distinct workbook bill dates");
        Equal(1, history.IgnoredTransactionCount, "workbook payment is ignored");
        Check(history.Periods.SequenceEqual(ExpectedPeriods), "Excel dates yield the same billing periods as CSV dates");
    }

    public static async Task ViewModelStagesHistory()
    {
        using var files = new TestFiles();
        var manager = new DataManager(files.StorageDirectory);
        await manager.SaveAsync([new UsageData(new DateTime(2025, 5, 25), 1200m)]);
        var selectedPath = files.Csv("history.csv", History);
        var controller = new AppController(manager, new RateCalculator());
        using var vm = new MainViewModel(() => Task.FromResult<string?>(null), controller,
            () => Task.FromResult<string?>(selectedPath));
        await vm.InitializeAsync();

        await vm.ImportBillHistoryCommand.ExecuteAsync();
        Equal(2, vm.BillingModeIndex, "bill history opens the actual billing editor");
        Check(vm.BillingPeriodText.Contains("2025-05-11,2025-06-14,2025-06", StringComparison.Ordinal),
            "inferred periods appear in the draft");
        Check(!string.IsNullOrWhiteSpace(vm.BillingImportMessage), "the imported draft has a summary");
        Equal(BillingMode.CalendarMonths, controller.CurrentBillingSchedule.Mode, "import leaves the applied mode intact");
        Equal("$98.40", vm.EstimatedCostText, "the estimate still uses May calendar rates");

        var draft = vm.BillingPeriodText;
        selectedPath = files.Csv("invalid.csv", Header + "invalid,Bill,205,17.25\n");
        await vm.ImportBillHistoryCommand.ExecuteAsync();
        Equal(draft, vm.BillingPeriodText, "a failed import keeps the previous draft");
        Equal(2, vm.BillingModeIndex, "a failed import keeps the staged mode");
        Check(vm.IsStatusError && !string.IsNullOrWhiteSpace(vm.StatusMessage), "a failed import reports an error");

        await vm.ApplyBillingScheduleCommand.ExecuteAsync();
        Check(!vm.IsStatusError, vm.StatusMessage);
        Equal(BillingMode.Actual, controller.CurrentBillingSchedule.Mode, "Apply activates the staged billing schedule");
        Check(controller.CurrentBillingSchedule.Periods.SequenceEqual(ExpectedPeriods), "Apply uses the inferred periods");
        Equal("$138.50", vm.EstimatedCostText, "the estimate uses the inferred June closing month");
    }

    public static void VerifySuppliedFile(string path)
    {
        var history = BillHistoryImporter.Read(path);
        Equal(history.BillDateCount - 1, history.Periods.Count, "each pair of adjacent bill dates creates one period");
        Console.WriteLine(FormattableString.Invariant($"PASS  Supplied bill history imported {history.BillDateCount} bill dates and {history.Periods.Count} periods from {history.OldestBillDate:yyyy-MM-dd} to {history.Periods[^1].End:yyyy-MM-dd}. Ignored {history.IgnoredTransactionCount} other transactions."));
    }

    private static string SerialDate(int year, int month, int day)
        => new DateTime(year, month, day).ToOADate().ToString("R", CultureInfo.InvariantCulture);

    private static void ExpectInvalid(string path, string message)
    {
        try { BillHistoryImporter.Read(path); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException(message);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message}: expected {expected}, got {actual}");
    }
}
