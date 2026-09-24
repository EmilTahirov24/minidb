using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace MiniDb.Bench;

/// <summary>
/// Readers and a writer at the same time: several threads reading random keys while one
/// thread commits single-key updates, for a fixed time. Counts reads and commits, and how long
/// each read took - which is where waiting for a writer shows up.
/// </summary>
internal static class Concurrency
{
    public static string Run(int readers = 4, double seconds = 3, int runs = 5)
    {
        var keys = Data.RandomKeys();
        string directory = Stores.NewDirectory();
        var rounds = new List<Round>();
        try
        {
            using var store = new MiniDbStore(directory);
            store.Load(keys, Data.Value, Data.Batch);
            store.Checkpoint();
            for (int run = 0; run < runs; run++)
            {
                rounds.Add(Measure(store, keys, readers, TimeSpan.FromSeconds(seconds), seed: run));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        var culture = CultureInfo.InvariantCulture;
        var report = new StringBuilder();
        report.AppendLine(culture, $"{readers} threads reading random keys and 1 thread committing single-key updates, together, for {seconds:N0} s; {Data.Keys:N0} keys of {Data.KeySize} bytes, values of {Data.ValueSize}. The median of {runs} runs.");
        report.AppendLine();
        report.AppendLine("| Reads per second | Commits per second | Read, median | Read, 99th percentile | Read, 99.9th percentile |");
        report.AppendLine("|---|---|---|---|---|");
        report.AppendLine(culture,
            $"| {Median(rounds, r => r.ReadsPerSecond):N0} | {Median(rounds, r => r.CommitsPerSecond):N0} | {Median(rounds, r => r.P50):N1} µs | {Median(rounds, r => r.P99):N1} µs | {Median(rounds, r => r.P999):N1} µs |");
        return report.ToString();
    }

    private sealed record Round(double ReadsPerSecond, double CommitsPerSecond, double P50, double P99, double P999);

    private static Round Measure(MiniDbStore store, List<byte[]> keys, int readers, TimeSpan duration, int seed)
    {
        using var stop = new CancellationTokenSource();
        long commits = 0;
        var latencies = new List<long>[readers];
        var threads = new List<Thread>();

        threads.Add(new Thread(() =>
        {
            var random = new Random(1000 + seed);
            while (!stop.IsCancellationRequested)
            {
                store.PutAndCommit(keys[random.Next(keys.Count)], Data.Value);
                commits++;
            }
        }));
        for (int r = 0; r < readers; r++)
        {
            int reader = r;
            latencies[reader] = new List<long>(1 << 20);
            threads.Add(new Thread(() =>
            {
                var random = new Random(seed * 100 + reader);
                while (!stop.IsCancellationRequested)
                {
                    long start = Stopwatch.GetTimestamp();
                    store.Get(keys[random.Next(keys.Count)]);
                    latencies[reader].Add(Stopwatch.GetTimestamp() - start);
                }
            }));
        }

        var clock = Stopwatch.StartNew();
        threads.ForEach(thread => thread.Start());
        Thread.Sleep(duration);
        stop.Cancel();
        threads.ForEach(thread => thread.Join());
        double elapsed = clock.Elapsed.TotalSeconds;

        var all = latencies.SelectMany(list => list).Order().ToArray();
        double Micro(double q) => all[(int)Math.Min(all.Length - 1, q * all.Length)] * 1e6 / Stopwatch.Frequency;
        return new Round(all.Length / elapsed, commits / elapsed, Micro(0.5), Micro(0.99), Micro(0.999));
    }

    private static double Median(List<Round> rounds, Func<Round, double> value)
    {
        var sorted = rounds.Select(value).Order().ToList();
        return sorted[sorted.Count / 2];
    }
}
