using System.Diagnostics;
using System.Globalization;

namespace WeatherAggregation.Client;

public static class ClientApplication
{
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error,
        HttpClient? client = null, CancellationToken cancellationToken = default)
    {
        error = TextWriter.Synchronized(error);
        if (args.Contains("--help"))
        {
            await output.WriteLineAsync("Options: --url http://localhost:5080 --measurements 100000 --stations 1000 --seed 42 --timeout-seconds 7200 --workload uniform|mixed|unique --fixture true --progress true\nAPI key: Authentication__ApiKey environment variable. --fixture true sends the README's three measurements. Progress is written to stderr.");
            return 0;
        }
        var ownsClient = client is null;
        try
        {
            var settings = new Dictionary<string, string>(StringComparer.Ordinal);
            var supported = new[] { "--url", "--measurements", "--stations", "--seed", "--timeout-seconds", "--workload", "--fixture", "--progress" };
            if (args.Length % 2 != 0) throw new ArgumentException("Each option requires a value; use --help.");
            for (var i = 0; i < args.Length; i += 2)
                if (!supported.Contains(args[i]) || !settings.TryAdd(args[i], args[i + 1])) throw new ArgumentException("Unknown or duplicate option; use --help.");
            string Get(string name, string fallback) => settings.GetValueOrDefault(name, fallback);
            var count = long.Parse(Get("--measurements", "100000"), CultureInfo.InvariantCulture);
            var stations = long.Parse(Get("--stations", "1000"), CultureInfo.InvariantCulture);
            var seed = int.Parse(Get("--seed", "42"), CultureInfo.InvariantCulture);
            var seconds = int.Parse(Get("--timeout-seconds", "7200"), CultureInfo.InvariantCulture);
            var workload = Get("--workload", "uniform");
            var fixture = bool.Parse(Get("--fixture", "false"));
            var showProgress = bool.Parse(Get("--progress", "true"));
            if (count is < 0 or > 1_000_000_000 || stations is < 1 or > 1_000_000_000 || seconds is < 1 or > 604800 || workload is not ("uniform" or "mixed" or "unique"))
                throw new ArgumentException("Counts, timeout, or workload are outside supported ranges.");
            var url = new Uri(Get("--url", "http://localhost:5080"));
            if (url.Scheme is not ("http" or "https")) throw new ArgumentException("Use an HTTP or HTTPS URL.");
            var key = Environment.GetEnvironmentVariable("Authentication__ApiKey");
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Set Authentication__ApiKey.");
            client ??= new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(url, "/measurements/aggregate"));
            request.Headers.Add("X-Api-Key", key);
            var requestId = Guid.NewGuid().ToString("N");
            request.Headers.Add("X-Request-Id", requestId);
            await error.WriteLineAsync($"Request ID: {requestId}");
            await using var progress = showProgress ? new ProgressReporter(error, fixture ? 3 : count) : null;
            request.Content = GeneratedContent.Create(count, stations, seed, workload, fixture,
                generated: progress is null ? null : progress.Generated, uploaded: progress is null ? null : progress.Uploaded);
            var watch = Stopwatch.StartNew();
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            progress?.ReceivingResults();
            if (response.Headers.TryGetValues("X-Request-Id", out var ids)) await error.WriteLineAsync($"Server request ID: {ids.First()}");
            if (!response.IsSuccessStatusCode)
            {
                var problem = await response.Content.ReadAsStringAsync(timeout.Token);
                await error.WriteLineAsync($"HTTP {(int)response.StatusCode}: {problem}");
                return 1;
            }
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
            var buffer = new char[16 * 1024];
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory(), timeout.Token)) > 0)
                await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
            await output.FlushAsync(timeout.Token);
            await error.WriteLineAsync($"Completed in {watch.Elapsed.TotalSeconds:F3}s.");
            return 0;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or ArgumentException or FormatException or OverflowException)
        {
            await error.WriteLineAsync($"Request failed: {ex.Message}");
            return 1;
        }
        finally { if (ownsClient) client?.Dispose(); }
    }
}
