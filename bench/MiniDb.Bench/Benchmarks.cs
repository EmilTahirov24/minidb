using System.Buffers.Binary;

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;

namespace MiniDb.Bench;

/// <summary>The same keys every run: 16 bytes each, random or counting up, and 100-byte values.</summary>
internal static class Data
{
    public const int Keys = 100_000;
    public const int KeySize = 16;
    public const int ValueSize = 100;
    public const int Batch = 1_000;

    public static byte[] Value { get; } = Enumerable.Range(0, ValueSize).Select(i => (byte)i).ToArray();

    public static List<byte[]> RandomKeys(int count = Keys, int seed = 1)
    {
        var random = new Random(seed);
        var keys = new List<byte[]>(count);
        for (int i = 0; i < count; i++)
        {
            var key = new byte[KeySize];
            random.NextBytes(key);
            keys.Add(key);
        }
        return keys;
    }

    public static List<byte[]> AscendingKeys(int count = Keys)
    {
        var keys = new List<byte[]>(count);
        for (int i = 0; i < count; i++)
        {
            var key = new byte[KeySize];
            BinaryPrimitives.WriteInt64BigEndian(key.AsSpan(KeySize - 8), i);
            keys.Add(key);
        }
        return keys;
    }
}

/// <summary>Loading 100,000 keys into an empty store, 1,000 to a transaction.</summary>
[SimpleJob(RunStrategy.Monitoring, warmupCount: 1, iterationCount: 5)]
public class Load
{
    private List<byte[]> keys = [];
    private IStore? store;
    private string directory = "";

    [Params("MiniDB", "SQLite")]
    public string Engine { get; set; } = "";

    [Params("random", "ascending")]
    public string Order { get; set; } = "";

    [GlobalSetup]
    public void Keys() => keys = Order == "random" ? Data.RandomKeys() : Data.AscendingKeys();

    [IterationSetup]
    public void Empty()
    {
        directory = Stores.NewDirectory();
        store = Stores.Open(Engine, directory);
    }

    [Benchmark]
    public void LoadKeys() => store!.Load(keys, Data.Value, Data.Batch);

    [IterationCleanup]
    public void Remove()
    {
        store!.Dispose();
        Directory.Delete(directory, recursive: true);
    }
}

/// <summary>Reading from a store that holds 100,000 random keys.</summary>
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class Read
{
    private List<byte[]> keys = [];
    private IStore? store;
    private string directory = "";
    private int next;

    [Params("MiniDB", "SQLite")]
    public string Engine { get; set; } = "";

    [GlobalSetup]
    public void Fill()
    {
        keys = Data.RandomKeys();
        directory = Stores.NewDirectory();
        store = Stores.Open(Engine, directory);
        store.Load(keys, Data.Value, Data.Batch);
        store.Checkpoint();
        // Keys to look up in a different order from the one they went in.
        var random = new Random(2);
        keys = keys.OrderBy(_ => random.Next()).ToList();
    }

    [Benchmark]
    public byte[]? PointRead() => store!.Get(keys[next++ % keys.Count]);

    [Benchmark]
    public int Scan100() => store!.Scan(keys[next++ % keys.Count], 100);

    [GlobalCleanup]
    public void Remove()
    {
        store!.Dispose();
        Directory.Delete(directory, recursive: true);
    }
}

/// <summary>Transactions of one put each: every one waits for its flush to the disk.</summary>
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class Commit
{
    private List<byte[]> keys = [];
    private IStore? store;
    private string directory = "";
    private int next;

    [Params("MiniDB", "SQLite")]
    public string Engine { get; set; } = "";

    [GlobalSetup]
    public void Open()
    {
        keys = Data.RandomKeys(count: 1_000_000, seed: 3);
        directory = Stores.NewDirectory();
        store = Stores.Open(Engine, directory);
    }

    [Benchmark]
    public void SingleKeyCommit() => store!.PutAndCommit(keys[next++ % keys.Count], Data.Value);

    [GlobalCleanup]
    public void Remove()
    {
        store!.Dispose();
        Directory.Delete(directory, recursive: true);
    }
}
