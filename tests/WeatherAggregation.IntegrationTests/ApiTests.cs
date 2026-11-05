using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using WeatherAggregation.Client;
using WeatherAggregation.Testing;
using Xunit;

namespace WeatherAggregation.IntegrationTests;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "weather-api-tests-" + Guid.NewGuid().ToString("N"));
    public string ApiKey { get; set; } = "test-key";
    public long DiskBytes { get; set; } = 30L * 1024 * 1024 * 1024;
    public int TimeoutSeconds { get; set; } = 30;
    public long MaxMeasurements { get; set; } = 1_000_000_000;
    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder
        .UseSetting("Authentication:ApiKey", ApiKey)
        .UseSetting("Aggregation:TempRoot", Root)
        .UseSetting("Aggregation:BatchEntries", "2")
        .UseSetting("Aggregation:MaxConcurrentRequests", "1")
        .UseSetting("Aggregation:TimeoutSeconds", TimeoutSeconds.ToString())
        .UseSetting("Aggregation:MaxMeasurements", MaxMeasurements.ToString())
        .UseSetting("Aggregation:MaxDiskBytes", DiskBytes.ToString());
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(Root)) Directory.Delete(Root, true);
    }
    public HttpClient AuthorizedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "test-key");
        return client;
    }
}

public class ApiTests
{
    [Fact]
    public async Task FixtureProducesExactSortedJsonlAndCleansUp()
    {
        using var factory = new ApiFactory();
        using var client = factory.AuthorizedClient();
        using var response = await client.PostAsync("/measurements/aggregate", GeneratedContent.Create(3, 2, fixture: true));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("{\"stationName\":\"Bulawayo\",\"min\":8.9,\"mean\":8.9,\"max\":8.9}\n{\"stationName\":\"Hamburg\",\"min\":12.0,\"mean\":23.1,\"max\":34.2}\n", await response.Content.ReadAsStringAsync());
        Assert.Empty(Directory.GetDirectories(factory.Root));
        using var empty = await client.PostAsync("/measurements/aggregate", Body(""));
        Assert.Equal("", await empty.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AuthenticationProtectsEveryRoute()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        foreach (var path in new[] { "/health", "/measurements/aggregate", "/unknown" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        client.DefaultRequestHeaders.Add("X-Api-Key", "wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/health")).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Api-Key");
        client.DefaultRequestHeaders.Add("X-Api-Key", new[] { "test-key", "test-key" });
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public void MissingKeyPreventsStartup()
    {
        using var factory = new ApiFactory { ApiKey = "" };
        Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
    }

    [Fact]
    public async Task LateFailureReturnsOnlyProblemWithLineNumber()
    {
        using var factory = new ApiFactory();
        using var client = factory.AuthorizedClient();
        using var response = await client.PostAsync("/measurements/aggregate", Body("{\"stationName\":\"A\",\"temperature\":1.0}\n{\"stationName\":\"B\",\"temperature\":2.0}\ninvalid"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var result = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"lineNumber\":3", result);
        Assert.DoesNotContain("\"min\"", result);
        Assert.Empty(Directory.GetDirectories(factory.Root));
    }

    [Fact]
    public async Task WrongMediaTypeAndLineLimitAreReported()
    {
        using var factory = new ApiFactory();
        using var client = factory.AuthorizedClient();
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await client.PostAsync("/measurements/aggregate", new StringContent("{}"))).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.PostAsync("/measurements/aggregate", Body(new string('x', 17000)))).StatusCode);
    }

    [Fact]
    public async Task StorageExhaustionReturns507AndCleansUp()
    {
        using var factory = new ApiFactory { DiskBytes = 1024 * 1024 };
        using var client = factory.AuthorizedClient();
        using var response = await client.PostAsync("/measurements/aggregate", GeneratedContent.Create(10000, 10000, workload: "unique"));
        Assert.Equal((HttpStatusCode)507, response.StatusCode);
        Assert.Contains("storage_limit", await response.Content.ReadAsStringAsync());
        Assert.Empty(Directory.GetDirectories(factory.Root));
    }

    [Fact]
    public async Task StorageIoFailureReturnsProblem()
    {
        using var factory = new ApiFactory();
        using var client = factory.AuthorizedClient();
        Directory.Delete(factory.Root);
        File.WriteAllText(factory.Root, "simulated unavailable storage directory");
        try
        {
            using var response = await client.PostAsync("/measurements/aggregate", Body(""));
            Assert.Equal((HttpStatusCode)507, response.StatusCode);
            Assert.Contains("storage_or_io_failure", await response.Content.ReadAsStringAsync());
        }
        finally { File.Delete(factory.Root); }
    }

    [Fact]
    public async Task CapacityAndTimeoutReleaseProcessingSlot()
    {
        await using var server = await ApiProcess.StartAsync(timeoutSeconds: 5, maxConcurrentRequests: 1);
        using var client = server.CreateClient();
        using var blocking = new GateContent();
        var first = client.PostAsync("/measurements/aggregate", blocking);
        await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        for (var i = 0; i < 200 && Directory.GetDirectories(server.TempRoot).Length == 0; i++) await Task.Delay(10);
        Assert.NotEmpty(Directory.GetDirectories(server.TempRoot));
        // Directory creation precedes database initialization; sample until a database exists.
        long temporaryBytes = 0;
        for (var i = 0; i < 200 && temporaryBytes == 0; i++)
        {
            using var snapshot = JsonDocument.Parse(await client.GetStringAsync("/metrics"));
            Assert.Equal(1, snapshot.RootElement.GetProperty("activeAggregations").GetInt64());
            temporaryBytes = snapshot.RootElement.GetProperty("temporaryStorageBytes").GetInt64();
            if (temporaryBytes == 0) await Task.Delay(10);
        }
        Assert.True(temporaryBytes > 0);
        using var busy = await client.PostAsync("/measurements/aggregate", Body(""));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, busy.StatusCode);
        await Task.Delay(TimeSpan.FromSeconds(6));
        blocking.Release.TrySetResult();
        using var timedOut = await first.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.RequestTimeout, timedOut.StatusCode);
        blocking.Release.TrySetResult();
        Assert.Empty(Directory.GetDirectories(server.TempRoot));
        using var next = await client.PostAsync("/measurements/aggregate", Body(""));
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
    }

    [Fact]
    public async Task DisconnectCleansUpAndReleasesCapacity()
    {
        await using var server = await ApiProcess.StartAsync(maxConcurrentRequests: 1);
        using var client = server.CreateClient();
        using var blocking = new GateContent();
        using var cancellation = new CancellationTokenSource();
        var first = client.PostAsync("/measurements/aggregate", blocking, cancellation.Token);
        await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        for (var i = 0; i < 200 && Directory.GetDirectories(server.TempRoot).Length == 0; i++) await Task.Delay(10);
        Assert.NotEmpty(Directory.GetDirectories(server.TempRoot));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await first);
        for (var i = 0; i < 200 && Directory.GetDirectories(server.TempRoot).Length != 0; i++) await Task.Delay(10);
        Assert.Empty(Directory.GetDirectories(server.TempRoot));
        using var next = await client.PostAsync("/measurements/aggregate", Body(""));
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
    }

    [Fact]
    public async Task EnforcesMeasurementCeilingAndKeepsRequestsIndependent()
    {
        using var factory = new ApiFactory { MaxMeasurements = 3 };
        using var client = factory.AuthorizedClient();
        using var tooMany = await client.PostAsync("/measurements/aggregate", GeneratedContent.Create(4, 1));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooMany.StatusCode);
        Assert.Contains("\"lineNumber\":4", await tooMany.Content.ReadAsStringAsync());
        foreach (var value in new[] { "1.0", "2.0" })
        {
            using var response = await client.PostAsync("/measurements/aggregate", Body($"{{\"stationName\":\"A\",\"temperature\":{value}}}"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains($"\"min\":{value}", await response.Content.ReadAsStringAsync());
        }
        Assert.Empty(Directory.GetDirectories(factory.Root));
    }

    [Fact]
    public async Task ConsoleClientStreamsFixtureAndReportsErrors()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var previous = Environment.GetEnvironmentVariable("Authentication__ApiKey");
        try
        {
            Environment.SetEnvironmentVariable("Authentication__ApiKey", "test-key");
            var output = new StringWriter();
            var errors = new StringWriter();
            Assert.Equal(0, await ClientApplication.RunAsync(["--fixture", "true"], output, errors, client));
            Assert.Contains("\"mean\":23.1", output.ToString());
            Assert.Contains("Completed", errors.ToString());
            Assert.Contains("generated 3/3 (100.0%)", errors.ToString());
            var quietDiagnostics = new StringWriter();
            Assert.Equal(0, await ClientApplication.RunAsync(["--fixture", "true", "--progress", "false"], new StringWriter(), quietDiagnostics, client));
            Assert.DoesNotContain("generated", quietDiagnostics.ToString());
            Environment.SetEnvironmentVariable("Authentication__ApiKey", "wrong");
            Assert.Equal(1, await ClientApplication.RunAsync(["--fixture", "true"], new StringWriter(), errors, client));
            Assert.Contains("401", errors.ToString());
        }
        finally { Environment.SetEnvironmentVariable("Authentication__ApiKey", previous); }
    }

    private static StringContent Body(string value) => new(value, Encoding.UTF8, "application/x-ndjson");

    private sealed class GateContent : HttpContent
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public GateContent() => Headers.ContentType = new MediaTypeHeaderValue("application/x-ndjson");
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => WriteAsync(stream, CancellationToken.None);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) => WriteAsync(stream, cancellationToken);
        private async Task WriteAsync(Stream stream, CancellationToken cancellationToken)
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes("{\"stationName\":\"A\",\"temperature\":1.0}\n"), cancellationToken);
            await stream.FlushAsync(cancellationToken);
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }
        protected override void Dispose(bool disposing) { Release.TrySetResult(); base.Dispose(disposing); }
    }

}
