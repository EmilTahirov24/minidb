using System.Buffers.Binary;

namespace MiniDb.Pages;

/// <summary>
/// A B+tree node: a slotted page. Two-byte slots, kept in key order, point at cells that grow
/// down from the end of the page in whatever order they were written. Inserting a key moves
/// slots, never whole records; deleting one leaves a hole, counted until the page is compacted.
/// </summary>
/// <remarks>
/// <code>
/// offset  size  field
/// 0       4     checksum
/// 4       1     type (leaf or internal)
/// 5       1     reserved
/// 6       2     count: cells in the page
/// 8       2     cell start: where the cell content area begins
/// 10      2     fragmented: bytes lost to deleted cells
/// 12      4     right: the next leaf, or an internal page's rightmost child
/// 16      4     left: the previous leaf; 0 in an internal page
/// 20            slots, count x 2 bytes
/// </code>
/// </remarks>
internal readonly ref struct NodePage
{
    public const int HeaderSize = 20;
    public const int SlotSize = 2;

    /// <summary>
    /// The largest cell a page accepts. A cell and its slot may use at most a quarter of the
    /// space after the header, which is what makes a split always succeed: half a full page
    /// plus one more cell still fits in a page.
    /// </summary>
    public const int MaxCellSize = (Page.Size - HeaderSize) / 4 - SlotSize;

    private readonly Span<byte> bytes;

    public NodePage(Span<byte> bytes)
    {
        if (bytes.Length != Page.Size)
        {
            throw new ArgumentException($"a page is {Page.Size} bytes", nameof(bytes));
        }
        this.bytes = bytes;
    }

    /// <summary>An empty leaf or internal page, every byte after the header zero.</summary>
    public static NodePage Format(Span<byte> bytes, PageType type)
    {
        if (type is not (PageType.Leaf or PageType.Internal))
        {
            throw new ArgumentException($"a node is a leaf or an internal page, not {type}", nameof(type));
        }
        bytes.Clear();
        bytes[4] = (byte)type;
        var page = new NodePage(bytes)
        {
            CellStart = Page.Size,
        };
        return page;
    }

    public PageType Type => (PageType)bytes[4];

    public int Count
    {
        get => BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]);
        private set => BinaryPrimitives.WriteUInt16LittleEndian(bytes[6..], (ushort)value);
    }

    public uint Right
    {
        get => BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
        set => BinaryPrimitives.WriteUInt32LittleEndian(bytes[12..], value);
    }

    public uint Left
    {
        get => BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]);
        set => BinaryPrimitives.WriteUInt32LittleEndian(bytes[16..], value);
    }

    /// <summary>Bytes a new cell and its slot can use, once the page is compacted.</summary>
    public int FreeSpace => ContiguousFree + Fragmented;

    public ReadOnlySpan<byte> Cell(int index)
    {
        int offset = CellOffset(index);
        return bytes.Slice(offset, CellSizeAt(bytes, offset));
    }

    public ReadOnlySpan<byte> KeyAt(int index) =>
        Type == PageType.Leaf ? LeafCell.Key(Cell(index)) : InternalCell.Key(Cell(index));

    /// <summary>Point the internal cell at <paramref name="index"/> at another child page.</summary>
    public void SetChild(int index, uint child) =>
        InternalCell.SetChild(bytes[CellOffset(index)..], child);

    /// <summary>
    /// Put <paramref name="cell"/> at slot <paramref name="index"/>, moving the slots after it
    /// along. False, with the page unchanged, if it does not fit even after compaction.
    /// </summary>
    public bool TryInsert(int index, ReadOnlySpan<byte> cell)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(cell.Length, MaxCellSize);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, Count);

        int needed = cell.Length + SlotSize;
        if (ContiguousFree < needed)
        {
            if (FreeSpace < needed)
            {
                return false;
            }
            Compact();
        }

        int offset = CellStart - cell.Length;
        cell.CopyTo(bytes[offset..]);
        CellStart = offset;

        int count = Count;
        bytes.Slice(SlotOffset(index), (count - index) * SlotSize).CopyTo(bytes[SlotOffset(index + 1)..]);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[SlotOffset(index)..], (ushort)offset);
        Count = count + 1;
        return true;
    }

    /// <summary>Take out the cell at slot <paramref name="index"/>; its bytes become free space.</summary>
    public void Remove(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);

        int offset = CellOffset(index);
        int size = CellSizeAt(bytes, offset);
        int count = Count;
        bytes.Slice(SlotOffset(index + 1), (count - index - 1) * SlotSize).CopyTo(bytes[SlotOffset(index)..]);
        Count = --count;

        if (count == 0)
        {
            // Nothing left to keep: the whole page is free again.
            CellStart = Page.Size;
            Fragmented = 0;
            bytes[HeaderSize..].Clear();
        }
        else
        {
            // Deleted bytes do not linger on disk.
            bytes.Slice(offset, size).Clear();
            if (offset == CellStart)
            {
                // The lowest cell: the content area just shrinks, and no hole is left behind.
                CellStart = offset + size;
            }
            else
            {
                Fragmented += size;
            }
        }
    }

    /// <summary>Move every cell to the end of the page, in slot order, so the free space is one run.</summary>
    public void Compact()
    {
        Span<byte> before = stackalloc byte[Page.Size];
        bytes.CopyTo(before);

        int end = Page.Size;
        int count = Count;
        for (int i = 0; i < count; i++)
        {
            int offset = BinaryPrimitives.ReadUInt16LittleEndian(before[SlotOffset(i)..]);
            int size = CellSizeAt(before, offset);
            end -= size;
            before.Slice(offset, size).CopyTo(bytes[end..]);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes[SlotOffset(i)..], (ushort)end);
        }
        CellStart = end;
        Fragmented = 0;
        bytes[SlotOffset(count)..end].Clear();
    }

    private int CellStart
    {
        get => BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]);
        set => BinaryPrimitives.WriteUInt16LittleEndian(bytes[8..], (ushort)value);
    }

    private int Fragmented
    {
        get => BinaryPrimitives.ReadUInt16LittleEndian(bytes[10..]);
        set => BinaryPrimitives.WriteUInt16LittleEndian(bytes[10..], (ushort)value);
    }

    private int ContiguousFree => CellStart - SlotOffset(Count);

    private static int SlotOffset(int index) => HeaderSize + index * SlotSize;

    private int CellOffset(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
        return BinaryPrimitives.ReadUInt16LittleEndian(bytes[SlotOffset(index)..]);
    }

    private static int CellSizeAt(ReadOnlySpan<byte> page, int offset) =>
        (PageType)page[4] == PageType.Leaf
            ? LeafCell.SizeAt(page[offset..])
            : InternalCell.SizeAt(page[offset..]);
}
