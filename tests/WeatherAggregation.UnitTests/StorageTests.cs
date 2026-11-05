using Microsoft.Data.Sqlite;
using WeatherAggregation.Core;
using WeatherAggregation.Storage;
using Xunit;

namespace WeatherAggregation.UnitTests;

public class StorageTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "weather-tests-" + Guid.NewGuid().ToString("N"));
    private AggregationOptions Options(int batch = 2) => new() { TempRoot = root, BatchEntries = batch };

    [Fact]
    public void DiskMergesMatchIndependentReferenceAndCleanUp()
    {
        var random = new Random(7);
        var inputs = Enumerable.Range(0, 2000).Select(i => new Measurement(i % 2 == 0 ? $"STATION {i % 37}" : $"station {i % 37}", random.Next(-999, 1000))).ToArray();
        using (var store = new TemporaryStore(Options()))
        {
            foreach (var input in inputs) store.Add(input, default);
            var expected = inputs.GroupBy(x => x.StationName, StringComparer.OrdinalIgnoreCase)
                .Select(g => new Aggregate(StationNames.Normalize(g.Key), g.Min(x => x.Tenths), g.Max(x => x.Tenths), g.Sum(x => (long)x.Tenths), g.LongCount()))
                .OrderBy(x => x.Name, Comparer<string>.Create(StationNames.CompareOutput)).ToArray();
            Assert.Equal(expected, store.ReadResults(default).ToArray());
        }
        Assert.Empty(Directory.GetDirectories(root));
    }

    [Fact]
    public void UnicodeIdentityAndDisplayAreIndependentOfInputOrder()
    {
        var inputs = new[] { "st. john's", "ST. JOHN'S", "ÉVRY", "évry", "σ", "ς", "Σ", "é", "e", "e\u0301" };
        string[] Run(IEnumerable<string> names)
        {
            using var store = new TemporaryStore(Options(1));
            foreach (var name in names) store.Add(new(name, 10), default);
            return store.ReadResults(default).Select(x => x.ToJsonLine()).ToArray();
        }
        Assert.Equal(Run(inputs), Run(inputs.Reverse()));
        Assert.Equal(6, Run(inputs).Length);
    }

    [Fact]
    public void CancellationDisposesStore()
    {
        using (var store = new TemporaryStore(Options()))
            Assert.Throws<OperationCanceledException>(() => store.Add(new("A", 10), new CancellationToken(true)));
        Assert.Empty(Directory.GetDirectories(root));
    }

    [Fact]
    public void EnforcesDiskBudgetAndReleasesDatabaseAfterFailure()
    {
        var options = Options(100);
        options.MaxDiskBytes = 1024 * 1024;
        using (var store = new TemporaryStore(options))
        {
            var ex = Assert.Throws<SqliteException>(() =>
            {
                for (var i = 0; i < 20000; i++) store.Add(new($"{i:D10}-{new string('x', 80)}", 10), default);
                store.Flush(default);
            });
            Assert.Equal(13, ex.SqliteErrorCode);
        }
        Assert.Empty(Directory.GetDirectories(root));
    }

    [Fact]
    public void ScavengerRecoversCrashBeforeLeaseWasCreated()
    {
        var stale = Directory.CreateDirectory(Path.Combine(root, Guid.NewGuid().ToString("N"))).FullName;
        Directory.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-2));
        var errors = new List<Exception>();
        TemporaryStore.Scavenge(root, errors.Add);
        Assert.Empty(errors);
        Assert.False(Directory.Exists(stale));
    }

    [Fact]
    public void DisposeIsIdempotent()
    {
        var errors = new List<Exception>();
        var store = new TemporaryStore(Options(), errors.Add);
        store.Dispose();
        store.Dispose();
        Assert.Empty(errors);
        Assert.Empty(Directory.GetDirectories(root));
    }

    [Fact]
    public void ScavengerLeavesActiveAndUnrecognizedDirectoriesAlone()
    {
        using var store = new TemporaryStore(Options());
        Directory.SetLastWriteTimeUtc(store.DirectoryPath, DateTime.UtcNow.AddDays(-2));
        var unknown = Directory.CreateDirectory(Path.Combine(root, "do-not-delete")).FullName;
        var stale = Directory.CreateDirectory(Path.Combine(root, Guid.NewGuid().ToString("N"))).FullName;
        File.WriteAllText(Path.Combine(stale, "active.lock"), "");
        File.WriteAllText(Path.Combine(stale, "aggregates.db"), "");
        Directory.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-2));
        TemporaryStore.Scavenge(root, _ => { });
        Assert.True(Directory.Exists(store.DirectoryPath));
        Assert.True(Directory.Exists(unknown));
        Assert.False(Directory.Exists(stale));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
