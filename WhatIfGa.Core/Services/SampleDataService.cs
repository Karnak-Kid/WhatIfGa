using WhatIfGa.Core.Models;

namespace WhatIfGa.Core.Services;

public static class SampleDataService
{
    /// <summary>Deterministic synthetic hourly data; never represents the user's actual history.</summary>
    public static IReadOnlyList<UsageData> CreateYear()
    {
        var start = new DateTime(2025, 1, 1);
        var end = start.AddYears(1);
        var data = new List<UsageData>(8760);
        for (var timestamp = start; timestamp < end; timestamp = timestamp.AddHours(1))
        {
            var season = (Math.Cos((timestamp.DayOfYear - 205) * 2 * Math.PI / 365) + 1) / 2;
            var evening = timestamp.Hour is >= 16 and <= 21 ? 0.8 : 0;
            var morning = timestamp.Hour is >= 6 and <= 8 ? 0.3 : 0;
            var weekend = timestamp.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? 0.15 : 0;
            var variation = Math.Sin(timestamp.DayOfYear * 1.7 + timestamp.Hour * 0.9) * 0.1;
            var kwh = decimal.Round((decimal)(0.55 + season * 0.7 + evening + morning + weekend + variation), 3);
            var temp = decimal.Round((decimal)(45 + season * 40 + Math.Sin(timestamp.Hour * Math.PI / 12) * 5), 1);
            data.Add(new UsageData(timestamp, kwh, temp));
        }
        return data.AsReadOnly();
    }
}
