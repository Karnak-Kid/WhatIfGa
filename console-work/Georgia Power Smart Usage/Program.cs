using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace WhatIfGa;

public record HourlyUsage(DateTime StartTime, double Kwh);

public class SmartUsageCalculator
{
    private const decimal OnPeakRate = 0.146m;
    private const decimal OffPeakRate = 0.016m;
    private const decimal DemandRate = 12.44m;

    public static bool IsOnPeak(DateTime dateTime)
    {
        if (dateTime.Month < 6 || dateTime.Month > 9)
            return false;

        if (dateTime.DayOfWeek == DayOfWeek.Saturday ||
            dateTime.DayOfWeek == DayOfWeek.Sunday)
            return false;

        if (dateTime.Month == 7 && dateTime.Day == 4)
            return false;

        if (IsLaborDay(dateTime))
            return false;

        return dateTime.Hour >= 14 && dateTime.Hour < 19;
    }

    private static bool IsLaborDay(DateTime date)
    {
        if (date.Month != 9)
            return false;

        DateTime firstDay = new(date.Year, 9, 1);

        int offset = ((int)DayOfWeek.Monday -
            (int)firstDay.DayOfWeek + 7) % 7;

        return date.Date == firstDay.AddDays(offset).Date;
    }

    public static decimal CalculateBill(
        List<HourlyUsage> usage,
        out decimal onPeakCharge,
        out decimal offPeakCharge,
        out decimal demandCharge,
        out double maximumDemand)
    {
        if (usage.Count == 0)
            throw new ArgumentException("No usage records provided.");

        if (usage.Any(x => x.Kwh < 0 ||
            double.IsNaN(x.Kwh) || double.IsInfinity(x.Kwh)))
            throw new ArgumentException("Invalid usage value.");

        decimal onPeakKwh = 0;
        decimal offPeakKwh = 0;

        foreach (var hour in usage)
        {
            decimal kwh = (decimal)hour.Kwh;

            if (IsOnPeak(hour.StartTime))
                onPeakKwh += kwh;
            else
                offPeakKwh += kwh;
        }

        maximumDemand = usage.Max(x => x.Kwh);
        onPeakCharge = onPeakKwh * OnPeakRate;
        offPeakCharge = offPeakKwh * OffPeakRate;
        demandCharge = (decimal)maximumDemand * DemandRate;

        return onPeakCharge + offPeakCharge + demandCharge;
    }

    public static List<HourlyUsage> LoadCsv(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"File not found: {filePath}");

        var records = new List<HourlyUsage>();

        foreach (string line in File.ReadAllLines(filePath))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            string[] columns = line.Split(',');

            if (columns.Length != 2)
                throw new FormatException($"Invalid CSV row: {line}");

            if (columns[0].Trim().Equals(
                "Timestamp", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!DateTime.TryParse(
                columns[0].Trim(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTime timestamp))
                throw new FormatException($"Invalid timestamp: {columns[0]}");

            if (!double.TryParse(
                columns[1].Trim(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double kwh))
                throw new FormatException($"Invalid usage: {columns[1]}");

            if (kwh < 0 || double.IsNaN(kwh) || double.IsInfinity(kwh))
                throw new FormatException("Usage must be nonnegative.");

            records.Add(new HourlyUsage(timestamp, kwh));
        }

        records = records.OrderBy(x => x.StartTime).ToList();

        if (records.Count == 0)
            throw new InvalidOperationException("No usage records found.");

        for (int i = 1; i < records.Count; i++)
        {
            if (records[i].StartTime - records[i - 1].StartTime
                != TimeSpan.FromHours(1))
                throw new InvalidOperationException(
                    "Records must be consecutive hourly intervals.");
        }

        return records;
    }
}

public class Program
{
    public static void Main(string[] args)
    {
        Console.WriteLine("======================================");
        Console.WriteLine(" GEORGIA POWER SMART USAGE EVALUATOR");
        Console.WriteLine("======================================");

        string filePath = args.Length > 0 ? args[0] : "usage.csv";

        try
        {
            List<HourlyUsage> usage =
                SmartUsageCalculator.LoadCsv(filePath);

            decimal total = SmartUsageCalculator.CalculateBill(
                usage,
                out decimal onPeak,
                out decimal offPeak,
                out decimal demand,
                out double maxDemand);

            Console.WriteLine("\nSMART USAGE BILL ESTIMATE");
            Console.WriteLine("--------------------------------------");
            Console.WriteLine($"Hours analyzed: {usage.Count}");
            Console.WriteLine($"On-peak energy charge: {onPeak:C}");
            Console.WriteLine($"Off-peak energy charge: {offPeak:C}");
            Console.WriteLine($"Maximum demand: {maxDemand:F2} kW");
            Console.WriteLine($"Demand charge: {demand:C}");
            Console.WriteLine("--------------------------------------");
            Console.WriteLine($"Estimated total: {total:C}");

            Console.WriteLine("\nUSAGE RECOMMENDATIONS");

            Console.WriteLine(maxDemand > 7
                ? "Consider staggering major appliances to reduce demand."
                : "Maximum demand is at or below 7 kW.");

            if (onPeak > 0)
                Console.WriteLine(
                    "Consider shifting summer weekday usage outside peak hours.");

            Console.WriteLine(
                "\nNote: Estimate excludes additional utility fees and taxes.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\nError: {ex.Message}");
        }
    }
}
