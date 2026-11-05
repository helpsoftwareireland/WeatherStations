using System.Text;
using System.Text.Json;

namespace WeatherAggregation.Core;

public readonly record struct Measurement(string StationName, int Tenths);

public static class MeasurementParser
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static Measurement Parse(ReadOnlySpan<byte> line, long lineNumber)
    {
        try
        {
            // Validate encoding before JSON decoding, including escaped unpaired surrogates below.
            StrictUtf8.GetCharCount(line);
            var reader = new Utf8JsonReader(line, new JsonReaderOptions { MaxDepth = 4 });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) throw Invalid();
            string? station = null;
            int? tenths = null;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName) throw Invalid();
                var name = reader.GetString();
                if (!reader.Read()) throw Invalid();
                if (name == "stationName" && station is null && reader.TokenType == JsonTokenType.String)
                {
                    station = reader.GetString()!;
                    if (StrictUtf8.GetByteCount(station) is < 1 or > 100) throw Invalid();
                }
                else if (name == "temperature" && tenths is null && reader.TokenType == JsonTokenType.Number)
                {
                    var token = reader.ValueSpan;
                    var offset = token[0] == (byte)'-' ? 1 : 0;
                    if (token.Length < offset + 3 || token[^2] != (byte)'.') throw Invalid();
                    for (var i = offset; i < token.Length; i++)
                        if (i != token.Length - 2 && token[i] is < (byte)'0' or > (byte)'9') throw Invalid();
                    if (!reader.TryGetDecimal(out var value) || value is < -99.9m or > 99.9m) throw Invalid();
                    tenths = (int)(value * 10);
                }
                else throw Invalid();
            }
            if (reader.TokenType != JsonTokenType.EndObject || reader.Read() || station is null || tenths is null)
                throw Invalid();
            return new(station, tenths.Value);
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException or InvalidOperationException or ArgumentException)
        {
            throw Invalid();
        }

        InputException Invalid() => new("invalid_measurement", "Expected a stationName of 1–100 UTF-8 bytes and a temperature from -99.9 to 99.9 with exactly one fractional digit.", lineNumber);
    }
}
