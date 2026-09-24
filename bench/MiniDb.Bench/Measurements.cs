using System.Globalization;
using System.Text;

namespace MiniDb.Bench;

/// <summary>
/// What the design costs in bytes, not time: how much a commit writes to the log, how full
/// the pages are, and what deleting half the keys gives back. The same work goes through
/// SQLite for comparison. Every figure is counted, so it comes out the same on every run.
/// </summary>
internal static class Measurements
{
    private const int PageSize = 4096;
    private const int Commits = 1_000;

    public static string Run()
    {
        var report = new StringBuilder();
        var culture = CultureInfo.InvariantCulture;
        report.AppendLine(culture, $"Keys of {Data.KeySize} bytes, values of {Data.ValueSize}.");
        report.AppendLine();

        report.AppendLine(culture, $"A commit of one new key, {Commits:N0} of them, with no checkpoint in between:");
        report.AppendLine();
        report.AppendLine("| Engine | Log bytes per commit | Of which data |");
        report.AppendLine("|---|---|---|");
        foreach (string engine in new[] { "MiniDB", "SQLite" })
        {
            long log = LogBytesPerCommit(engine);
            report.AppendLine(culture, $"| {engine} | {log:N0} | {(double)(Data.KeySize + Data.ValueSize) / log:P1} |");
        }
        report.AppendLine();

        report.AppendLine(culture, $"{Data.Keys:N0} keys loaded {Data.Batch:N0} to a transaction, then a checkpoint; then a random half of them deleted, then a checkpoint:");
        report.AppendLine();
        report.AppendLine("| Engine | Keys went in | Data file | Data in it | After deleting half | Free pages then |");
        report.AppendLine("|---|---|---|---|---|---|");
        foreach (string engine in new[] { "MiniDB", "SQLite" })
        {
            foreach (string order in new[] { "increasing", "random" })
            {
                var (size, afterDelete, free) = Space(engine, order);
                double payload = (double)Data.Keys * (Data.KeySize + Data.ValueSize);
                report.AppendLine(culture,
                    $"| {engine} | {order} | {size / 1024.0 / 1024.0:N1} MiB | {payload / size:P0} | {afterDelete / 1024.0 / 1024.0:N1} MiB | {free:N0} |");
            }
        }
        return report.ToString();
    }

    private static long LogBytesPerCommit(string engine)
    {
        string directory = Stores.NewDirectory();
        try
        {
            IStore store = engine == "MiniDB"
                ? new MiniDbStore(directory, new DatabaseOptions { CheckpointAfterBytes = long.MaxValue })
                : new SqliteStore(directory, autoCheckpoint: false);
            using (store)
            {
                store.PutAndCommit([0], Data.Value); // the log exists from here on
                long before = new FileInfo(store.LogFile).Length;
                foreach (var key in Data.RandomKeys(Commits, seed: 4))
                {
                    store.PutAndCommit(key, Data.Value);
                }
                return (new FileInfo(store.LogFile).Length - before) / Commits;
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>The data file's size after loading, and after deleting half; the free pages then.</summary>
    private static (long Loaded, long AfterDelete, long FreePages) Space(string engine, string order)
    {
        string directory = Stores.NewDirectory();
        try
        {
            var keys = order == "random" ? Data.RandomKeys() : Data.AscendingKeys();
            var random = new Random(5);
            var half = keys.OrderBy(_ => random.Next()).Take(keys.Count / 2).ToList();

            using var store = Stores.Open(engine, directory);
            store.Load(keys, Data.Value, Data.Batch);
            store.Checkpoint();
            long loaded = new FileInfo(store.DataFile).Length;

            store.Delete(half, Data.Batch);
            store.Checkpoint();
            long afterDelete = new FileInfo(store.DataFile).Length;
            long free = store switch
            {
                MiniDbStore mini => mini.Database.PageCounts().Free,
                SqliteStore lite => lite.Pragma("freelist_count"),
                _ => 0,
            };
            return (loaded, afterDelete, free);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
