using System.Net;
using System.Diagnostics;
using System.Text.Json;
using WeatherAggregation.Client;
using WeatherAggregation.Testing;
using Xunit;

namespace WeatherAggregation.IntegrationTests;

public class KestrelTests
{
    [Fact]
    public async Task ConsoleExecutablePrintsOnlyJsonlOnStdout()
    {
        await using var server = await ApiProcess.StartAsync();
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add(typeof(GeneratedContent).Assembly.Location);
        start.ArgumentList.Add("--url");
        start.ArgumentList.Add(server.Address.ToString());
        start.ArgumentList.Add("--fixture");
        start.ArgumentList.Add("true");
        start.Environment["Authentication__ApiKey"] = ApiProcess.Key;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("{\"stationName\":\"Bulawayo\",\"min\":8.9,\"mean\":8.9,\"max\":8.9}\n{\"stationName\":\"Hamburg\",\"min\":12.0,\"mean\":23.1,\"max\":34.2}\n", await stdout);
            var diagnostics = await stderr;
            Assert.Contains("Completed", diagnostics);
            Assert.Contains("generated 3/3 (100.0%)", diagnostics);
            Assert.Contains("uploaded", diagnostics);
            Assert.Contains("Server request ID:", diagnostics);
        }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }

    [Fact]
    public async Task RealServerAcceptsStreamBeyondDefaultBodyLimit()
    {
        await using var server = await ApiProcess.StartAsync();
        using var client = server.CreateClient();
        using var content = GeneratedContent.Create(600000, 1);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/measurements/aggregate") { Content = content };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(content.BytesWritten > 30_000_000);
        var result = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(result);
        Assert.Equal("Station 0000000000", json.RootElement.GetProperty("stationName").GetString());
        Assert.Equal(-99.9m, json.RootElement.GetProperty("min").GetDecimal());
        Assert.Equal(99.9m, json.RootElement.GetProperty("max").GetDecimal());
        Assert.Empty(Directory.GetDirectories(server.TempRoot));
    }
}
