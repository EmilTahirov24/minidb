using System.Buffers.Binary;

namespace MiniDb.Pages;

/// <summary>
/// Page 0: what the file is, and where the tree and the free pages are. It changes inside
/// transactions and goes through the log like any other page.
/// </summary>
/// <remarks>
/// <code>
/// offset  size  field
/// 0       4     checksum
/// 4       1     type (header)
/// 5       1     format version
/// 6       2     page size
/// 8       8     magic, "MiniDB" and two zero bytes
/// 16      4     root page
/// 20      4     page count: pages in use, the header and free pages included
/// 24      4     first free page, or 0
/// 28      4     number of free pages
/// </code>
/// </remarks>
internal readonly ref struct HeaderPage
{
    public const byte FormatVersion = 1;

    private static ReadOnlySpan<byte> Magic => "MiniDB\0\0"u8;

    private readonly Span<byte> bytes;

    public HeaderPage(Span<byte> bytes)
    {
        if (bytes.Length != Page.Size)
        {
            throw new ArgumentException($"a page is {Page.Size} bytes", nameof(bytes));
        }
        this.bytes = bytes;
    }

    /// <summary>A header for a new database whose tree is the single, empty leaf at page 1.</summary>
    public static HeaderPage Format(Span<byte> bytes)
    {
        bytes.Clear();
        bytes[4] = (byte)PageType.Header;
        bytes[5] = FormatVersion;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[6..], (ushort)Page.Size);
        Magic.CopyTo(bytes[8..]);
        var header = new HeaderPage(bytes)
        {
            Root = 1,
            PageCount = 2,
        };
        return header;
    }

    public uint Root
    {
        get => BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]);
        set => BinaryPrimitives.WriteUInt32LittleEndian(bytes[16..], value);
    }

    public uint PageCount
    {
        get => BinaryPrimitives.ReadUInt32LittleEndian(bytes[20..]);
        set => BinaryPrimitives.WriteUInt32LittleEndian(bytes[20..], value);
    }

    public uint FirstFree
    {
        get => BinaryPrimitives.ReadUInt32LittleEndian(bytes[24..]);
        set => BinaryPrimitives.WriteUInt32LittleEndian(bytes[24..], value);
    }

    public uint FreeCount
    {
        get => BinaryPrimitives.ReadUInt32LittleEndian(bytes[28..]);
        set => BinaryPrimitives.WriteUInt32LittleEndian(bytes[28..], value);
    }

    /// <summary>Throw unless this is an intact header of a database this code can read.</summary>
    public void Validate()
    {
        if (!bytes[8..16].SequenceEqual(Magic))
        {
            throw new InvalidDataException("not a MiniDB database");
        }
        if (!Page.IsIntact(bytes))
        {
            throw new InvalidDataException("the database header is damaged");
        }
        if (bytes[5] != FormatVersion)
        {
            throw new InvalidDataException($"format version {bytes[5]}; this build reads {FormatVersion}");
        }
        int pageSize = BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]);
        if (pageSize != Page.Size)
        {
            throw new InvalidDataException($"pages of {pageSize} bytes; this build uses {Page.Size}");
        }
    }
}
