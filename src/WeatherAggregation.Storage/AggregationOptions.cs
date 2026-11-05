namespace WeatherAggregation.Storage;

public sealed class AggregationOptions
{
    public int MaxLineBytes { get; set; } = 16 * 1024;
    public int BatchEntries { get; set; } = 10_000;
    public int CacheKiB { get; set; } = 8192;
    public long MaxMeasurements { get; set; } = 1_000_000_000;
    public int MaxConcurrentRequests { get; set; } = 2;
    public int TimeoutSeconds { get; set; } = 7200;
    public long MaxDiskBytes { get; set; } = 30L * 1024 * 1024 * 1024;
    public string TempRoot { get; set; } = Path.Combine(Path.GetTempPath(), "broadridge-weather");

    public void Validate()
    {
        if (MaxLineBytes is < 128 or > 1_048_576 || BatchEntries is < 1 or > 1_000_000 ||
            CacheKiB is < 64 or > 1_048_576 || MaxMeasurements is < 1 or > 1_000_000_000 ||
            MaxConcurrentRequests is < 1 or > 128 || TimeoutSeconds is < 1 or > 604800 ||
            MaxDiskBytes < 1024 * 1024 || string.IsNullOrWhiteSpace(TempRoot))
            throw new InvalidOperationException("Invalid Aggregation configuration. See README for supported limits.");
    }
}
