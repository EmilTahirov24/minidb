using MiniDb.Tests.Simulation;

namespace MiniDb.Tests;

public sealed class DatabaseTests : IDisposable
{
    private static readonly DatabaseOptions Options = new();
    private readonly string directory = Path.Combine(Path.GetTempPath(), "minidb-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void What_was_committed_is_there_after_the_database_is_closed_and_reopened()
    {
        var disk = new SimulatedDisk();
        using (var db = Database.Open(disk, "db", Options))
        {
            using var tx = db.BeginWrite();
            tx.Put("a"u8, "1"u8);
            tx.Put("b"u8, "2"u8);
            tx.Commit();
        }

        using var reopened = Database.Open(disk, "db", Options);
        using var read = reopened.BeginRead();
        Assert.Equal("1"u8.ToArray(), read.Get("a"u8));
        Assert.Equal(2, read.Scan().Count());
    }

    [Fact]
    public void A_transaction_that_does_not_commit_leaves_nothing_behind()
    {
        using var db = Database.Open(new SimulatedDisk(), "db", Options);
        using (var tx = db.BeginWrite())
        {
            tx.Put("a"u8, "1"u8);
            Assert.Equal("1"u8.ToArray(), tx.Get("a"u8)); // it sees its own change
        }

        using var read = db.BeginRead();
        Assert.Null(read.Get("a"u8));
        Assert.Empty(read.Scan());
    }

    [Fact]
    public void Committed_changes_reach_a_real_disk_and_come_back()
    {
        string path = Path.Combine(directory, "app");
        using (var db = Database.Open(path))
        {
            using var tx = db.BeginWrite();
            for (int i = 0; i < 2000; i++)
            {
                tx.Put(BitConverter.GetBytes(i), new byte[50]);
            }
            tx.Commit();
        }

        Assert.True(File.Exists(path + ".db"));
        using var reopened = Database.Open(path);
        using (var read = reopened.BeginRead())
        {
            Assert.Equal(2000, read.Scan().Count());
        }
        reopened.Verify();
    }

    [Fact]
    public void A_thread_holding_a_transaction_that_asks_for_another_gets_an_error_not_a_hang()
    {
        using var db = Database.Open(new SimulatedDisk(), "db", Options);
        using (var read = db.BeginRead())
        {
            var error = Assert.Throws<InvalidOperationException>(() => db.BeginWrite());
            Assert.Contains("wait for ever", error.Message);
            Assert.Throws<InvalidOperationException>(() => db.BeginRead());
            Assert.Throws<InvalidOperationException>(db.Checkpoint);
        }
        using var write = db.BeginWrite(); // free again once the first one is done
    }

    [Fact]
    public void Another_thread_waits_its_turn_and_then_gets_it()
    {
        using var db = Database.Open(new SimulatedDisk(), "db", Options);
        var read = db.BeginRead();
        var other = new Thread(() =>
        {
            using var tx = db.BeginWrite();
            tx.Put("a"u8, "1"u8);
            tx.Commit();
        });
        other.Start();

        Assert.False(other.Join(200), "the second transaction did not wait for the first");
        read.Dispose();
        Assert.True(other.Join(10_000), "the second transaction never got its turn");

        using var check = db.BeginRead();
        Assert.Equal("1"u8.ToArray(), check.Get("a"u8));
    }

    [Fact]
    public void A_database_that_hit_an_io_error_refuses_work_until_it_is_reopened()
    {
        var disk = new SimulatedDisk(crashAt: 8);
        var db = Database.Open(disk, "db", Options);
        Assert.Throws<SimulatedCrashException>(() =>
        {
            for (int i = 0; i < 1000; i++)
            {
                using var tx = db.BeginWrite();
                tx.Put(BitConverter.GetBytes(i), "x"u8);
                tx.Commit();
            }
        });

        Assert.Throws<InvalidOperationException>(() => db.BeginWrite());
        db.Dispose(); // closes without writing anything more

        using var reopened = Database.Open(disk.Reboot(Survival.Nothing), "db", Options);
        reopened.Verify();
    }

    [Fact]
    public void A_log_left_without_its_database_is_not_replayed_onto_a_new_one()
    {
        var disk = new SimulatedDisk();
        using (var db = Database.Open(disk, "db", new DatabaseOptions { CheckpointAfterBytes = long.MaxValue }))
        {
            using var tx = db.BeginWrite();
            tx.Put("old"u8, "value"u8);
            tx.Commit();
            // Closed without a checkpoint below: the change stays only in the log.
            disk = disk.Reboot(Survival.Everything);
        }
        disk.Delete("db.db");

        using var fresh = Database.Open(disk, "db", Options);
        using var read = fresh.BeginRead();
        Assert.Empty(read.Scan());
    }

    [Fact]
    public void A_key_too_large_is_refused_and_the_transaction_can_still_commit()
    {
        using var db = Database.Open(new SimulatedDisk(), "db", Options);
        using (var tx = db.BeginWrite())
        {
            Assert.Throws<ArgumentException>(() => tx.Put(new byte[2000], "x"u8));
            tx.Put("a"u8, "1"u8);
            tx.Commit();
        }
        using var read = db.BeginRead();
        Assert.Single(read.Scan());
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
