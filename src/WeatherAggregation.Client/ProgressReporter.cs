using System.Diagnostics;
using System.Globalization;

namespace WeatherAggregation.Client;

public sealed class ProgressReporter : IAsyncDisposable
{
    private readonly TextWriter writer;
    private readonly long total;
    private readonly Stopwatch watch = Stopwatch.StartNew();
    private readonly CancellationTokenSource stop = new();
    private readonly Task loop;
    private long generated, uploaded;
    private string phase = "Uploading / awaiting results";
    public ProgressReporter(TextWriter writer, long total)
    {
        this.writer = writer;
        this.total = total;
        loop = ReportAsync();
    }
    public void Generated(long count) => Interlocked.Add(ref generated, count);
    public void Uploaded(int bytes) => Interlocked.Add(ref uploaded, bytes);
    public void ReceivingResults() => Volatile.Write(ref phase, "Receiving results");

    private async Task ReportAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(stop.Token)) await WriteAsync();
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    private Task WriteAsync()
    {
        var rows = Interlocked.Read(ref generated);
        var bytes = Interlocked.Read(ref uploaded);
        var seconds = watch.Elapsed.TotalSeconds;
        var percent = total == 0 ? 100 : 100d * rows / total;
        return writer.WriteLineAsync(FormattableString.Invariant($"{Volatile.Read(ref phase)}: generated {rows}/{total} ({percent:F1}%); uploaded {bytes / 1048576d:F2} MiB; elapsed {seconds:F1}s; {rows / Math.Max(seconds, 0.001):F0} rows/s, {bytes / 1048576d / Math.Max(seconds, 0.001):F2} MiB/s."));
    }

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        await loop;
        await WriteAsync();
        stop.Dispose();
    }
}
