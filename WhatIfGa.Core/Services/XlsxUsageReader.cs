using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using WhatIfGa.Core.Models;

namespace WhatIfGa.Core.Services;

/// <summary>
/// Reads a bounded, values-only OOXML workbook without Excel, macros, formula execution,
/// network access, or extracting ZIP entries to disk. Account/preamble cells are never persisted.
/// </summary>
public static class XlsxUsageReader
{
    private const int MaximumEntries = 256;
    private const int MaximumColumns = 64;
    private const int MaximumRows = UsageValidation.MaximumRecords + 128;
    private const int MaximumSheets = 16;
    private const int MaximumSharedStrings = 60000;
    private const int MaximumCellCharacters = 65536;
    private const long MaximumPartBytes = 32 * 1024 * 1024;
    private const long MaximumExpandedBytes = 64 * 1024 * 1024;

    public static IReadOnlyList<UsageImportRow> Read(string path, CancellationToken cancellationToken = default)
        => Read(path, IsUsageHeader, "usage",
            "No visible worksheet contains a supported hourly usage header (Hour,kWh or Timestamp,kWh).", cancellationToken);

    internal static IReadOnlyList<UsageImportRow> Read(string path, Func<IReadOnlyList<string>, bool> isHeader,
        string tableName, string missingHeaderMessage, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > UsageValidation.MaximumFileBytes)
            throw new InvalidDataException("Excel files must be 10 MiB or smaller.");
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        if (archive.Entries.Count > MaximumEntries)
            throw new InvalidDataException("The workbook contains too many package parts.");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        long expandedBytes = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!entries.TryAdd(entry.FullName, entry))
                throw new InvalidDataException("The workbook contains duplicate package parts.");
            if (entry.Length > MaximumPartBytes || entry.Length > MaximumExpandedBytes - expandedBytes)
                throw new InvalidDataException("The workbook exceeds the safe expanded-size limit.");
            expandedBytes += entry.Length;
        }

        var workbook = LoadDocument(Required(entries, "xl/workbook.xml"), cancellationToken);
        var relationshipDocument = LoadDocument(Required(entries, "xl/_rels/workbook.xml.rels"), cancellationToken);
        var relationships = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var relation in relationshipDocument.Root?.Elements() ?? [])
        {
            if (relation.Name.LocalName != "Relationship") continue;
            var id = (string?)relation.Attribute("Id");
            if (string.IsNullOrWhiteSpace(id) || !relationships.TryAdd(id, relation))
                throw new InvalidDataException("The workbook contains invalid or duplicate relationships.");
        }

        var date1904 = workbook.Root?.Elements().FirstOrDefault(x => x.Name.LocalName == "workbookPr")?
            .Attribute("date1904")?.Value is "1" or "true";
        var sharedStrings = ReadSharedStrings(FindPart("/sharedStrings"), cancellationToken);
        var dateStyles = ReadDateStyles(FindPart("/styles"), cancellationToken);
        var sheetElements = workbook.Root?.Elements().FirstOrDefault(x => x.Name.LocalName == "sheets")?
            .Elements().Where(x => x.Name.LocalName == "sheet").ToArray() ?? [];
        if (sheetElements.Length > MaximumSheets)
            throw new InvalidDataException($"Workbooks may contain at most {MaximumSheets} worksheets.");
        IReadOnlyList<UsageImportRow>? selected = null;
        foreach (var sheet in sheetElements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = (string?)sheet.Attribute("state");
            if (state is "hidden" or "veryHidden") continue;
            var relationId = sheet.Attributes().FirstOrDefault(x => x.Name.LocalName == "id")?.Value;
            if (relationId is null || !relationships.TryGetValue(relationId, out var relation) ||
                !((string?)relation.Attribute("Type") ?? "").EndsWith("/worksheet", StringComparison.Ordinal))
                throw new InvalidDataException("A visible worksheet has an invalid package relationship.");
            var rows = ReadRows(Required(entries, ResolveTarget(relation)), sharedStrings, dateStyles,
                date1904, cancellationToken);
            if (!rows.Take(33).Any(row => isHeader(row.Fields))) continue;
            if (selected is not null)
                throw new InvalidDataException($"The workbook contains multiple {tableName} worksheets. Keep one {tableName} table per workbook before importing.");
            selected = rows;
        }
        return selected ?? throw new InvalidDataException(missingHeaderMessage);

        ZipArchiveEntry? FindPart(string typeSuffix)
        {
            var matches = relationships.Values.Where(x => ((string?)x.Attribute("Type") ?? "")
                .EndsWith(typeSuffix, StringComparison.Ordinal)).ToArray();
            if (matches.Length > 1) throw new InvalidDataException("The workbook contains ambiguous package relationships.");
            return matches.Length == 0 ? null : Required(entries, ResolveTarget(matches[0]));
        }
    }

    private static ZipArchiveEntry Required(IReadOnlyDictionary<string, ZipArchiveEntry> entries, string name)
        => entries.TryGetValue(name, out var entry) ? entry
            : throw new InvalidDataException("A required workbook package part is missing.");

    private static string ResolveTarget(XElement relationship)
    {
        if ((string?)relationship.Attribute("TargetMode") is { } mode && mode != "Internal")
            throw new InvalidDataException("External workbook data relationships are not supported.");
        var target = (string?)relationship.Attribute("Target");
        if (string.IsNullOrWhiteSpace(target) || target.Contains('\\') ||
            !Uri.TryCreate(new Uri("https://workbook.invalid/xl/workbook.xml"), target, out var resolved) ||
            resolved.Scheme != "https" || resolved.Host != "workbook.invalid" ||
            resolved.Query.Length != 0 || resolved.Fragment.Length != 0)
            throw new InvalidDataException("The workbook contains an invalid package path.");
        var name = Uri.UnescapeDataString(resolved.AbsolutePath).TrimStart('/');
        if (!name.StartsWith("xl/", StringComparison.Ordinal) || name.Split('/').Any(x => x is ".." or ".") || name.Contains('\\'))
            throw new InvalidDataException("The workbook contains an invalid package path.");
        return name;
    }

    private static XmlReader OpenXml(ZipArchiveEntry entry)
        => XmlReader.Create(entry.Open(), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumPartBytes,
            MaxCharactersFromEntities = 1024,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            CloseInput = true
        });

    private static XDocument LoadDocument(ZipArchiveEntry entry, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var reader = OpenXml(entry);
        var document = XDocument.Load(reader);
        token.ThrowIfCancellationRequested();
        return document;
    }

    private static IReadOnlyList<string> ReadSharedStrings(ZipArchiveEntry? entry, CancellationToken token)
    {
        var result = new List<string>();
        if (entry is null) return result;
        using var reader = OpenXml(entry);
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "si") continue;
            if (result.Count >= MaximumSharedStrings) throw new InvalidDataException("The workbook contains too many shared strings.");
            using var subtree = reader.ReadSubtree();
            var item = XElement.Load(subtree);
            var value = string.Concat(item.Descendants().Where(x => x.Name.LocalName == "t" &&
                !x.Ancestors().Any(a => a.Name.LocalName == "rPh")).Select(x => x.Value));
            if (value.Length > MaximumCellCharacters) throw new InvalidDataException("A workbook cell is too long.");
            result.Add(value);
        }
        return result;
    }

    private static IReadOnlyList<bool> ReadDateStyles(ZipArchiveEntry? entry, CancellationToken token)
    {
        if (entry is null) return Array.Empty<bool>();
        var document = LoadDocument(entry, token);
        var customFormats = new Dictionary<int, string>();
        foreach (var format in document.Descendants().Where(x => x.Name.LocalName == "numFmt"))
        {
            if (int.TryParse((string?)format.Attribute("numFmtId"), NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                customFormats[id] = (string?)format.Attribute("formatCode") ?? "";
        }
        return document.Root?.Elements().FirstOrDefault(x => x.Name.LocalName == "cellXfs")?.Elements()
            .Where(x => x.Name.LocalName == "xf").Select(style =>
            {
                token.ThrowIfCancellationRequested();
                var id = int.TryParse((string?)style.Attribute("numFmtId"), out var numberFormat) ? numberFormat : 0;
                return id is >= 14 and <= 22 or >= 27 and <= 36 or >= 45 and <= 47 or >= 50 and <= 58 ||
                    customFormats.TryGetValue(id, out var code) && IsDateFormat(code);
            }).ToArray() ?? [];
    }

    private static bool IsDateFormat(string format)
    {
        var tokens = new StringBuilder();
        var quoted = false;
        var bracketed = false;
        for (var i = 0; i < format.Length; i++)
        {
            var character = char.ToLowerInvariant(format[i]);
            if (character == '"') { quoted = !quoted; continue; }
            if (quoted) continue;
            if (character is '\\' or '_' or '*') { i++; continue; }
            if (character == '[') { bracketed = true; continue; }
            if (character == ']') { bracketed = false; continue; }
            if (!bracketed) tokens.Append(character);
        }
        return tokens.ToString().Any(character => character is 'y' or 'm' or 'd' or 'h' or 's');
    }

    private static IReadOnlyList<UsageImportRow> ReadRows(ZipArchiveEntry entry, IReadOnlyList<string> sharedStrings,
        IReadOnlyList<bool> dateStyles, bool date1904, CancellationToken token)
    {
        var result = new List<UsageImportRow>();
        var previousRow = 0;
        using var reader = OpenXml(entry);
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "row") continue;
            if (result.Count >= MaximumRows) throw new InvalidDataException("The worksheet contains too many rows to import.");
            var rowNumberText = reader.GetAttribute("r");
            var number = previousRow + 1;
            if (rowNumberText is not null && (!int.TryParse(rowNumberText, NumberStyles.None, CultureInfo.InvariantCulture, out number) || number <= previousRow || number > 1048576))
                throw new InvalidDataException("The worksheet contains invalid or unordered row numbers.");
            previousRow = number;
            using var subtree = reader.ReadSubtree();
            var row = XElement.Load(subtree);
            var fields = new List<string>();
            var previousColumn = -1;
            foreach (var cell in row.Elements().Where(x => x.Name.LocalName == "c"))
            {
                token.ThrowIfCancellationRequested();
                var reference = (string?)cell.Attribute("r");
                var column = reference is null ? previousColumn + 1 : ColumnIndex(reference, number);
                if (column <= previousColumn || column >= MaximumColumns)
                    throw new InvalidDataException($"Worksheet row {number}: cells must be unique, ordered and within the first {MaximumColumns} columns.");
                previousColumn = column;
                while (fields.Count <= column) fields.Add("");
                fields[column] = CellValue(cell, sharedStrings, dateStyles, date1904, number);
            }
            result.Add(new UsageImportRow(number, fields.AsReadOnly()));
        }
        return result.AsReadOnly();
    }

    private static int ColumnIndex(string reference, int rowNumber)
    {
        var column = 0;
        var letters = 0;
        foreach (var character in reference)
        {
            if (character is < 'A' or > 'Z') break;
            if (++letters > 3) throw new InvalidDataException($"Worksheet row {rowNumber}: invalid cell reference.");
            column = column * 26 + character - 'A' + 1;
        }
        if (letters == 0 || !int.TryParse(reference.AsSpan(letters), NumberStyles.None, CultureInfo.InvariantCulture, out var cellRow) || cellRow != rowNumber)
            throw new InvalidDataException($"Worksheet row {rowNumber}: invalid cell reference.");
        return column - 1;
    }

    private static string CellValue(XElement cell, IReadOnlyList<string> sharedStrings,
        IReadOnlyList<bool> dateStyles, bool date1904, int rowNumber)
    {
        if (cell.Elements().Any(x => x.Name.LocalName == "f"))
            throw new InvalidDataException($"Worksheet row {rowNumber}: formulas are not supported. Export plain values before importing.");
        var type = (string?)cell.Attribute("t") ?? "n";
        var value = cell.Elements().FirstOrDefault(x => x.Name.LocalName == "v")?.Value ?? "";
        if (type == "e") throw new InvalidDataException($"Worksheet row {rowNumber}: a cell contains an Excel error.");
        if (type == "s")
        {
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index < 0 || index >= sharedStrings.Count)
                throw new InvalidDataException($"Worksheet row {rowNumber}: invalid shared-string reference.");
            value = sharedStrings[index];
        }
        else if (type == "inlineStr")
            value = string.Concat(cell.Descendants().Where(x => x.Name.LocalName == "t" &&
                !x.Ancestors().Any(a => a.Name.LocalName == "rPh")).Select(x => x.Value));
        else if (type == "b")
            throw new InvalidDataException($"Worksheet row {rowNumber}: boolean cell values are unsupported.");
        else if (type is not ("n" or "str" or "d"))
            throw new InvalidDataException($"Worksheet row {rowNumber}: unsupported cell data type.");
        if (value.Length > MaximumCellCharacters) throw new InvalidDataException($"Worksheet row {rowNumber}: a cell is too long.");
        var styleText = (string?)cell.Attribute("s");
        if (styleText is not null)
        {
            if (!int.TryParse(styleText, NumberStyles.None, CultureInfo.InvariantCulture, out var style) || style < 0 || style >= dateStyles.Count)
                throw new InvalidDataException($"Worksheet row {rowNumber}: invalid cell style reference.");
            if (type == "n" && value.Length > 0 && dateStyles[style]) value = ExcelDate(value, date1904, rowNumber);
        }
        return value;
    }

    private static string ExcelDate(string value, bool date1904, int rowNumber)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) || !double.IsFinite(serial))
            throw new InvalidDataException($"Worksheet row {rowNumber}: invalid Excel date value.");
        try
        {
            if (!date1904 && (serial < 1 || serial is >= 60 and < 61) || date1904 && serial < 0)
                throw new ArgumentException("Unsupported Excel date serial.");
            var timestamp = date1904 ? new DateTime(1904, 1, 1).AddDays(serial)
                : serial < 60 ? new DateTime(1899, 12, 31).AddDays(serial) : DateTime.FromOADate(serial);
            var second = TimeSpan.TicksPerSecond;
            var rounded = (timestamp.Ticks + second / 2) / second * second;
            if (Math.Abs(timestamp.Ticks - rounded) <= TimeSpan.TicksPerMillisecond)
                timestamp = new DateTime(rounded);
            return timestamp.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture).TrimEnd('.');
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            throw new InvalidDataException($"Worksheet row {rowNumber}: Excel date is outside the supported calendar range.", exception);
        }
    }

    private static bool IsUsageHeader(IReadOnlyList<string> fields)
    {
        var names = fields.Select(field => new string(field.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray())).ToArray();
        var usage = names.Contains("kwh") || names.Contains("usagekwh");
        var timestamp = names.Any(name => name is "timestamp" or "datetime" or "startdatetime");
        return usage && (timestamp || names.Contains("hour"));
    }
}
