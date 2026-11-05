using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace WeatherAggregation.Client;

public sealed class GeneratedContent(long measurements, long stations, int seed = 42, string workload = "uniform", bool fixture = false,
    Action<long>? generated = null, Action<int>? uploaded = null) : HttpContent
{
    public long BytesWritten { get; private set; }
    protected override bool TryComputeLength(out long length) { length = 0; return false; }
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => WriteAsync(stream, CancellationToken.None);
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) => WriteAsync(stream, cancellationToken);

    public static GeneratedContent Create(long measurements, long stations, int seed = 42, string workload = "uniform", bool fixture = false,
        Action<long>? generated = null, Action<int>? uploaded = null)
    {
        var content = new GeneratedContent(measurements, stations, seed, workload, fixture, generated, uploaded);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-ndjson");
        return content;
    }

    private async Task WriteAsync(Stream stream, CancellationToken cancellationToken)
    {
        BytesWritten = 0;
        using var counting = new UploadStream(stream, uploaded);
        await using var writer = new StreamWriter(counting, new UTF8Encoding(false), 64 * 1024, leaveOpen: true);
        if (fixture)
        {
            const string data = "{\"stationName\":\"Hamburg\",\"temperature\":12.0}\n{\"stationName\":\"Bulawayo\",\"temperature\":8.9}\n{\"stationName\":\"HAMBURG\",\"temperature\":34.2}\n";
            await writer.WriteAsync(data.AsMemory(), cancellationToken);
            BytesWritten = Encoding.UTF8.GetByteCount(data);
            generated?.Invoke(3);
        }
        else
        {
            var random = new Random(seed);
            for (long i = 0; i < measurements; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = workload switch
                {
                    "unique" => i,
                    "mixed" when random.Next(10) < 8 => random.NextInt64(Math.Min(10, stations)),
                    _ => i % stations
                };
                var name = $"{(i % 2 == 0 ? "station" : "STATION")} {id.ToString("D10", CultureInfo.InvariantCulture)}";
                var temperature = (random.Next(-999, 1000) / 10m).ToString("F1", CultureInfo.InvariantCulture);
                var line = $"{{\"stationName\":\"{name}\",\"temperature\":{temperature}}}\n";
                await writer.WriteAsync(line.AsMemory(), cancellationToken);
                BytesWritten += line.Length; // Generated records are ASCII.
                if ((i + 1) % 1024 == 0) generated?.Invoke(1024);
            }
            generated?.Invoke(measurements % 1024);
        }
        await writer.FlushAsync(cancellationToken);
    }

    private sealed class UploadStream(Stream inner, Action<int>? report) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) { inner.Write(buffer, offset, count); report?.Invoke(count); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(buffer, cancellationToken);
            report?.Invoke(buffer.Length);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        // The HTTP transport owns inner; disposing this wrapper leaves it open.
    }
}
