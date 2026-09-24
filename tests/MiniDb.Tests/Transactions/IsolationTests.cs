using System.Buffers.Binary;

using MiniDb.Tests.Simulation;

namespace MiniDb.Tests.Transactions;

/// <summary>
/// One test for each anomaly snapshot isolation is defined by: the ones it prevents must not
/// happen, and the two it allows - write skew and the read-only anomaly - must. Each is an
/// exact interleaving of transactions, written out step by step.
/// </summary>
public sealed class IsolationTests : IDisposable
{
    private readonly Database db = Database.Open(new SimulatedDisk(), "db", new DatabaseOptions());

    [Fact]
    public void A_dirty_read_cannot_happen()
    {
        Set("a", 1);
        using var writer = db.BeginWrite();
        using var reader = db.BeginRead();

        writer.Put(Key("a"), Number(2));      // not committed
        Assert.Equal(1, Read(reader, "a"));

        writer.Commit();
        Assert.Equal(1, Read(reader, "a"));   // committed after the reader began: still invisible
    }

    [Fact]
    public void A_non_repeatable_read_cannot_happen()
    {
        Set("a", 1);
        using var reader = db.BeginWrite();
        Assert.Equal(1, Read(reader, "a"));

        Set("a", 2);

        Assert.Equal(1, Read(reader, "a"));
    }

    [Fact]
    public void A_phantom_cannot_happen()
    {
        Set("k1", 1);
        Set("k3", 3);
        using var reader = db.BeginRead();
        Assert.Equal(2, reader.Scan(Key("k"), Key("l")).Count());

        Set("k2", 2);

        Assert.Equal(2, reader.Scan(Key("k"), Key("l")).Count());
    }

    [Fact]
    public void A_lost_update_is_prevented_and_the_second_commit_changes_nothing()
    {
        Set("counter", 10);
        Set("note", 0);
        using var first = db.BeginWrite();
        using var second = db.BeginWrite();
        long seenByFirst = Read(first, "counter");
        long seenBySecond = Read(second, "counter");

        first.Put(Key("counter"), Number(seenByFirst + 1));
        first.Commit();
        second.Put(Key("counter"), Number(seenBySecond + 1));
        second.Put(Key("note"), Number(99));

        Assert.Throws<WriteConflictException>(second.Commit);
        Assert.Equal(11, Current("counter"));
        Assert.Equal(0, Current("note"));     // none of the failed commit was written
    }

    [Fact]
    public void Write_skew_can_happen()
    {
        // At least one doctor must stay on call. Each transaction checks that the other one is
        // on call, then takes itself off. They write different keys, so neither conflicts.
        Set("alice", 1);
        Set("bob", 1);
        using var alice = db.BeginWrite();
        using var bob = db.BeginWrite();

        if (Read(alice, "bob") == 1)
        {
            alice.Put(Key("alice"), Number(0));
        }
        if (Read(bob, "alice") == 1)
        {
            bob.Put(Key("bob"), Number(0));
        }
        alice.Commit();
        bob.Commit();

        Assert.Equal((0, 0), (Current("alice"), Current("bob")));
    }

    [Fact]
    public void The_read_only_anomaly_can_happen()
    {
        // Fekete, O'Neil and O'Neil (2004). A withdrawal charges a penalty of 1 if it overdraws
        // checking plus savings; a deposit goes to savings; a report reads both balances.
        Set("checking", 0);
        Set("savings", 0);

        using var withdrawal = db.BeginWrite();
        long checking = Read(withdrawal, "checking");
        long savings = Read(withdrawal, "savings");

        using (var deposit = db.BeginWrite())
        {
            deposit.Put(Key("savings"), Number(Read(deposit, "savings") + 20));
            deposit.Commit();
        }

        long reportedChecking, reportedSavings;
        using (var report = db.BeginRead())
        {
            reportedChecking = Read(report, "checking");
            reportedSavings = Read(report, "savings");
        }

        long penalty = checking + savings - 10 < 0 ? 1 : 0;
        withdrawal.Put(Key("checking"), Number(checking - 10 - penalty));
        withdrawal.Commit();

        // The report saw the deposit and not the withdrawal, so the deposit came first - but then
        // the withdrawal would have seen the savings and charged no penalty. No order of the three
        // explains both what the report saw and where the balances ended.
        Assert.Equal((0, 20), (reportedChecking, reportedSavings));
        Assert.Equal((-11, 20), (Current("checking"), Current("savings")));
    }

    [Fact]
    public void Transactions_that_write_different_keys_both_commit()
    {
        using var first = db.BeginWrite();
        using var second = db.BeginWrite();
        first.Put(Key("x"), Number(1));
        second.Put(Key("y"), Number(2));
        first.Commit();
        second.Commit();
        Assert.Equal((1, 2), (Current("x"), Current("y")));
    }

    [Fact]
    public void A_transaction_that_only_reads_never_fails_to_commit()
    {
        Set("a", 1);
        using var reader = db.BeginWrite();
        Read(reader, "a");
        Set("a", 2);
        reader.Commit();
    }

    [Fact]
    public void A_transaction_sees_its_own_changes_in_reads_and_scans_and_nobody_else_does()
    {
        Set("k1", 1);
        Set("k2", 2);
        using var writer = db.BeginWrite();
        writer.Put(Key("k3"), Number(3));
        writer.Put(Key("k1"), Number(10));
        Assert.True(writer.Delete(Key("k2")));

        Assert.Equal(10, Read(writer, "k1"));
        Assert.Null(writer.Get(Key("k2")));
        Assert.Equal(["k1", "k3"], writer.Scan().Select(pair => System.Text.Encoding.ASCII.GetString(pair.Key)));

        using var other = db.BeginRead();
        Assert.Equal(["k1", "k2"], other.Scan().Select(pair => System.Text.Encoding.ASCII.GetString(pair.Key)));
    }

    [Fact]
    public void A_delete_of_a_key_that_was_never_committed_writes_nothing_and_cannot_conflict()
    {
        using var first = db.BeginWrite();
        using var second = db.BeginWrite();
        first.Put(Key("a"), Number(1));
        Assert.True(first.Delete(Key("a")));   // its own put, undone
        second.Put(Key("a"), Number(2));
        second.Commit();

        first.Commit();                         // nothing to write, nothing to conflict with
        Assert.Equal(2, Current("a"));
    }

    public void Dispose() => db.Dispose();

    private void Set(string key, long value)
    {
        using var tx = db.BeginWrite();
        tx.Put(Key(key), Number(value));
        tx.Commit();
    }

    private long Current(string key)
    {
        using var tx = db.BeginRead();
        return Read(tx, key);
    }

    private static long Read(ReadTransaction tx, string key) => BinaryPrimitives.ReadInt64BigEndian(tx.Get(Key(key)));

    private static long Read(WriteTransaction tx, string key) => BinaryPrimitives.ReadInt64BigEndian(tx.Get(Key(key)));

    private static byte[] Key(string key) => System.Text.Encoding.ASCII.GetBytes(key);

    private static byte[] Number(long value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        return bytes;
    }
}
