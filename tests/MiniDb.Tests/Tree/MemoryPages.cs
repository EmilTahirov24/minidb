using MiniDb.Pages;
using MiniDb.Tree;

namespace MiniDb.Tests.Tree;

/// <summary>
/// Pages in a dictionary, for testing the tree on its own: no cache, no log, no disk. The free
/// list and the header are the real ones.
/// </summary>
internal sealed class MemoryPages : PageSpace
{
    private readonly Dictionary<uint, byte[]> pages = [];

    public MemoryPages()
    {
        var header = new byte[Page.Size];
        HeaderPage.Format(header);
        pages[0] = header;
        var root = new byte[Page.Size];
        NodePage.Format(root, PageType.Leaf);
        pages[1] = root;
    }

    public uint FreeCount => new HeaderPage(Read(0)).FreeCount;

    public override byte[] Read(uint page) =>
        pages.TryGetValue(page, out var bytes) ? bytes : throw new InvalidOperationException($"page {page} was never written");

    public override byte[] Modify(uint page)
    {
        if (!pages.TryGetValue(page, out var bytes))
        {
            bytes = new byte[Page.Size];
            pages[page] = bytes;
        }
        return bytes;
    }

    /// <summary>The tree's shape, and that every page is accounted for.</summary>
    public void Verify(BTree tree) => VerifyAccounting(tree.Verify());
}

internal sealed class ByteComparer : IComparer<byte[]>
{
    public static readonly ByteComparer Instance = new();

    public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y);
}
