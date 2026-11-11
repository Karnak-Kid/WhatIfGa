namespace WhatIfGa.Core.Models;

/// <summary>Shared CSV/XLSX usage-import outcome. The original type name is retained for API compatibility.</summary>
public sealed record CsvValidationResult(
    bool IsValid,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<UsageData> Data)
{
    public string Summary => IsValid
        ? $"Validated {Data.Count:N0} hourly records • {Data.Sum(item => item.KWh):N2} kWh" +
          (Warnings.Count == 0 ? "." : $" • {Warnings.Count} data warning(s).")
        : $"Import rejected: {Errors.Count:N0} validation error(s). Existing history was kept.";
}
