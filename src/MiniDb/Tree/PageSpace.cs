using System.Buffers.Binary;

using MiniDb.Pages;

namespace MiniDb.Tree;

/// <summary>
/// The pages of a database as the tree uses them, with the bookkeeping every kind of access
/// shares: where the root is, and which pages are free. Both live in the header page, so they
/// change through the same reads and writes as the tree itself.
/// </summary>
/// <remarks>
/// Free pages form a list, each holding the number of the next in its <c>right</c> field. A
/// freed page goes to the front; a page is allocated from the front, or past the last page when
/// the list is empty.
/// </remarks>
internal abstract class PageSpace : IPages
{
    private const int NextFreeOffset = 12;

    public abstract byte[] Read(uint page);

    /// <summary>The page to change. A page past the last one in use starts out as zeros.</summary>
    public abstract byte[] Modify(uint page);

    public uint Root
    {
        get => new HeaderPage(Read(0)).Root;
        set => new HeaderPage(Modify(0)).Root = value;
    }

    public uint PageCount => new HeaderPage(Read(0)).PageCount;

    public uint Allocate()
    {
        var header = new HeaderPage(Modify(0));
        uint id = header.FirstFree;
        if (id == Page.None)
        {
            id = header.PageCount;
            header.PageCount = id + 1;
        }
        else
        {
            header.FirstFree = BinaryPrimitives.ReadUInt32LittleEndian(Read(id).AsSpan(NextFreeOffset));
            header.FreeCount--;
        }
        Modify(id).AsSpan().Clear();
        return id;
    }

    public void Free(uint page)
    {
        if (page == 0 || page >= PageCount)
        {
            throw new ArgumentOutOfRangeException(nameof(page), $"page {page} is not a page in use");
        }
        var header = new HeaderPage(Modify(0));
        byte[] bytes = Modify(page);
        bytes.AsSpan().Clear();
        bytes[4] = (byte)PageType.Free;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(NextFreeOffset), header.FirstFree);
        header.FirstFree = page;
        header.FreeCount++;
    }

    /// <summary>
    /// Check that every page below the page count is exactly one of: the header, one of
    /// <paramref name="inTree"/>, or on the free list, which must be as long as the header says.
    /// </summary>
    public void VerifyAccounting(IReadOnlySet<uint> inTree)
    {
        var header = new HeaderPage(Read(0));
        var free = new HashSet<uint>();
        for (uint id = header.FirstFree; id != Page.None;)
        {
            if (id >= header.PageCount || inTree.Contains(id) || !free.Add(id))
            {
                throw new InvalidDataException($"page {id} is on the free list and also elsewhere, or loops it");
            }
            byte[] bytes = Read(id);
            if (Page.TypeOf(bytes) != PageType.Free)
            {
                throw new InvalidDataException($"page {id} is on the free list but is a {Page.TypeOf(bytes)} page");
            }
            id = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(NextFreeOffset));
        }
        if (free.Count != header.FreeCount)
        {
            throw new InvalidDataException($"{free.Count} free pages listed; the header says {header.FreeCount}");
        }
        for (uint id = 1; id < header.PageCount; id++)
        {
            if (!inTree.Contains(id) && !free.Contains(id))
            {
                throw new InvalidDataException($"page {id} is neither in the tree nor free: it is lost");
            }
        }
        uint pageCount = header.PageCount;
        if (inTree.Any(id => id == 0 || id >= pageCount))
        {
            throw new InvalidDataException("the tree uses a page outside the pages in use");
        }
    }
}
