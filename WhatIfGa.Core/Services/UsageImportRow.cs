namespace WhatIfGa.Core.Services;

/// <summary>A physical CSV line or worksheet row before shared usage validation.</summary>
public sealed record UsageImportRow(int Number, IReadOnlyList<string> Fields);
