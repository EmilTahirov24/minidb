using MiniDb.Log;
using MiniDb.Pages;
using MiniDb.Tests.Simulation;

namespace MiniDb.Tests.Log;

public class WriteAheadLogTests
{
    [Fact]
    public void Committed_transactions_come_back_in_the_order_they_were_written()
    {
        var (_, file) = NewLog(out var log);
        log.Append([(3, Image(1)), (5, Image(2))]);
        log.Append([(3, Image(3))]);
        log.Flush();

        var committed = WriteAheadLog.Open(file).ReadCommitted();

        Assert.Equal([3u, 5u, 3u], committed.Select(c => c.Page));
        Assert.Equal([1, 2, 3], committed.Select(c => (int)c.Image[100]));
    }

    [Fact]
    public void A_transaction_whose_commit_frame_is_missing_does_not_count()
    {
        var (_, file) = NewLog(out var log);
        log.Append([(3, Image(1))]);
        log.Append([(4, Image(2)), (5, Image(3))]);
        log.Flush();
        // The second transaction's commit frame never made it.
        file.SetLength(WriteAheadLog.HeaderSize + 2 * WriteAheadLog.FrameSize);

        var committed = WriteAheadLog.Open(file).ReadCommitted();

        Assert.Equal([3u], committed.Select(c => c.Page));
    }

    [Fact]
    public void Reading_stops_at_the_first_damaged_frame_even_if_later_ones_are_intact()
    {
        var (_, file) = NewLog(out var log);
        log.Append([(3, Image(1))]);
        log.Append([(4, Image(2))]);
        log.Append([(5, Image(3))]);
        log.Flush();
        // Damage the second frame: the third is intact, but a disk that reorders writes could
        // have kept it while losing the second, so it must not count either.
        var bytes = new byte[1];
        long inSecond = WriteAheadLog.HeaderSize + WriteAheadLog.FrameSize + 1000;
        file.Read(inSecond, bytes);
        bytes[0] ^= 0xFF;
        file.Write(inSecond, bytes);

        var committed = WriteAheadLog.Open(file).ReadCommitted();

        Assert.Equal([3u], committed.Select(c => c.Page));
    }

    [Fact]
    public void After_a_reset_no_frame_written_before_it_counts()
    {
        var (_, file) = NewLog(out var log);
        log.Append([(3, Image(1)), (4, Image(1))]);
        log.Flush();
        log.Reset();

        Assert.Empty(WriteAheadLog.Open(file).ReadCommitted());
    }

    [Fact]
    public void After_a_damaged_header_the_next_generation_is_above_every_old_frame()
    {
        // Frames of an old generation, then a reset whose header write is torn.
        var (_, file) = NewLog(out var log);
        log.Append([(3, Image(1))]);
        log.Append([(4, Image(2))]);
        log.Append([(5, Image(3))]);
        log.Flush();
        log.Reset();
        file.Write(0, new byte[WriteAheadLog.HeaderSize]);

        // The damaged log holds nothing. Reset and write one new transaction over the start of
        // the file: had the old generation come back, the old frames after it would read as
        // committed transactions, and replay stale pages over newer ones.
        var reopened = WriteAheadLog.Open(file);
        Assert.Empty(reopened.ReadCommitted());
        reopened.Reset();
        reopened.Append([(9, Image(9))]);
        reopened.Flush();

        var committed = WriteAheadLog.Open(file).ReadCommitted();
        Assert.Equal([9u], committed.Select(c => c.Page));
    }

    private static (SimulatedDisk Disk, MiniDb.Storage.IStorageFile File) NewLog(out WriteAheadLog log)
    {
        var disk = new SimulatedDisk();
        var file = disk.Open("wal");
        log = WriteAheadLog.Open(file);
        log.Reset();
        return (disk, file);
    }

    private static byte[] Image(byte value)
    {
        var page = new byte[Page.Size];
        page.AsSpan(20).Fill(value);
        Page.Seal(page);
        return page;
    }
}
