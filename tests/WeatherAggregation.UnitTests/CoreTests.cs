using System.Globalization;
using System.Text;
using WeatherAggregation.Core;
using Xunit;

namespace WeatherAggregation.UnitTests;

public class CoreTests
{
    [Theory]
    [InlineData("hamburg", "Hamburg")]
    [InlineData("HAMBURG", "Hamburg")]
    [InlineData("new york", "New York")]
    [InlineData("NEW YORK", "New York")]
    [InlineData("st. john's", "St. John's")]
    [InlineData("ST. JOHN'S", "St. John's")]
    [InlineData("rio de janeiro", "Rio De Janeiro")]
    [InlineData("  ÉVRY-sur  SEINE", "  Évry-Sur  Seine")]
    [InlineData("st. JOHN’S", "St. John’s")]
    [InlineData("東京", "東京")]
    public void NormalizesWithoutLosingSeparators(string input, string expected) => Assert.Equal(expected, StationNames.Normalize(input));

    [Theory]
    [InlineData("{\"stationName\":\"A\",\"temperature\":-99.9}", -999)]
    [InlineData("{\"temperature\":99.9,\"stationName\":\"A\"}", 999)]
    [InlineData("{\"stationName\":\"A\",\"temperature\":-0.0}", 0)]
    public void ParsesExactTenths(string json, int expected) => Assert.Equal(expected, MeasurementParser.Parse(Encoding.UTF8.GetBytes(json), 1).Tenths);

    [Theory]
    [InlineData("12")][InlineData("12.00")][InlineData("1.2e1")][InlineData("100.0")]
    [InlineData("-100.0")][InlineData("\"1.0\"")][InlineData("null")]
    public void RejectsInvalidTemperatures(string token) => Reject($"{{\"stationName\":\"A\",\"temperature\":{token}}}");

    [Theory]
    [InlineData("")][InlineData("{}")][InlineData("[]")]
    [InlineData("{\"stationName\":\"\",\"temperature\":1.0}")]
    [InlineData("{\"stationName\":\"A\",\"stationName\":\"B\",\"temperature\":1.0}")]
    [InlineData("{\"stationName\":\"A\",\"temperature\":1.0,\"temperature\":2.0}")]
    [InlineData("{\"stationName\":\"A\",\"temperature\":1.0,\"extra\":true}")]
    [InlineData("{\"stationName\":null,\"temperature\":1.0}")]
    [InlineData("{\"stationName\":\"A\",\"temperature\":1.0,}")]
    [InlineData("{\"stationName\":\"A\",\"temperature\":1.0} {}")]
    [InlineData("{\"stationName\":\"\\uD800\",\"temperature\":1.0}")]
    public void RejectsMalformedSchema(string json) => Reject(json);

    [Fact]
    public void ValidatesUtf8BytesRatherThanCharacters()
    {
        foreach (var name in new[] { "a", new string('a', 100), new string('é', 50), " " })
            Assert.Equal(name, MeasurementParser.Parse(Encoding.UTF8.GetBytes($"{{\"stationName\":\"{name}\",\"temperature\":1.0}}"), 1).StationName);
        Reject($"{{\"stationName\":\"{new string('é', 50)}a\",\"temperature\":1.0}}");
        Reject($"{{\"stationName\":\"{new string('a', 101)}\",\"temperature\":1.0}}");
        Assert.Throws<InputException>(() => MeasurementParser.Parse([0xff], 1));
        Reject("\ufeff{\"stationName\":\"A\",\"temperature\":1.0}");
    }

    [Theory]
    [InlineData(1, 2, "0.1")][InlineData(-1, 2, "-0.1")][InlineData(0, 2, "0.0")]
    [InlineData(999000000000L, 1000000000L, "99.9")]
    public void RoundsOnceAndSupportsLargeSums(long sum, long count, string expected)
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            Assert.Contains($"\"mean\":{expected}", new Aggregate("Istanbul", 0, 999, sum, count).ToJsonLine());
            Assert.Equal("Istanbul", StationNames.Normalize("ISTANBUL"));
        }
        finally { CultureInfo.CurrentCulture = old; }
    }

    [Fact]
    public async Task FramesBoundariesCrLfAndFinalLine()
    {
        await using var stream = new TinyStream(Encoding.UTF8.GetBytes("abc\r\ndef\nghi"));
        var lines = new List<string>();
        await foreach (var line in JsonLines.ReadAsync(stream, 3)) lines.Add(Encoding.UTF8.GetString(line.Span));
        Assert.Equal(new[] { "abc", "def", "ghi" }, lines);
    }

    [Fact]
    public async Task RejectsOversizedLinesBeforeUnboundedAllocation()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("a\n12345\n"));
        var error = await Assert.ThrowsAsync<InputException>(async () => { await foreach (var _ in JsonLines.ReadAsync(stream, 3)) { } });
        Assert.Equal(2, error.LineNumber);
        Assert.Equal(413, error.Status);
    }

    private static void Reject(string json) => Assert.Equal(7, Assert.Throws<InputException>(() => MeasurementParser.Parse(Encoding.UTF8.GetBytes(json), 7)).LineNumber);

    private sealed class TinyStream(byte[] data) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => base.ReadAsync(buffer[..Math.Min(2, buffer.Length)], cancellationToken);
    }
}
