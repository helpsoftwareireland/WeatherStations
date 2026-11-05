using System.Diagnostics;
using System.Net.Http;

namespace WeatherAggregation.Testing;

public sealed class ApiProcess : IAsyncDisposable
{
    public const string Key = "local-automated-test-key";
    public Process Process { get; }
    public string TempRoot { get; } = Path.Combine(Path.GetTempPath(), "weather-process-" + Guid.NewGuid().ToString("N"));
    public Uri Address { get; private set; } = null!;

    private ApiProcess(Process process) => Process = process;

    public static async Task<ApiProcess> StartAsync(int timeoutSeconds = 7200, int maxConcurrentRequests = 2)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Broadridge.sln"))) root = root.Parent;
        if (root is null) throw new InvalidOperationException("Run from a checkout containing Broadridge.sln.");
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = root.FullName };
        start.ArgumentList.Add(Path.Combine(root.FullName, "src", "WeatherAggregation.Api", "bin", configuration, "net10.0", "WeatherAggregation.Api.dll"));
        start.ArgumentList.Add("--urls");
        start.ArgumentList.Add("http://127.0.0.1:0");
        var server = new ApiProcess(new Process { StartInfo = start });
        start.Environment["Authentication__ApiKey"] = Key;
        start.Environment["Aggregation__TempRoot"] = server.TempRoot;
        start.Environment["Aggregation__BatchEntries"] = "1000";
        start.Environment["Aggregation__TimeoutSeconds"] = timeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["Aggregation__MaxConcurrentRequests"] = maxConcurrentRequests.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["Logging__LogLevel__Default"] = "Information";
        var listening = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        var diagnostics = new System.Text.StringBuilder();
        server.Process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not { } line) return;
            var index = line.IndexOf("http://127.0.0.1:", StringComparison.Ordinal);
            if (index >= 0 && Uri.TryCreate(line[index..].Trim(), UriKind.Absolute, out var address)) listening.TrySetResult(address);
        };
        server.Process.ErrorDataReceived += (_, e) => { lock (diagnostics) { if (diagnostics.Length < 8000) diagnostics.AppendLine(e.Data); } };
        try
        {
            server.Process.Start();
            server.Process.BeginOutputReadLine();
            server.Process.BeginErrorReadLine();
            server.Address = await listening.Task.WaitAsync(TimeSpan.FromSeconds(30));
            return server;
        }
        catch (Exception ex)
        {
            await server.DisposeAsync();
            throw new InvalidOperationException("Could not start Kestrel: " + diagnostics, ex);
        }
    }

    public HttpClient CreateClient()
    {
        var client = new HttpClient { BaseAddress = Address, Timeout = TimeSpan.FromHours(24) };
        client.DefaultRequestHeaders.Add("X-Api-Key", Key);
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        if (Process.Id != 0 && !Process.HasExited)
        {
            Process.Kill(entireProcessTree: true);
            await Process.WaitForExitAsync();
        }
        Process.Dispose();
        if (Directory.Exists(TempRoot)) Directory.Delete(TempRoot, true);
    }
}
