using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using WeatherAggregation.Api;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Data.Sqlite;
using WeatherAggregation.Core;
using WeatherAggregation.Storage;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddSimpleConsole(console => console.IncludeScopes = true);
var key = builder.Configuration["Authentication:ApiKey"];
if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("Configure Authentication:ApiKey before starting the service.");
var expectedKeyHash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
var options = new AggregationOptions();
builder.Configuration.GetSection("Aggregation").Bind(options);
options.Validate();
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<OperationsMetrics>();
var app = builder.Build();
var metrics = app.Services.GetRequiredService<OperationsMetrics>();
using var slots = new SemaphoreSlim(options.MaxConcurrentRequests);
TemporaryStore.Scavenge(options.TempRoot, ex => app.Logger.LogWarning(ex, "Could not remove an abandoned temporary store."));

app.Use(async (context, next) =>
{
    var suppliedId = context.Request.Headers["X-Request-Id"];
    var candidate = suppliedId.Count == 1 ? suppliedId[0] : null;
    var id = candidate is { Length: > 0 and <= 64 } && candidate.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')
        ? candidate : Guid.NewGuid().ToString("N");
    context.TraceIdentifier = id;
    context.Response.Headers["X-Request-Id"] = id;
    using var scope = app.Logger.BeginScope(new Dictionary<string, object?> { ["RequestId"] = id });
    var watch = Stopwatch.StartNew();
    var failed = false;
    try { await next(context); }
    catch { failed = true; throw; }
    finally
    {
        var status = context.RequestAborted.IsCancellationRequested ? 499 : failed ? 500 : context.Response.StatusCode;
        var path = context.Request.Path;
        var route = path.StartsWithSegments("/docs") ? "/docs" : path.Value is "/health" or "/metrics" or "/measurements/aggregate" ? path.Value : "other";
        metrics.Record(route!, status, watch.Elapsed.TotalSeconds);
        app.Logger.LogInformation("Request {RequestId} {Route} completed with {Status} in {ElapsedMs:F1} ms", id, route, status, watch.Elapsed.TotalMilliseconds);
    }
});

app.Use(async (context, next) =>
{
    var authorized = context.Request.Headers.TryGetValue("X-Api-Key", out var supplied) && supplied.Count == 1 && Matches(supplied[0] ?? "");
    // Browsers can prompt for Basic credentials for protected documentation assets.
    // API operations still require X-Api-Key; Basic is accepted only beneath /docs.
    if (!authorized && !context.Request.Headers.ContainsKey("X-Api-Key") && context.Request.Path.StartsWithSegments("/docs"))
    {
        var authorization = context.Request.Headers.Authorization;
        if (authorization.Count == 1 && authorization[0] is { } value && value.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var credentials = Encoding.UTF8.GetString(Convert.FromBase64String(value[6..]));
                authorized = credentials.StartsWith("apikey:", StringComparison.Ordinal) && Matches(credentials[7..]);
            }
            catch (FormatException) { }
        }
    }
    if (!authorized)
    {
        if (context.Request.Path.StartsWithSegments("/docs")) context.Response.Headers.WWWAuthenticate = "Basic realm=\"Weather API documentation\", charset=\"UTF-8\"";
        await Results.Problem(statusCode: 401, title: "API key required", extensions: new Dictionary<string, object?> { ["code"] = "unauthorized", ["requestId"] = context.TraceIdentifier }).ExecuteAsync(context);
        return;
    }
    await next(context);
});

app.UseSwaggerUI(settings =>
{
    settings.RoutePrefix = "docs";
    settings.SwaggerEndpoint("openapi.json", "Weather Aggregation v1");
    settings.DocumentTitle = "Weather Aggregation API";
});
app.MapGet("/docs/openapi.json", () => Results.Stream(
    typeof(Program).Assembly.GetManifestResourceStream("WeatherAggregation.Api.openapi.json")!, "application/json"));
app.MapGet("/metrics", () => Results.Ok(metrics.Snapshot()));

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapPost("/measurements/aggregate", async (HttpContext context) =>
{
    var mediaType = context.Request.ContentType?.Split(';', 2)[0].Trim();
    if (!string.Equals(mediaType, "application/x-ndjson", StringComparison.OrdinalIgnoreCase))
    {
        await Problem(415, "unsupported_media_type", "Use application/x-ndjson.");
        return;
    }
    if (!await slots.WaitAsync(0, context.RequestAborted))
    {
        context.Response.Headers.RetryAfter = "5";
        await Problem(503, "capacity_exceeded", "All processing slots are occupied.");
        return;
    }
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
    metrics.BeginAggregation();
    timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
    var cancellation = timeout.Token;
    try
    {
        var sizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false }) sizeFeature.MaxRequestBodySize = null;
        using var store = new TemporaryStore(options, ex => app.Logger.LogWarning(ex, "Temporary store cleanup failed."));
        long lines = 0;
        await foreach (var line in JsonLines.ReadAsync(context.Request.Body, options.MaxLineBytes, cancellation))
        {
            lines++;
            if (lines > options.MaxMeasurements) throw new InputException("measurement_limit", "Measurement limit exceeded.", lines, 413);
            store.Add(MeasurementParser.Parse(line.Span, lines), cancellation);
        }
        store.Flush(cancellation);
        metrics.AddMeasurements(lines);
        context.Response.ContentType = "application/x-ndjson; charset=utf-8";
        await using var writer = new StreamWriter(context.Response.Body, new UTF8Encoding(false), 64 * 1024, leaveOpen: true);
        foreach (var result in store.ReadResults(cancellation))
            await writer.WriteAsync(result.ToJsonLine().AsMemory(), cancellation);
        await writer.FlushAsync(cancellation);
        app.Logger.LogInformation("Aggregated {Measurements} measurements.", lines);
    }
    catch (InputException ex) { await Problem(ex.Status, ex.Code, ex.Message, ex.LineNumber); }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { context.Abort(); }
    catch (OperationCanceledException) { await Problem(408, "processing_timeout", "Processing time limit exceeded."); }
    catch (SqliteException ex)
    {
        app.Logger.LogError(ex, "Temporary aggregation database failed.");
        await Problem(ex.SqliteErrorCode == 13 ? 507 : 500, ex.SqliteErrorCode == 13 ? "storage_limit" : "storage_failure", "Temporary aggregation storage is unavailable.");
    }
    catch (IOException ex)
    {
        app.Logger.LogWarning(ex, "Request I/O failed.");
        await Problem(507, "storage_or_io_failure", "Request or temporary storage I/O failed.");
    }
    finally { metrics.EndAggregation(); slots.Release(); }

    async Task Problem(int status, string code, string title, long? line = null)
    {
        if (context.Response.HasStarted) { context.Abort(); return; }
        await Results.Problem(statusCode: status, title: title,
            extensions: new Dictionary<string, object?> { ["code"] = code, ["lineNumber"] = line, ["requestId"] = context.TraceIdentifier }).ExecuteAsync(context);
    }
});
app.Run();

bool Matches(string value) => CryptographicOperations.FixedTimeEquals(expectedKeyHash, SHA256.HashData(Encoding.UTF8.GetBytes(value)));

public partial class Program;
