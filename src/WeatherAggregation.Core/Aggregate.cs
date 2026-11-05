using System.Globalization;
using System.Text.Json;

namespace WeatherAggregation.Core;

public sealed record Aggregate(string Name, int Min, int Max, long Sum, long Count)
{
    public static Aggregate From(Measurement value) => new(StationNames.Normalize(value.StationName), value.Tenths, value.Tenths, value.Tenths, 1);
    public Aggregate Merge(Aggregate other) => new(
        StringComparer.Ordinal.Compare(Name, other.Name) <= 0 ? Name : other.Name,
        Math.Min(Min, other.Min), Math.Max(Max, other.Max), checked(Sum + other.Sum), checked(Count + other.Count));

    public string ToJsonLine()
    {
        var mean = decimal.Round(Sum / (10m * Count), 1, MidpointRounding.AwayFromZero);
        return $"{{\"stationName\":{JsonSerializer.Serialize(Name)},\"min\":{Format(Min / 10m)},\"mean\":{Format(mean)},\"max\":{Format(Max / 10m)}}}\n";
    }
    private static string Format(decimal value) => (value == 0 ? 0m : value).ToString("F1", CultureInfo.InvariantCulture);
}
