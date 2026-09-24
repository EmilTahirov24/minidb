using MiniDb.Pages;
using MiniDb.Tree;

namespace MiniDb.Tests.Tree;

/// <summary>Pages in a dictionary, for testing the tree on its own: no cache, no log, no disk.</summary>
internal sealed class MemoryPages : IPages
{
    private readonly Dictionary<uint, byte[]> pages = [];
    private uint next = 2;

    public MemoryPages()
    {
        var root = new byte[Page.Size];
        NodePage.Format(root, PageType.Leaf);
        pages[1] = root;
        Root = 1;
    }

    public uint Root { get; set; }

    public IEnumerable<uint> Ids => pages.Keys;

    public byte[] Read(uint page) => pages[page];

    public byte[] Modify(uint page) => pages[page];

    public uint Allocate()
    {
        uint id = next++;
        pages[id] = new byte[Page.Size];
        return id;
    }

    public void Free(uint page) => pages.Remove(page);
}

internal sealed class ByteComparer : IComparer<byte[]>
{
    public static readonly ByteComparer Instance = new();

    public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y);
}
