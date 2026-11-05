using Microsoft.Data.Sqlite;
using WeatherAggregation.Core;

namespace WeatherAggregation.Storage;

public sealed class TemporaryStore : IDisposable
{
    private readonly string directory;
    private readonly FileStream lease;
    private readonly SqliteConnection connection;
    private readonly Dictionary<string, Aggregate> batch = new(StringComparer.OrdinalIgnoreCase);
    private readonly AggregationOptions options;
    private readonly Action<Exception>? cleanupError;
    private bool disposed;
    public string DirectoryPath => directory;

    public TemporaryStore(AggregationOptions options, Action<Exception>? cleanupError = null)
    {
        this.options = options;
        this.cleanupError = cleanupError;
        directory = Path.Combine(Path.GetFullPath(options.TempRoot), Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        FileStream? pendingLease = null;
        SqliteConnection? pendingConnection = null;
        try
        {
            lease = pendingLease = new FileStream(Path.Combine(directory, "active.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            connection = pendingConnection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(directory, "aggregates.db"), Pooling = false
            }.ToString());
            connection.Open();
            connection.CreateCollation("STATION", StringComparer.OrdinalIgnoreCase.Compare);
            connection.CreateCollation("DISPLAY", StationNames.CompareOutput);
            connection.CreateFunction("display_min", (string a, string b) => StringComparer.Ordinal.Compare(a, b) <= 0 ? a : b);
            // Reserve room for a rollback journal and filesystem overhead. The covering order index
            // avoids an unbounded final sort file outside this request's storage budget.
            var pages = Math.Max(32, options.MaxDiskBytes / 3 / 4096);
            Execute($"PRAGMA page_size=4096; PRAGMA max_page_count={pages}; PRAGMA cache_size=-{options.CacheKiB}; PRAGMA temp_store=FILE; PRAGMA journal_mode=DELETE; PRAGMA synchronous=NORMAL;");
            Execute("CREATE TABLE aggregates (identity TEXT COLLATE STATION PRIMARY KEY, display TEXT NOT NULL, minimum INTEGER NOT NULL, maximum INTEGER NOT NULL, sum INTEGER NOT NULL, count INTEGER NOT NULL) WITHOUT ROWID; CREATE INDEX output_order ON aggregates(display COLLATE DISPLAY);");
        }
        catch
        {
            try { pendingConnection?.Dispose(); }
            finally
            {
                pendingLease?.Dispose();
                DeleteDirectory();
            }
            throw;
        }
    }

    public void Add(Measurement measurement, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var incoming = Aggregate.From(measurement);
        batch[measurement.StationName] = batch.TryGetValue(measurement.StationName, out var previous)
            ? previous.Merge(incoming) : incoming;
        if (batch.Count >= options.BatchEntries) Flush(cancellationToken);
    }

    public void Flush(CancellationToken cancellationToken)
    {
        if (batch.Count == 0) return;
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO aggregates VALUES ($identity,$display,$min,$max,$sum,$count)
            ON CONFLICT(identity) DO UPDATE SET
            display=display_min(aggregates.display,excluded.display),
            minimum=min(aggregates.minimum,excluded.minimum),
            maximum=max(aggregates.maximum,excluded.maximum),
            sum=aggregates.sum+excluded.sum, count=aggregates.count+excluded.count;
            """;
        foreach (var name in new[] { "$identity", "$display", "$min", "$max", "$sum", "$count" })
            command.Parameters.Add(new SqliteParameter(name, name is "$identity" or "$display" ? SqliteType.Text : SqliteType.Integer));
        command.Prepare();
        foreach (var (identity, value) in batch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            command.Parameters[0].Value = identity;
            command.Parameters[1].Value = value.Name;
            command.Parameters[2].Value = value.Min;
            command.Parameters[3].Value = value.Max;
            command.Parameters[4].Value = value.Sum;
            command.Parameters[5].Value = value.Count;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
        batch.Clear();
    }

    public IEnumerable<Aggregate> ReadResults(CancellationToken cancellationToken)
    {
        Flush(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT display,minimum,maximum,sum,count FROM aggregates INDEXED BY output_order ORDER BY display COLLATE DISPLAY";
        using var registration = cancellationToken.Register(command.Cancel);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new(reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt64(3), reader.GetInt64(4));
        }
    }

    private void Execute(string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { connection.Dispose(); }
        finally
        {
            lease.Dispose();
            DeleteDirectory();
        }
    }

    private void DeleteDirectory()
    {
        try { Directory.Delete(directory, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { cleanupError?.Invoke(ex); }
    }

    public static void Scavenge(string root, Action<Exception> report)
    {
        Directory.CreateDirectory(root);
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            try
            {
                if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _) ||
                    (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 ||
                    Directory.GetLastWriteTimeUtc(directory) > DateTime.UtcNow.AddDays(-1)) continue;
                // Only remove known, flat service files; never recurse into unexpected directories.
                var files = Directory.GetFiles(directory);
                if (Directory.GetDirectories(directory).Length != 0 || files.Any(p =>
                    Path.GetFileName(p) is not ("active.lock" or "aggregates.db" or "aggregates.db-journal") ||
                    (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)) continue;
                // A running request holds this lease even if its directory is old.
                // OpenOrCreate also recovers crashes between directory and lease creation.
                var lockPath = Path.Combine(directory, "active.lock");
                using (new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) { }
                foreach (var file in files) File.Delete(file);
                if (!files.Contains(lockPath)) File.Delete(lockPath);
                Directory.Delete(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { report(ex); }
        }
    }
}
