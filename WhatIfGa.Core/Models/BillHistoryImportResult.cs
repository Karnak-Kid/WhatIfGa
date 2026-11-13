namespace WhatIfGa.Core.Models;

public sealed record BillHistoryImportResult(
    IReadOnlyList<BillingPeriod> Periods,
    DateOnly OldestBillDate,
    int BillDateCount,
    int IgnoredTransactionCount);
