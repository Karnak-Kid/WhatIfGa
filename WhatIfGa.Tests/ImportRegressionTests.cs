using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using WhatIfGa.Core.Models;
using WhatIfGa.Core.Services;
using WhatIfGa.Services;
using WhatIfGa.ViewModels;

/// <summary>All permanent fixtures are synthetic. Optional real-file checks never copy source data into the repository.</summary>
internal static class ImportRegressionTests
{
    private const string FakeAccount = "0001112223";
    private const string MainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelNs = "http://schemas.openxmlformats.org/package/2006/relationships";

    public static async Task GeorgiaPowerCsv()
    {
        using var files = new TestFiles();
        var manager = new DataManager(files.StorageDirectory);
        var csv = files.Csv("georgia.csv", PreambleCsv + "Hour,kWh,Temp\n2025-01-01 00:00,1.25,42\n2025-01-01 01:00,0.75,\n");
        var result = await manager.ImportFileAsync(csv);
        Valid(result);
        Equal(2, result.Data.Count, "utility record count");
        Equal(2.00m, result.Data.Sum(x => x.KWh), "utility energy");
        Equal(new DateTime(2025, 1, 1), result.Data[0].Timestamp, "Hour is a full timestamp");
        Equal<decimal?>(null, result.Data[1].Temp, "blank utility temperature");
        Check(!File.ReadAllText(manager.DatabasePath).Contains(FakeAccount, StringComparison.Ordinal), "account metadata must not be persisted");
        Check(!string.Join(" ", result.Warnings.Concat(result.Errors)).Contains(FakeAccount, StringComparison.Ordinal), "account metadata must not enter reports");
        var uppercase = files.Csv("georgia.CSV", "Hour,kWh\n2025-01-01 00:00,1.25\n");
        Valid(manager.ValidateFile(uppercase));
        Valid(manager.ValidateFile(files.Csv("components.csv", "Year,Month,Day,Hour,kWh\n2025,10,23,1,0.75\n")));
    }

    public static Task GeorgiaPowerRowErrors()
    {
        using var files = new TestFiles();
        var manager = new DataManager(files.StorageDirectory);
        var result = manager.ValidateFile(files.Csv("bad-row.csv", PreambleCsv + "Hour,kWh,Temp\n2025-01-01 00:00,-1,42\n"));
        Check(!result.IsValid && result.Errors.Any(x => x.Contains("Line 4:", StringComparison.Ordinal)), "utility errors should identify the original fourth line");
        foreach (var csv in new[]
        {
            "Unexpected export instructions,,\nHour,kWh,Temp\n2025-01-01 00:00,1,42\n",
            "Hour,Temp\nHour,kWh,Temp\n2025-01-01 00:00,1,42\n",
            "Hour,kWh,Temp\n2025-01-01 00:00,1,42\nAccount Number: " + FakeAccount + ",,\n",
            PreambleCsv + "Hour,kWh,Temp\n2025-01-01 00:00,1,42\nnot-a-date,1,42\n"
        })
            Check(!manager.ValidateFile(files.Csv(Guid.NewGuid() + ".csv", csv)).IsValid, "unknown or malformed rows must not be silently skipped");
        return Task.CompletedTask;
    }

    public static Task XlsxStringCells()
    {
        using var files = new TestFiles();
        var manager = new DataManager(files.StorageDirectory);
        var inline = Workbook(files, "inline.xlsx", UtilityRows());
        var inlineResult = manager.ValidateFile(inline);
        Valid(inlineResult);
        Equal(2, inlineResult.Data.Count, "inline XLSX record count");
        Equal(2.00m, inlineResult.Data.Sum(x => x.KWh), "inline XLSX energy");
        Equal<decimal?>(null, inlineResult.Data[1].Temp, "sparse C cell is an absent optional temperature");

        var shared = $"<sst xmlns=\"{MainNs}\" count=\"5\" uniqueCount=\"5\"><si><r><t>Ho</t></r><r><t>ur</t></r></si><si><t>kWh</t></si><si><t>Temp</t></si><si><t>2025-01-01 00:00</t></si><si><t>2025-01-01 01:00</t></si></sst>";
        var rows = Row(1, Shared("A1", 0) + Shared("B1", 1) + Shared("C1", 2)) +
                   Row(2, Shared("A2", 3) + Number("B2", "1.25") + Number("C2", "42")) +
                   Row(3, Shared("A3", 4) + Number("B3", "0.75"));
        var result = manager.ValidateFile(Workbook(files, "shared.xlsx", rows, shared));
        Valid(result);
        Check(inlineResult.Data.SequenceEqual(result.Data), "inline/shared readers must normalize to identical readings");
        return Task.CompletedTask;
    }

    public static Task XlsxSerialDates()
    {
        using var files = new TestFiles();
        var manager = new DataManager(files.StorageDirectory);
        var expected = new DateTime(2025, 1, 1, 13, 0, 0);
        foreach (var date1904 in new[] { false, true })
        {
            var serial = expected.ToOADate() - (date1904 ? 1462d : 0d);
            var rows = Headers() + Row(2, Number("A2", serial.ToString("R", CultureInfo.InvariantCulture), 1) + Number("B2", "1.25"));
            var result = manager.ValidateFile(Workbook(files, $"dates-{date1904}.xlsx", rows, date1904: date1904));
            Valid(result);
            Equal(expected, result.Data.Single().Timestamp, $"Excel {(date1904 ? 1904 : 1900)} date system");
        }
        var subhour = new DateTime(2025, 1, 1, 13, 30, 0).ToOADate().ToString("R", CultureInfo.InvariantCulture);
        Check(!manager.ValidateFile(Workbook(files, "subhour.xlsx", Headers() + Row(2, Number("A2", subhour, 1) + Number("B2", "1")))).IsValid,
            "formatted Excel timestamps must retain and reject subhour data");
        return Task.CompletedTask;
    }

    public static Task XlsxInvalidContent()
    {
        using var files = new TestFiles();
        var manager = new DataManager(files.StorageDirectory);
        var malformed = files.Csv("malformed.xlsx", "This is not an OOXML ZIP workbook.");
        Check(!manager.ValidateFile(malformed).IsValid, "malformed ZIP workbook must be rejected");
        foreach (var name in new[] { "old.xls", "macro.xlsm", "unrelated.txt" })
            Check(!manager.ValidateFile(files.Csv(name, "Hour,kWh\n2025-01-01 00:00,1\n")).IsValid, "unsupported extensions must be rejected");
        var invalidRows = new Dictionary<string, string>
        {
            ["formula"] = Headers() + Row(2, Inline("A2", "2025-01-01 00:00") + "<c r=\"B2\"><f>1+1</f><v>2</v></c>"),
            ["error"] = Headers() + Row(2, Inline("A2", "2025-01-01 00:00") + "<c r=\"B2\" t=\"e\"><v>#VALUE!</v></c>"),
            ["missing required cell"] = Headers() + Row(2, Inline("A2", "2025-01-01 00:00") + Number("C2", "42")),
            ["duplicate"] = Headers() + Row(2, Inline("A2", "2025-01-01 00:00") + Number("B2", "1")) + Row(3, Inline("A3", "2025-01-01 00:00") + Number("B3", "1")),
            ["unresolved shared string"] = Headers() + Row(2, Shared("A2", 999) + Number("B2", "1")),
            ["invalid serial date"] = Headers() + Row(2, Number("A2", "999999999", 1) + Number("B2", "1")),
            ["fictional Excel leap day"] = Headers() + Row(2, Number("A2", "60", 1) + Number("B2", "1"))
        };
        foreach (var (name, rows) in invalidRows)
        {
            var result = manager.ValidateFile(Workbook(files, Guid.NewGuid() + ".xlsx", rows));
            Check(!result.IsValid && result.Errors.Count > 0, name + " must be a readable validation error");
        }
        var ambiguous = manager.ValidateFile(Workbook(files, "ambiguous.xlsx", UtilityRows(), twoDataSheets: true));
        Check(!ambiguous.IsValid, "two usage worksheets require an explicit choice instead of silently selecting one");
        var external = manager.ValidateFile(Workbook(files, "external.xlsx", UtilityRows(), externalSheet: true));
        Check(!external.IsValid, "external worksheet relationships cannot be read");
        var oversized = Path.Combine(files.Root, "oversized.xlsx");
        using (var stream = File.Create(oversized)) stream.SetLength(UsageValidation.MaximumFileBytes + 1);
        var oversizedResult = manager.ValidateFile(oversized);
        Check(!oversizedResult.IsValid && oversizedResult.Errors.Any(x => x.Contains("10 MiB", StringComparison.Ordinal)), "compressed input file size must be bounded before parsing");
        var manyParts = Path.Combine(files.Root, "many-parts.xlsx");
        using (var archive = ZipFile.Open(manyParts, ZipArchiveMode.Create))
            for (var part = 0; part < 257; part++) archive.CreateEntry($"part-{part}.xml");
        var manyPartsResult = manager.ValidateFile(manyParts);
        Check(!manyPartsResult.IsValid && manyPartsResult.Errors.Any(x => x.Contains("too many", StringComparison.OrdinalIgnoreCase)), "ZIP package entry count must be bounded");
        return Task.CompletedTask;
    }

    public static async Task XlsxPersistence()
    {
        using var files = new TestFiles();
        var manager = new DataManager(files.StorageDirectory);
        var result = await manager.ImportFileAsync(Workbook(files, "good.xlsx", UtilityRows()));
        Valid(result);
        var stored = await File.ReadAllTextAsync(manager.DatabasePath);
        Check(!stored.Contains(FakeAccount, StringComparison.Ordinal), "account preamble must not be saved from XLSX");
        var invalid = await manager.ImportFileAsync(Workbook(files, "invalid.xlsx", Headers() + Row(2, Inline("A2", "2025-01-01 00:00") + Number("B2", "-1"))));
        Check(!invalid.IsValid, "invalid XLSX must be rejected");
        Equal(stored, await File.ReadAllTextAsync(manager.DatabasePath), "rejected XLSX retains saved data");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await manager.ImportFileAsync(Workbook(files, "cancelled.xlsx", UtilityRows()), cancellation.Token);
            throw new InvalidOperationException("cancelled XLSX import unexpectedly completed");
        }
        catch (OperationCanceledException) { }
        Equal(stored, await File.ReadAllTextAsync(manager.DatabasePath), "cancelled XLSX retains saved data");
    }

    public static async Task ViewModelXlsx()
    {
        using var files = new TestFiles();
        var path = Workbook(files, "gui.xlsx", UtilityRows());
        var controller = new AppController(new DataManager(files.StorageDirectory), new RateCalculator());
        using var vm = new MainViewModel(() => Task.FromResult<string?>(path), controller);
        await vm.ImportCommand.ExecuteAsync();
        Check(vm.HasData && !vm.IsStatusError && !vm.IsBusy, "XLSX GUI import failed: " + vm.StatusMessage);
        Equal(2, controller.CurrentData.Count, "GUI XLSX record count");
        Equal("$0.16", vm.EstimatedCostText, "XLSX usage reaches Residential Service calculation");
        Equal(1, vm.MonthlyBills.Count, "XLSX monthly breakdown");
        Check(vm.DataSource.Contains("gui.xlsx", StringComparison.Ordinal), "GUI source filename");
    }

    public static Task VerifySuppliedFiles(string csvPath, string xlsxPath)
    {
        using var files = new TestFiles();
        var manager = new DataManager(files.StorageDirectory);
        var csv = manager.ValidateFile(csvPath);
        var xlsx = manager.ValidateFile(xlsxPath);
        Valid(csv); Valid(xlsx);
        Equal(csv.Data.Count, xlsx.Data.Count, "supplied CSV/XLSX record counts");
        Check(csv.Data.SequenceEqual(xlsx.Data), "supplied CSV/XLSX timestamp, kWh, and temperature tuples differ");
        var plan = new ResidentialServicePlan();
        Equal(plan.CalculateCost(csv.Data).TotalCost, plan.CalculateCost(xlsx.Data).TotalCost, "supplied CSV/XLSX cost parity");
        Console.WriteLine($"PASS  Supplied Georgia Power exports match: {csv.Data.Count:N0} hourly records, {csv.Data.Sum(x => x.KWh):N2} kWh; normalized tuples and estimated costs agree.");
        return Task.CompletedTask;
    }

    private static string PreambleCsv => "\"Disclaimer: Usage may be estimated, see your bill.\",,\nAccount Number: " + FakeAccount + ",,\n";
    private static string UtilityRows() => Row(1, Inline("A1", "Disclaimer: Usage may be estimated, see your bill.")) +
        Row(2, Inline("A2", "Account Number: " + FakeAccount)) +
        Row(3, Inline("A3", "Hour") + Inline("B3", "kWh") + Inline("C3", "Temp")) +
        Row(4, Inline("A4", "2025-01-01 00:00") + Number("B4", "1.25") + Number("C4", "42")) +
        Row(5, Inline("A5", "2025-01-01 01:00") + Number("B5", "0.75"));
    private static string Headers() => Row(1, Inline("A1", "Hour") + Inline("B1", "kWh") + Inline("C1", "Temp"));
    internal static string Row(int row, string cells) => $"<row r=\"{row}\">{cells}</row>";
    internal static string Inline(string address, string value) => $"<c r=\"{address}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{SecurityElement.Escape(value)}</t></is></c>";
    private static string Shared(string address, int index) => $"<c r=\"{address}\" t=\"s\"><v>{index}</v></c>";
    internal static string Number(string address, string value, int style = 0) => $"<c r=\"{address}\" s=\"{style}\"><v>{value}</v></c>";

    internal static string Workbook(TestFiles files, string name, string rows, string? sharedStrings = null,
        bool date1904 = false, bool twoDataSheets = false, bool externalSheet = false)
    {
        var path = Path.Combine(files.Root, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Add("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/></Types>");
        Add("_rels/.rels", $"<Relationships xmlns=\"{RelNs}\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
        Add("xl/workbook.xml", $"<workbook xmlns=\"{MainNs}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><workbookPr date1904=\"{(date1904 ? "1" : "0")}\"/><sheets><sheet name=\"Usage\" sheetId=\"1\" r:id=\"usageSheet\"/>{(twoDataSheets ? "<sheet name=\"Other Usage\" sheetId=\"2\" r:id=\"otherSheet\"/>" : "")}</sheets></workbook>");
        Add("xl/_rels/workbook.xml.rels", $"<Relationships xmlns=\"{RelNs}\"><Relationship Id=\"usageSheet\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"{(externalSheet ? "https://example.invalid/usage.xml" : "worksheets/usage.xml")}\"{(externalSheet ? " TargetMode=\"External\"" : "")}/><Relationship Id=\"styles\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>{(sharedStrings is null ? "" : "<Relationship Id=\"strings\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings\" Target=\"sharedStrings.xml\"/>")}{(twoDataSheets ? "<Relationship Id=\"otherSheet\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/other.xml\"/>" : "")}</Relationships>");
        Add("xl/styles.xml", $"<styleSheet xmlns=\"{MainNs}\"><numFmts count=\"1\"><numFmt numFmtId=\"165\" formatCode=\"yyyy-mm-dd hh:mm\"/></numFmts><cellXfs count=\"2\"><xf numFmtId=\"0\"/><xf numFmtId=\"165\" applyNumberFormat=\"1\"/></cellXfs></styleSheet>");
        Add("xl/worksheets/usage.xml", $"<worksheet xmlns=\"{MainNs}\"><sheetData>{rows}</sheetData></worksheet>");
        if (twoDataSheets) Add("xl/worksheets/other.xml", $"<worksheet xmlns=\"{MainNs}\"><sheetData>{rows}</sheetData></worksheet>");
        if (sharedStrings is not null) Add("xl/sharedStrings.xml", sharedStrings);
        return path;

        void Add(string entryName, string xml)
        {
            using var writer = new StreamWriter(zip.CreateEntry(entryName).Open(), new UTF8Encoding(false));
            writer.Write(xml);
        }
    }

    private static void Valid(CsvValidationResult result) => Check(result.IsValid, string.Join("; ", result.Errors));
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual, string message) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"{message}: expected {expected}, got {actual}");
}
