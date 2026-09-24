using System.Buffers.Binary;

using MiniDb.Tests.Simulation;

using Xunit.Abstractions;

namespace MiniDb.Tests.Transactions;

/// <summary>
/// Real threads moving money between accounts while other threads add up every balance. A
/// transfer that conflicts is retried. Every sum any reader sees, and the final one, must be
/// the total the bank started with.
/// </summary>
public class BankTests(ITestOutputHelper output)
{
    private const int Accounts = 12;
    private const long Opening = 1_000;

    [Fact]
    public void Concurrent_transfers_never_create_or_destroy_money_and_every_snapshot_adds_up()
    {
        using var db = Database.Open(new SimulatedDisk(), "bank", new DatabaseOptions { CachePages = 16 });
        using (var setup = db.BeginWrite())
        {
            for (int account = 0; account < Accounts; account++)
            {
                setup.Put(Account(account), Amount(Opening));
            }
            setup.Commit();
        }

        long transfers = 0, conflicts = 0, sums = 0;
        var failures = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var stop = new CancellationTokenSource();

        // An exception on a thread of its own would end the whole test run; it is kept and
        // reported as a failure instead.
        Thread Guarded(Action body) => new(() =>
        {
            try
            {
                body();
            }
            catch (Exception error)
            {
                failures.Enqueue(error.ToString());
                stop.Cancel();
            }
        });

        var movers = Enumerable.Range(0, 4).Select(t => Guarded(() =>
        {
            var random = new Random(t);
            for (int i = 0; i < 300; i++)
            {
                int from = random.Next(Accounts), to = (from + 1 + random.Next(Accounts - 1)) % Accounts;
                long amount = random.Next(1, 100);
                while (true)
                {
                    using var tx = db.BeginWrite();
                    long balance = Read(tx.Get(Account(from)));
                    if (balance < amount)
                    {
                        break;
                    }
                    tx.Put(Account(from), Amount(balance - amount));
                    tx.Put(Account(to), Amount(Read(tx.Get(Account(to))) + amount));
                    try
                    {
                        tx.Commit();
                        Interlocked.Increment(ref transfers);
                        break;
                    }
                    catch (WriteConflictException)
                    {
                        Interlocked.Increment(ref conflicts);
                    }
                }
            }
        })).ToList();

        var auditors = Enumerable.Range(0, 2).Select(_ => Guarded(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                using var tx = db.BeginRead();
                long total = tx.Scan().Sum(pair => Read(pair.Value));
                if (total != Accounts * Opening)
                {
                    failures.Enqueue($"a snapshot added up to {total}");
                }
                Interlocked.Increment(ref sums);
            }
        })).ToList();

        auditors.ForEach(thread => thread.Start());
        movers.ForEach(thread => thread.Start());
        movers.ForEach(thread => thread.Join());
        stop.Cancel();
        auditors.ForEach(thread => thread.Join());

        Assert.Empty(failures);
        using (var final = db.BeginRead())
        {
            Assert.Equal(Accounts * Opening, final.Scan().Sum(pair => Read(pair.Value)));
        }
        db.Verify();
        Assert.Equal(0, db.Cache.KeptVersions);
        Assert.True(conflicts > 0, "no transfer ever conflicted: the test did not test conflicts");
        output.WriteLine($"{transfers} transfers, {conflicts} conflicts retried, {sums} sums checked");
    }

    private static byte[] Account(int number) => [(byte)'a', (byte)number];

    private static byte[] Amount(long value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        return bytes;
    }

    private static long Read(byte[]? bytes) => BinaryPrimitives.ReadInt64BigEndian(bytes);
}
