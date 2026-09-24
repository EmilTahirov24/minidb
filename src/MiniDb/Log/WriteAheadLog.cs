using System.Buffers.Binary;

using MiniDb.Pages;
using MiniDb.Storage;

namespace MiniDb.Log;

/// <summary>
/// The write-ahead log: every page a committed transaction changed, as a whole image, appended
/// in order. A change is durable once its frames are flushed here; the data file catches up at
/// checkpoints, and recovery can always rebuild it from the log.
/// </summary>
/// <remarks>
/// <code>
/// header (32 bytes)                      frame (24 bytes, then the page)
/// 0   8  magic "MiniWAL" and a zero      0   4  page
/// 8   4  format version                  4   4  flags: bit 0 commits the transaction
/// 12  4  page size                       8   8  generation, equal to the header's
/// 16  8  generation                      16  4  CRC32C of bytes 0..15 and the image
/// 24  4  CRC32C of bytes 0..23           20  4  reserved
/// 28  4  reserved                        24     the page image
/// </code>
/// Reading stops at the first frame whose checksum or generation is wrong, and a transaction
/// counts only if its commit frame does. Resetting the log writes a header with the next
/// generation, which makes every frame of the old one dead without truncating the file.
/// </remarks>
internal sealed class WriteAheadLog
{
    public const int HeaderSize = 32;
    public const int FrameHeaderSize = 24;
    public const int FrameSize = FrameHeaderSize + Page.Size;
    private const uint FormatVersion = 1;
    private const uint CommitFlag = 1;

    private static ReadOnlySpan<byte> Magic => "MiniWAL\0"u8;

    private readonly IStorageFile file;

    /// <summary>True when the header was missing or damaged: then no frame counts until a reset.</summary>
    private bool holdsNothing;

    private WriteAheadLog(IStorageFile file, ulong generation, long end)
    {
        this.file = file;
        Generation = generation;
        End = end;
    }

    public ulong Generation { get; private set; }

    /// <summary>Where the next frame goes: the end of the valid frames.</summary>
    public long End { get; private set; }

    /// <summary>Bytes of frames since the last reset.</summary>
    public long Size => End - HeaderSize;

    /// <summary>
    /// Open the log in <paramref name="file"/>. A missing or damaged header means the log holds
    /// nothing: a header is only ever written by a reset, and a reset only follows a flush of the
    /// data file with everything the log held.
    /// </summary>
    /// <remarks>
    /// With the header gone, its generation is gone too, and the next reset must not reuse a
    /// generation that frames still in the file carry: new frames written over the start of the
    /// file would be read as continuing into those old ones, and the old page images would be
    /// applied on top of newer pages. So the generation is taken from the frames themselves.
    /// </remarks>
    public static WriteAheadLog Open(IStorageFile file)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        bool valid = file.Read(0, header) == HeaderSize
            && header[..8].SequenceEqual(Magic)
            && BinaryPrimitives.ReadUInt32LittleEndian(header[24..]) == Crc32C.Compute(header[..24])
            && BinaryPrimitives.ReadUInt32LittleEndian(header[8..]) == FormatVersion
            && BinaryPrimitives.ReadUInt32LittleEndian(header[12..]) == Page.Size;
        if (!valid)
        {
            return new WriteAheadLog(file, HighestFrameGeneration(file), HeaderSize) { holdsNothing = true };
        }
        var log = new WriteAheadLog(file, BinaryPrimitives.ReadUInt64LittleEndian(header[16..]), HeaderSize);
        log.End = log.ScanEnd();
        return log;
    }

    /// <summary>The highest generation of any intact frame anywhere in the file, or 0.</summary>
    private static ulong HighestFrameGeneration(IStorageFile file)
    {
        ulong highest = 0;
        var frame = new byte[FrameSize];
        for (long offset = HeaderSize; file.Read(offset, frame) == FrameSize; offset += FrameSize)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(16)) == FrameChecksum(frame))
            {
                highest = Math.Max(highest, BinaryPrimitives.ReadUInt64LittleEndian(frame.AsSpan(8)));
            }
        }
        return highest;
    }

    /// <summary>
    /// The page images of every transaction whose commit frame is in the log, in the order they
    /// were written: applying them in order leaves each page at its latest committed image.
    /// </summary>
    public List<(uint Page, byte[] Image)> ReadCommitted()
    {
        var committed = new List<(uint, byte[])>();
        if (holdsNothing)
        {
            return committed;
        }
        var transaction = new List<(uint, byte[])>();
        foreach (var (page, image, commit) in Frames())
        {
            transaction.Add((page, image));
            if (commit)
            {
                committed.AddRange(transaction);
                transaction.Clear();
            }
        }
        return committed;
    }

    /// <summary>
    /// Append one transaction's pages after the last frame, the last one marked as its commit.
    /// Nothing is durable until <see cref="Flush"/>.
    /// </summary>
    public void Append(IReadOnlyList<(uint Page, byte[] Image)> pages)
    {
        if (pages.Count == 0)
        {
            throw new ArgumentException("a transaction with no pages has nothing to log", nameof(pages));
        }
        if (holdsNothing)
        {
            throw new InvalidOperationException("the log has no valid header; reset it before writing");
        }
        var frames = new byte[pages.Count * FrameSize];
        for (int i = 0; i < pages.Count; i++)
        {
            var frame = frames.AsSpan(i * FrameSize, FrameSize);
            var (page, image) = pages[i];
            BinaryPrimitives.WriteUInt32LittleEndian(frame, page);
            BinaryPrimitives.WriteUInt32LittleEndian(frame[4..], i == pages.Count - 1 ? CommitFlag : 0);
            BinaryPrimitives.WriteUInt64LittleEndian(frame[8..], Generation);
            image.CopyTo(frame[FrameHeaderSize..]);
            BinaryPrimitives.WriteUInt32LittleEndian(frame[16..], FrameChecksum(frame));
        }
        // One write for the whole transaction: fewer calls, and still a crash can tear it anywhere.
        file.Write(End, frames);
        End += frames.Length;
    }

    public void Flush() => file.Flush();

    /// <summary>Start a new generation: every frame written so far is dead from now on.</summary>
    public void Reset()
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], FormatVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], Page.Size);
        BinaryPrimitives.WriteUInt64LittleEndian(header[16..], Generation + 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], Crc32C.Compute(header[..24]));
        file.Write(0, header);
        file.Flush();
        Generation++;
        End = HeaderSize;
        holdsNothing = false;
    }

    private IEnumerable<(uint Page, byte[] Image, bool Commit)> Frames()
    {
        var frame = new byte[FrameSize];
        for (long offset = HeaderSize; file.Read(offset, frame) == FrameSize; offset += FrameSize)
        {
            if (BinaryPrimitives.ReadUInt64LittleEndian(frame.AsSpan(8)) != Generation
                || BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(16)) != FrameChecksum(frame))
            {
                yield break;
            }
            uint page = BinaryPrimitives.ReadUInt32LittleEndian(frame);
            bool commit = (BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(4)) & CommitFlag) != 0;
            yield return (page, frame.AsSpan(FrameHeaderSize).ToArray(), commit);
        }
    }

    /// <summary>The offset after the last frame that belongs to a committed transaction.</summary>
    private long ScanEnd()
    {
        long end = HeaderSize, offset = HeaderSize;
        foreach (var (_, _, commit) in Frames())
        {
            offset += FrameSize;
            if (commit)
            {
                end = offset;
            }
        }
        return end;
    }

    private static uint FrameChecksum(ReadOnlySpan<byte> frame)
    {
        // The frame header's first 16 bytes and the image, as if they were one run of bytes.
        Span<byte> covered = stackalloc byte[16 + Page.Size];
        frame[..16].CopyTo(covered);
        frame[FrameHeaderSize..FrameSize].CopyTo(covered[16..]);
        return Crc32C.Compute(covered);
    }
}
