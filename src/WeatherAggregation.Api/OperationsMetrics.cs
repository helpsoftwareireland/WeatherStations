using System.Diagnostics.Metrics;
using WeatherAggregation.Storage;

namespace WeatherAggregation.Api;

public sealed class OperationsMetrics : IDisposable
{
    public const string MeterName = "WeatherAggregation";
    private readonly Meter meter = new(MeterName, "1.0.0");
    private readonly Counter<long> requestsCounter;
    private readonly Counter<long> rejectedCounter;
    private readonly Histogram<double> duration;
    private readonly AggregationOptions options;
    private long requests, rejected, active, measurements;
    private double totalSeconds;
    private readonly object durationLock = new();

    public OperationsMetrics(AggregationOptions options)
    {
        this.options = options;
        requestsCounter = meter.CreateCounter<long>("weather.requests", "{request}");
        rejectedCounter = meter.CreateCounter<long>("weather.requests.rejected", "{request}");
        duration = meter.CreateHistogram<double>("weather.request.duration", "s");
        meter.CreateObservableGauge("weather.aggregations.active", () => Interlocked.Read(ref active), "{request}");
        meter.CreateObservableCounter("weather.measurements.accepted", () => Interlocked.Read(ref measurements), "{measurement}");
        meter.CreateObservableGauge("weather.storage.temporary", TemporaryBytes, "By");
    }

    public void BeginAggregation() => Interlocked.Increment(ref active);
    public void EndAggregation() => Interlocked.Decrement(ref active);
    public void AddMeasurements(long count) => Interlocked.Add(ref measurements, count);

    public void Record(string route, int status, double seconds)
    {
        Interlocked.Increment(ref requests);
        requestsCounter.Add(1, new("route", route), new("status", status));
        if (status >= 400)
        {
            Interlocked.Increment(ref rejected);
            rejectedCounter.Add(1, new("route", route), new("status", status));
        }
        duration.Record(seconds, new KeyValuePair<string, object?>("route", route));
        lock (durationLock) totalSeconds += seconds;
    }

    public object Snapshot()
    {
        double elapsed;
        lock (durationLock) elapsed = totalSeconds;
        return new { completedRequests = Interlocked.Read(ref requests), rejectedRequests = Interlocked.Read(ref rejected),
            activeAggregations = Interlocked.Read(ref active), acceptedMeasurements = Interlocked.Read(ref measurements),
            totalRequestSeconds = elapsed, temporaryStorageBytes = TemporaryBytes(), meter = MeterName };
    }

    private long TemporaryBytes()
    {
        long bytes = 0;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(options.TempRoot))
            {
                if (!Guid.TryParseExact(Path.GetFileName(dir), "N", out _) || (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) continue;
                foreach (var name in new[] { "aggregates.db", "aggregates.db-journal" })
                {
                    var file = new FileInfo(Path.Combine(dir, name));
                    if (file.Exists && (file.Attributes & FileAttributes.ReparsePoint) == 0) bytes += file.Length;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Approximate sample during concurrent cleanup. */ }
        return bytes;
    }

    public void Dispose() => meter.Dispose();
}
