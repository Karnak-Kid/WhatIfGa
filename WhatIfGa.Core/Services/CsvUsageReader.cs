using System.Text;
using WhatIfGa.Core.Models;

namespace WhatIfGa.Core.Services;

/// <summary>Reads bounded CSV rows while preserving physical line numbers for validation.</summary>
internal static class CsvUsageReader
{
    public static IReadOnlyList<UsageImportRow> Read(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > UsageValidation.MaximumFileBytes)
            throw new InvalidDataException("CSV files must be 10 MiB or smaller.");
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        var rows = new List<UsageImportRow>();
        var lineNumber = 0;
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            cancellationToken.ThrowIfCancellationRequested();
            if (line.Length > 65536)
                throw new InvalidDataException($"Line {lineNumber}: the row exceeds the 65,536-character limit.");
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!TrySplitLine(line, out var fields, out var error))
                throw new InvalidDataException($"Line {lineNumber}: {error}");
            rows.Add(new UsageImportRow(lineNumber, fields.AsReadOnly()));
            if (rows.Count > UsageValidation.MaximumRecords + 33)
                throw new InvalidDataException($"Line {lineNumber}: the file contains too many rows to import.");
        }
        return rows.AsReadOnly();
    }

    private static bool TrySplitLine(string line, out List<string> fields, out string error)
    {
        fields = [];
        error = "";
        var field = new StringBuilder();
        var quoted = false;
        var closedQuote = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (quoted)
            {
                if (character == '"')
                {
                    if (index + 1 < line.Length && line[index + 1] == '"') { field.Append('"'); index++; }
                    else { quoted = false; closedQuote = true; }
                }
                else field.Append(character);
            }
            else if (character == ',')
            {
                fields.Add(field.ToString().Trim());
                field.Clear();
                closedQuote = false;
            }
            else if (character == '"')
            {
                if (field.ToString().Trim().Length != 0 || closedQuote)
                { error = "unexpected quote in an unquoted field."; return false; }
                field.Clear();
                quoted = true;
            }
            else if (closedQuote && !char.IsWhiteSpace(character))
            { error = "characters after a closing quote are invalid."; return false; }
            else if (!closedQuote) field.Append(character);
        }
        if (quoted) { error = "unclosed quoted field; embedded line breaks are not supported."; return false; }
        fields.Add(field.ToString().Trim());
        return true;
    }
}
