using MiniDb.Tests.Simulation;
using MiniDb.Tests.Tree;

namespace MiniDb.Tests.Transactions;

/// <summary>
/// Random interleavings of several transactions, run against the database and against a small
/// reference implementation of snapshot isolation that keeps every committed version of every
/// key. Every read, scan, delete and commit must come out the same in both.
/// </summary>
public class SnapshotModelTests
{
    [Fact]
    public void The_database_behaves_like_the_model_of_snapshot_isolation() => Seeds.Each(200, seed =>
    {
        var random = new Random(seed);
        var model = new SnapshotModel();
        using var db = Database.Open(new SimulatedDisk(), "db", new DatabaseOptions { CachePages = 8 });
        var open = new List<(WriteTransaction Real, SnapshotModel.Transaction Model)>();

        for (int step = 0; step < 400; step++)
        {
            if (open.Count == 0 || (open.Count < 4 && random.NextDouble() < 0.15))
            {
                open.Add((db.BeginWrite(), model.Begin()));
                continue;
            }
            int which = random.Next(open.Count);
            var (real, expected) = open[which];
            byte[] key = [(byte)random.Next(24)];
            double roll = random.NextDouble();

            if (roll < 0.3)
            {
                Same.Bytes(expected.Get(key), real.Get(key), $"step {step}: get");
            }
            else if (roll < 0.4)
            {
                byte[] from = [(byte)random.Next(24)], to = [(byte)random.Next(from[0], 25)];
                Same.Entries(expected.Scan(from, to), real.Scan(from, to).ToList());
            }
            else if (roll < 0.65)
            {
                byte[] value = [(byte)random.Next(256), (byte)step];
                real.Put(key, value);
                expected.Put(key, value);
            }
            else if (roll < 0.78)
            {
                Assert.Equal(expected.Delete(key), real.Delete(key));
            }
            else if (roll < 0.95)
            {
                bool commits = expected.Commit();
                if (commits)
                {
                    real.Commit();
                }
                else
                {
                    Assert.Throws<WriteConflictException>(real.Commit);
                }
                open.RemoveAt(which);
            }
            else
            {
                real.Dispose();
                open.RemoveAt(which);
            }
        }

        foreach (var (real, _) in open)
        {
            real.Dispose();
        }
        db.Verify();
        Assert.Equal(0, db.Cache.KeptVersions);   // nobody is reading: no old version is kept
        using var final = db.BeginRead();
        Same.Entries(model.Begin().Scan([0], [255]), final.Scan().ToList());
    });
}

/// <summary>
/// Snapshot isolation, as simply as it can be written: every committed version of every key,
/// numbered by commit. The rules for writes and deletes match the database's, including that
/// deleting a key the snapshot does not have writes nothing.
/// </summary>
internal sealed class SnapshotModel
{
    private readonly Dictionary<byte, List<(long Commit, byte[]? Value)>> versions = [];
    private long lastCommit;

    public Transaction Begin() => new(this, lastCommit);

    private byte[]? Visible(byte key, long snapshot) =>
        versions.TryGetValue(key, out var history)
            ? history.LastOrDefault(version => version.Commit <= snapshot).Value
            : null;

    internal sealed class Transaction(SnapshotModel model, long snapshot)
    {
        private readonly SortedDictionary<byte, byte[]?> writes = [];

        public byte[]? Get(byte[] key) =>
            writes.TryGetValue(key[0], out var written) ? written : model.Visible(key[0], snapshot);

        public List<KeyValuePair<byte[], byte[]>> Scan(byte[] from, byte[] to)
        {
            var result = new List<KeyValuePair<byte[], byte[]>>();
            for (int key = from[0]; key < to[0] && key < 256; key++)
            {
                if (Get([(byte)key]) is { } value)
                {
                    result.Add(new([(byte)key], value));
                }
            }
            return result;
        }

        public void Put(byte[] key, byte[] value) => writes[key[0]] = value;

        public bool Delete(byte[] key)
        {
            bool inSnapshot = model.Visible(key[0], snapshot) is not null;
            bool existed = writes.TryGetValue(key[0], out var written) ? written is not null : inSnapshot;
            if (inSnapshot)
            {
                writes[key[0]] = null;
            }
            else
            {
                writes.Remove(key[0]);
            }
            return existed;
        }

        /// <summary>First committer wins: fail if any key written here was committed since the snapshot.</summary>
        public bool Commit()
        {
            if (writes.Count == 0)
            {
                return true;
            }
            foreach (byte key in writes.Keys)
            {
                if (model.versions.TryGetValue(key, out var history) && history[^1].Commit > snapshot)
                {
                    return false;
                }
            }
            long commit = ++model.lastCommit;
            foreach (var (key, value) in writes)
            {
                if (!model.versions.TryGetValue(key, out var history))
                {
                    model.versions[key] = history = [];
                }
                history.Add((commit, value));
            }
            return true;
        }
    }
}
