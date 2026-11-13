using System.Globalization;

const decimal SuperOffPeakRate = 0.023m;
const decimal OffPeakRate = 0.104m;
const decimal OnPeakRate = 0.303m;

string path = args.Length > 0 ? args[0] : "usage.csv";

if (!File.Exists(path))
{
    Console.WriteLine($"File not found: {path}");
    Console.WriteLine("Expected CSV columns: start,end,kWh");
    return;
}

var kWhByPeriod = new Dictionary<string, decimal>
{
    ["Super Off-Peak"] = 0m,
    ["Off-Peak"] = 0m,
    ["On-Peak"] = 0m
};

var costByPeriod = new Dictionary<string, decimal>
{
    ["Super Off-Peak"] = 0m,
    ["Off-Peak"] = 0m,
    ["On-Peak"] = 0m
};

decimal totalKWh = 0m;
int rowsProcessed = 0;
int lineNumber = 0;

foreach (string rawLine in File.ReadLines(path))
{
    lineNumber++;
    string line = rawLine.Trim();

    if (string.IsNullOrWhiteSpace(line))
        continue;

    // Skip the expected header row.
    if (lineNumber == 1 &&
        line.StartsWith("start,", StringComparison.OrdinalIgnoreCase))
        continue;

    string[] fields = line.Split(',');

    if (fields.Length != 3)
    {
        Console.WriteLine($"Invalid CSV row {lineNumber}; expected 3 columns.");
        return;
    }

    if (!DateTime.TryParse(fields[0].Trim(), CultureInfo.InvariantCulture,
            DateTimeStyles.None, out DateTime start) ||
        !DateTime.TryParse(fields[1].Trim(), CultureInfo.InvariantCulture,
            DateTimeStyles.None, out DateTime end) ||
        !decimal.TryParse(fields[2].Trim(), NumberStyles.Number,
            CultureInfo.InvariantCulture, out decimal intervalKWh))
    {
        Console.WriteLine($"Could not read CSV row {lineNumber}.");
        return;
    }

    if (end <= start || intervalKWh < 0m)
    {
        Console.WriteLine($"Invalid interval or kWh value on row {lineNumber}.");
        return;
    }

    // Split intervals at rate-schedule boundaries. If an interval spans
    // a boundary, usage is apportioned evenly across that interval.
    DateTime cursor = start;

    while (cursor < end)
    {
        DateTime segmentEnd = NextBoundary(cursor);
        if (segmentEnd > end)
            segmentEnd = end;

        decimal segmentKWh =
            intervalKWh * (decimal)(segmentEnd - cursor).Ticks
            / (decimal)(end - start).Ticks;

        string period = GetPeriod(cursor);
        decimal rate = GetRate(period);

        kWhByPeriod[period] += segmentKWh;
        costByPeriod[period] += segmentKWh * rate;
        totalKWh += segmentKWh;

        cursor = segmentEnd;
    }

    rowsProcessed++;
}

if (rowsProcessed == 0)
{
    Console.WriteLine("No usage rows found.");
    return;
}

decimal actualCost = costByPeriod.Values.Sum();
decimal theoreticalOvernightCost = totalKWh * SuperOffPeakRate;
decimal theoreticalMaximumSavings = actualCost - theoreticalOvernightCost;

Console.WriteLine($"Rows processed: {rowsProcessed}");
Console.WriteLine($"Total usage:    {totalKWh:N2} kWh");
Console.WriteLine();

foreach (string period in new[] { "Super Off-Peak", "Off-Peak", "On-Peak" })
{
    Console.WriteLine(
        $"{period,-17} {kWhByPeriod[period],10:N2} kWh  " +
        $"{costByPeriod[period],10:C2}");
}

Console.WriteLine(new string('-', 48));
Console.WriteLine($"Estimated energy cost:              {actualCost:C2}");
Console.WriteLine($"Theoretical all-overnight cost:      {theoreticalOvernightCost:C2}");
Console.WriteLine($"Theoretical maximum shift savings:   {theoreticalMaximumSavings:C2}");

Console.WriteLine();
Console.WriteLine("Strategy:");
Console.WriteLine("- Prioritize shifting flexible use to 11 p.m.–7 a.m.");
Console.WriteLine("- Avoid flexible use on Jun–Sep weekdays from 2 p.m.–7 p.m.");
Console.WriteLine("- Treat the maximum savings figure as an upper bound.");

static DateTime NextBoundary(DateTime time)
{
    DateTime[] boundaries =
    {
        time.Date.AddHours(7),
        time.Date.AddHours(14),
        time.Date.AddHours(19),
        time.Date.AddHours(23),
        time.Date.AddDays(1)
    };

    return boundaries.Where(boundary => boundary > time).Min();
}

static string GetPeriod(DateTime time)
{
    TimeSpan clock = time.TimeOfDay;

    if (clock >= TimeSpan.FromHours(23) ||
        clock < TimeSpan.FromHours(7))
        return "Super Off-Peak";

    bool summerWeekday =
        time.Month >= 6 &&
        time.Month <= 9 &&
        time.DayOfWeek != DayOfWeek.Saturday &&
        time.DayOfWeek != DayOfWeek.Sunday;

    if (summerWeekday &&
        clock >= TimeSpan.FromHours(14) &&
        clock < TimeSpan.FromHours(19))
        return "On-Peak";

    return "Off-Peak";
}

static decimal GetRate(string period) => period switch
{
    "Super Off-Peak" => SuperOffPeakRate,
    "On-Peak" => OnPeakRate,
    _ => OffPeakRate
};
