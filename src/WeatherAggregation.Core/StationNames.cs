using System.Globalization;
using System.Text;

namespace WeatherAggregation.Core;

public static class StationNames
{
    public static string Normalize(string value)
    {
        var runes = value.EnumerateRunes().ToArray();
        var result = new StringBuilder(value.Length);
        var start = true;
        for (var i = 0; i < runes.Length; i++)
        {
            var rune = runes[i];
            result.Append((start ? Rune.ToUpperInvariant(rune) : Rune.ToLowerInvariant(rune)).ToString());
            var internalApostrophe = rune.Value is 0x27 or 0x2019 && i > 0 && i + 1 < runes.Length
                && Rune.IsLetter(runes[i - 1]) && Rune.IsLetter(runes[i + 1]);
            start = !internalApostrophe && (Rune.IsWhiteSpace(rune) || Rune.IsPunctuation(rune));
        }
        return result.ToString();
    }

    public static int CompareOutput(string? x, string? y)
    {
        var comparison = StringComparer.OrdinalIgnoreCase.Compare(x, y);
        return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(x, y);
    }
}
