using MiniDb.Tests.Simulation;
using MiniDb.Tests.Tree;

using Xunit.Abstractions;

namespace MiniDb.Tests;

/// <summary>
/// A crash at every disk operation of a workload, under every rule for what an unflushed write
/// leaves behind. After each one the database is reopened, which recovers it, and it must equal
/// the model as of the last commit that returned, or - if a commit was under way - as of that
/// one. Nothing in between: no transaction half applied, no committed one lost.
/// </summary>
public class CrashTests(ITestOutputHelper output)
{
    // A tiny cache and a small log, so that pages are evicted and written to the data file
    // between checkpoints, and checkpoints happen inside the workload.
    private static readonly DatabaseOptions Options = new() { CachePages = 4, CheckpointAfterBytes = 12 * 4120 };

    private static readonly (Survival Survival, int Seed)[] Afterwards =
    [
        (Survival.Nothing, 0),
        (Survival.Everything, 0),
        (Survival.RandomSectors, 1),
        (Survival.RandomSectors, 2),
    ];

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    public void The_database_recovers_to_a_committed_state_after_a_crash_at_any_write(int seed)
    {
        var plan = Workload.Make(seed);
        long operations = Workload.Run(new SimulatedDisk(), plan, Options).Disk.Operations;

        for (long crash = 0; crash < operations; crash++)
        {
            var (disk, committed, pending) = Workload.Run(new SimulatedDisk(crash), plan, Options);
            Assert.True(disk.Crashed, $"no crash at operation {crash}");
            foreach (var (survival, sectors) in Afterwards)
            {
                CheckRecovery(disk.Reboot(survival, sectors), committed, pending, $"crash at {crash}, {survival} {sectors}");
            }
        }
        output.WriteLine($"workload {seed}: {operations} crash points, {operations * Afterwards.Length} recoveries");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void A_crash_during_recovery_is_repaired_by_the_next_recovery(int seed)
    {
        var plan = Workload.Make(seed);
        long operations = Workload.Run(new SimulatedDisk(), plan, Options).Disk.Operations;
        int checkedPoints = 0;

        // Every seventh first crash, and then a crash at every operation of the recovery after it.
        for (long crash = 3; crash < operations; crash += 7)
        {
            var (disk, committed, pending) = Workload.Run(new SimulatedDisk(crash), plan, Options);
            var start = disk.Reboot(Survival.Everything);
            long recovering = RecoveryOperations(start.Reboot(Survival.Nothing));

            for (long second = 0; second < recovering; second++)
            {
                var again = start.Reboot(Survival.Nothing, crashAt: second);
                Assert.Throws<SimulatedCrashException>(() => Database.Open(again, "db", Options));
                foreach (var (survival, sectors) in Afterwards)
                {
                    CheckRecovery(again.Reboot(survival, sectors), committed, pending,
                        $"crash at {crash}, then at {second} of the recovery, {survival} {sectors}");
                }
                checkedPoints++;
            }
        }
        output.WriteLine($"workload {seed}: {checkedPoints} crash points inside recoveries");
    }

    /// <summary>
    /// Reopen, check the tree and every page, compare with the model; then show the recovered
    /// database still works: commit to it, close it, and find the commit there after reopening.
    /// </summary>
    private static void CheckRecovery(
        SimulatedDisk disk, SortedDictionary<byte[], byte[]> committed, SortedDictionary<byte[], byte[]>? pending, string where)
    {
        try
        {
            using (var db = Database.Open(disk, "db", Options))
            {
                db.Verify();
                var contents = Contents(db);
                bool asCommitted = SameAs(committed, contents);
                bool asPending = pending is not null && SameAs(pending, contents);
                Assert.True(asCommitted || asPending, "the recovered database is neither the committed state nor the pending one");

                using var tx = db.BeginWrite();
                tx.Put("after recovery"u8, "yes"u8);
                tx.Commit();
            }
            using var reopened = Database.Open(disk, "db", Options);
            reopened.Verify();
            using var read = reopened.BeginRead();
            Assert.Equal("yes"u8.ToArray(), read.Get("after recovery"u8));
        }
        catch (Exception failure)
        {
            throw new InvalidOperationException($"{where}: {failure.Message}", failure);
        }
    }

    private static List<KeyValuePair<byte[], byte[]>> Contents(Database db)
    {
        using var read = db.BeginRead();
        return read.Scan().ToList();
    }

    private static bool SameAs(SortedDictionary<byte[], byte[]> model, List<KeyValuePair<byte[], byte[]>> contents) =>
        model.Count == contents.Count
        && model.Zip(contents).All(pair =>
            pair.First.Key.AsSpan().SequenceEqual(pair.Second.Key)
            && pair.First.Value.AsSpan().SequenceEqual(pair.Second.Value));

    /// <summary>How many disk operations opening the database - recovering it - takes.</summary>
    private static long RecoveryOperations(SimulatedDisk disk)
    {
        var db = Database.Open(disk, "db", Options);
        long operations = disk.Operations;
        db.Dispose();
        return operations;
    }
}

/// <summary>A reproducible run of transactions, and the model of what the database should hold.</summary>
internal static class Workload
{
    public sealed record Operation(byte[] Key, byte[]? Value);

    public sealed record Transaction(List<Operation> Operations, bool Commits, bool CheckpointAfter);

    public static List<Transaction> Make(int seed, int transactions = 60)
    {
        var random = new Random(seed);
        var plan = new List<Transaction>();
        for (int t = 0; t < transactions; t++)
        {
            var operations = new List<Operation>();
            for (int n = random.Next(1, 12); n > 0; n--)
            {
                // Few enough keys that deletes find them, values large enough that pages split.
                var key = BitConverter.GetBytes(random.Next(0, 400));
                byte[]? value = random.NextDouble() < 0.25 ? null : new byte[random.NextDouble() < 0.1 ? random.Next(600, 900) : random.Next(0, 250)];
                if (value is not null)
                {
                    random.NextBytes(value);
                }
                operations.Add(new Operation(key, value));
            }
            plan.Add(new Transaction(operations, Commits: random.NextDouble() < 0.85, CheckpointAfter: random.NextDouble() < 0.1));
        }
        return plan;
    }

    /// <summary>
    /// Run the plan until it ends or the disk crashes. Returns the model as of the last commit
    /// that returned, and, if a commit was under way at the crash, the model it would have made.
    /// </summary>
    public static (SimulatedDisk Disk, SortedDictionary<byte[], byte[]> Committed, SortedDictionary<byte[], byte[]>? Pending) Run(
        SimulatedDisk disk, List<Transaction> plan, DatabaseOptions options)
    {
        var committed = new SortedDictionary<byte[], byte[]>(ByteComparer.Instance);
        SortedDictionary<byte[], byte[]>? pending = null;
        try
        {
            var db = Database.Open(disk, "db", options);
            foreach (var transaction in plan)
            {
                var next = new SortedDictionary<byte[], byte[]>(committed, ByteComparer.Instance);
                var tx = db.BeginWrite();
                foreach (var operation in transaction.Operations)
                {
                    if (operation.Value is null)
                    {
                        tx.Delete(operation.Key);
                        next.Remove(operation.Key);
                    }
                    else
                    {
                        tx.Put(operation.Key, operation.Value);
                        next[operation.Key] = operation.Value;
                    }
                }
                if (!transaction.Commits)
                {
                    tx.Dispose();
                    continue;
                }
                pending = next;
                tx.Commit();
                committed = next;
                pending = null;
                if (transaction.CheckpointAfter)
                {
                    db.Checkpoint();
                }
            }
            db.Dispose();
        }
        catch (SimulatedCrashException)
        {
        }
        return (disk, committed, pending);
    }
}
