using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using WeatherAggregation.Client;
using WeatherAggregation.Testing;

var count = args.Length > 0 ? long.Parse(args[0], CultureInfo.InvariantCulture) : 100_000;
if (count is < 100 or > 1_000_000_000) throw new ArgumentException("Measurement count must be from 100 to 1,000,000,000.");
Console.WriteLine(JsonSerializer.Serialize(new { runtime = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(), processors = Environment.ProcessorCount, seed = 42, batchEntries = 1000, cacheKiB = 8192 }));
foreach (var (rows, stations, workload) in new[] { (count / 10, 10L, "uniform"), (count, 10L, "uniform"), (count, Math.Max(10, count / 10), "mixed"), (count, count, "unique") })
{
    await using var server = await ApiProcess.StartAsync();
    using var client = server.CreateClient();
    using var content = GeneratedContent.Create(rows, stations, 42, workload);
    using var request = new HttpRequestMessage(HttpMethod.Post, "/measurements/aggregate") { Content = content };
    using var monitoring = new CancellationTokenSource();
    long peakDisk = 0;
    var sample = Task.Run(async () =>
    {
        while (!monitoring.IsCancellationRequested)
        {
            try
            {
                var bytes = Directory.Exists(server.TempRoot) ? Directory.EnumerateFiles(server.TempRoot, "*", SearchOption.AllDirectories).Sum(p => { try { return new FileInfo(p).Length; } catch (IOException) { return 0; } }) : 0;
                peakDisk = Math.Max(peakDisk, bytes);
            }
            catch (IOException) { }
            try { await Task.Delay(20, monitoring.Token); } catch (OperationCanceledException) { break; }
        }
    });
    var watch = Stopwatch.StartNew();
    try
    {
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        var headersSeconds = watch.Elapsed.TotalSeconds;
        response.EnsureSuccessStatusCode();
        await response.Content.CopyToAsync(Stream.Null);
        watch.Stop();
        server.Process.Refresh();
        using var self = Process.GetCurrentProcess();
        Console.WriteLine(JsonSerializer.Serialize(new { rows, stations, workload, inputBytes = content.BytesWritten, seconds = watch.Elapsed.TotalSeconds, headersSeconds, responseSeconds = watch.Elapsed.TotalSeconds - headersSeconds, rowsPerSecond = rows / watch.Elapsed.TotalSeconds, serverPeakWorkingSetBytes = server.Process.PeakWorkingSet64, clientLifetimePeakWorkingSetBytes = self.PeakWorkingSet64, sampledPeakTemporaryBytes = peakDisk }));
    }
    finally { monitoring.Cancel(); await sample; }
}
