using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WeatherAggregation.Api;
using WeatherAggregation.Client;
using WeatherAggregation.Core;
using Xunit;

namespace WeatherAggregation.IntegrationTests;

public class ExtraFeaturesTests
{
    [Fact]
    public async Task GeneratorProgressMatchesRowsAndActualTransportBytes()
    {
        long generated = 0, uploaded = 0;
        using var content = GeneratedContent.Create(2050, 3, generated: count => generated += count, uploaded: bytes => uploaded += bytes);
        using var stream = new MemoryStream();
        await content.CopyToAsync(stream);
        Assert.Equal(2050, generated);
        Assert.Equal(stream.Length, uploaded);
        Assert.Equal(content.BytesWritten, uploaded);
    }

    [Fact]
    public async Task ProgressIsReportedBeforeRequestCompletion()
    {
        var diagnostics = new SignallingWriter();
        await using var progress = new ProgressReporter(diagnostics, 1000);
        progress.Generated(500);
        progress.Uploaded(1048576);
        var line = await diagnostics.FirstLine.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("generated 500/1000 (50.0%)", line);
        Assert.Contains("uploaded 1.00 MiB", line);
        Assert.Contains("rows/s", line);
    }

    private sealed class SignallingWriter : StringWriter
    {
        public TaskCompletionSource<string> FirstLine { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override Task WriteLineAsync(string? value)
        {
            FirstLine.TrySetResult(value ?? "");
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task DocsSpecAssetsAndMetricsRequireAuthentication()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        foreach (var path in new[] { "/docs/", "/docs/index.html", "/docs/openapi.json", "/docs/swagger-ui.css", "/metrics" })
        {
            using var denied = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            Assert.True(denied.Headers.Contains("X-Request-Id"));
        }
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("apikey:test-key")));
        using var ui = await client.GetAsync("/docs/index.html");
        Assert.Equal(HttpStatusCode.OK, ui.StatusCode);
        Assert.Contains("swagger-ui", await ui.Content.ReadAsStringAsync());
        using var css = await client.GetAsync("/docs/swagger-ui.css");
        Assert.Equal(HttpStatusCode.OK, css.StatusCode);
        using var spec = await client.GetAsync("/docs/openapi.json");
        Assert.Equal(HttpStatusCode.OK, spec.StatusCode);
        Assert.DoesNotContain("test-key", await spec.Content.ReadAsStringAsync());
        using var metrics = await client.GetAsync("/metrics");
        Assert.Equal(HttpStatusCode.Unauthorized, metrics.StatusCode);
        client.DefaultRequestHeaders.Add("X-Api-Key", "wrong");
        using var conflicting = await client.GetAsync("/docs/openapi.json");
        Assert.Equal(HttpStatusCode.Unauthorized, conflicting.StatusCode);
    }

    [Fact]
    public async Task OpenApiExamplesSchemasAndReferencesAreConsistent()
    {
        using var factory = new ApiFactory();
        using var client = factory.AuthorizedClient();
        using var response = await client.GetAsync("/docs/openapi.json");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal("X-Api-Key", root.GetProperty("components").GetProperty("securitySchemes").GetProperty("ApiKey").GetProperty("name").GetString());
        CheckReferences(root);
        var operation = root.GetProperty("paths").GetProperty("/measurements/aggregate").GetProperty("post");
        var sample = operation.GetProperty("requestBody").GetProperty("content").GetProperty("application/x-ndjson").GetProperty("example").GetString()!;
        foreach (var line in sample.Split('\n', StringSplitOptions.RemoveEmptyEntries)) MeasurementParser.Parse(Encoding.UTF8.GetBytes(line), 1);
        using var result = await client.PostAsync("/measurements/aggregate", new StringContent(sample, Encoding.UTF8, "application/x-ndjson"));
        Assert.Equal(operation.GetProperty("responses").GetProperty("200").GetProperty("content").GetProperty("application/x-ndjson").GetProperty("example").GetString(), await result.Content.ReadAsStringAsync());
        foreach (var status in new[] { "400", "401", "408", "413", "415", "500", "503", "507" }) Assert.True(operation.GetProperty("responses").TryGetProperty(status, out _));

        void CheckReferences(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Object)
                foreach (var property in node.EnumerateObject())
                {
                    if (property.Name == "$ref")
                    {
                        var target = root;
                        foreach (var segment in property.Value.GetString()![2..].Split('/')) target = target.GetProperty(segment.Replace("~1", "/").Replace("~0", "~"));
                        Assert.NotEqual(JsonValueKind.Undefined, target.ValueKind);
                    }
                    else CheckReferences(property.Value);
                }
            else if (node.ValueKind == JsonValueKind.Array) foreach (var item in node.EnumerateArray()) CheckReferences(item);
        }
    }

    [Theory]
    [InlineData("client-request_123.abc", true)]
    [InlineData("bad id with spaces", false)]
    public async Task RequestIdsCorrelateProblemsAndRejectUnsafeValues(string supplied, bool retained)
    {
        using var factory = new ApiFactory();
        using var client = factory.AuthorizedClient();
        client.DefaultRequestHeaders.Add("X-Request-Id", supplied);
        using var response = await client.PostAsync("/measurements/aggregate", new StringContent("invalid", Encoding.UTF8, "application/x-ndjson"));
        var id = Assert.Single(response.Headers.GetValues("X-Request-Id"));
        Assert.Equal(retained, id == supplied);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(id, problem.RootElement.GetProperty("requestId").GetString());
    }

    [Fact]
    public async Task MetricsCountAcceptedAndRejectedRequestsWithoutLeakingNames()
    {
        using var factory = new ApiFactory();
        using var client = factory.AuthorizedClient();
        using var result = await client.PostAsync("/measurements/aggregate", GeneratedContent.Create(3, 2, fixture: true));
        result.EnsureSuccessStatusCode();
        using var failed = await client.PostAsync("/measurements/aggregate", new StringContent("invalid", Encoding.UTF8, "application/x-ndjson"));
        using var response = await client.GetAsync("/metrics");
        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        Assert.Equal(3, document.RootElement.GetProperty("acceptedMeasurements").GetInt64());
        Assert.Equal(0, document.RootElement.GetProperty("activeAggregations").GetInt64());
        Assert.Equal(0, document.RootElement.GetProperty("temporaryStorageBytes").GetInt64());
        Assert.Equal(1, document.RootElement.GetProperty("rejectedRequests").GetInt64());
        Assert.Equal(2, document.RootElement.GetProperty("completedRequests").GetInt64());
        Assert.True(document.RootElement.GetProperty("totalRequestSeconds").GetDouble() > 0);
        Assert.DoesNotContain("Hamburg", json);
    }

    [Fact]
    public void NativeMetricsEmitCountersAndDuration()
    {
        var readings = new Dictionary<string, double>();
        using var factory = new ApiFactory();
        using var client = factory.AuthorizedClient();
        var metrics = factory.Services.GetRequiredService<OperationsMetrics>();
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) => { if (instrument.Meter.Name == OperationsMetrics.MeterName) l.EnableMeasurementEvents(instrument); };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => { lock (readings) readings[instrument.Name] = value; });
        listener.SetMeasurementEventCallback<double>((instrument, value, _, _) => { lock (readings) readings[instrument.Name] = value; });
        listener.Start();
        metrics.Record("/health", 401, 0.5);
        listener.RecordObservableInstruments();
        lock (readings)
        {
            Assert.True(readings.ContainsKey("weather.requests"));
            Assert.True(readings.ContainsKey("weather.requests.rejected"));
            Assert.True(readings.ContainsKey("weather.request.duration"));
            Assert.True(readings.ContainsKey("weather.storage.temporary"));
        }
    }
}
