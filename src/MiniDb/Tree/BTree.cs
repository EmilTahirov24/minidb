using MiniDb.Pages;

namespace MiniDb.Tree;

/// <summary>
/// Sorted keys and their values in a B+tree of pages. Every key and value lives in a leaf;
/// internal pages only route. Leaves are linked both ways, so a range scan walks sideways.
/// </summary>
internal sealed class BTree(IPages pages)
{
    /// <summary>
    /// The longest key. A key also has to fit in an internal page as a separator, where it
    /// shares a cell with a child pointer, so it gets a little less room than key and value do.
    /// </summary>
    public const int MaxKeySize = 1000;

    /// <summary>
    /// Deeper than any real tree can be: even with only two children per page, 2^32 pages fit
    /// in 32 levels. A path longer than this goes round in a circle through damaged pages.
    /// </summary>
    private const int MaxDepth = 64;

    public byte[]? Get(ReadOnlySpan<byte> key)
    {
        var leaf = new NodePage(pages.Read(FindLeaf(key, path: null)));
        int index = leaf.LowerBound(key, out bool found);
        return found ? LeafCell.Value(leaf.Cell(index)).ToArray() : null;
    }

    /// <summary>Store <paramref name="value"/> under <paramref name="key"/>, replacing any value there was.</summary>
    public void Put(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        CheckSize(key, value);
        var path = new List<(uint Page, int Slot)>();
        uint id = FindLeaf(key, path);

        var leaf = new NodePage(pages.Modify(id));
        int index = leaf.LowerBound(key, out bool found);
        if (found)
        {
            leaf.Remove(index);
        }
        var cell = new byte[LeafCell.SizeOf(key.Length, value.Length)];
        LeafCell.Write(cell, key, value);
        if (!leaf.TryInsert(index, cell))
        {
            SplitLeaf(id, index, cell, path);
        }
    }

    /// <summary>Remove <paramref name="key"/> and its value; false if it was not there.</summary>
    public bool Delete(ReadOnlySpan<byte> key)
    {
        var path = new List<(uint Page, int Slot)>();
        uint id = FindLeaf(key, path);
        int index = new NodePage(pages.Read(id)).LowerBound(key, out bool found);
        if (!found)
        {
            return false;
        }

        var leaf = new NodePage(pages.Modify(id));
        leaf.Remove(index);
        if (leaf.Count > 0 || path.Count == 0)
        {
            // A leaf keeps its page however empty it gets, until it is completely empty; a root
            // leaf keeps it even then, as the empty tree.
            return true;
        }

        if (leaf.Left != Page.None)
        {
            new NodePage(pages.Modify(leaf.Left)).Right = leaf.Right;
        }
        if (leaf.Right != Page.None)
        {
            new NodePage(pages.Modify(leaf.Right)).Left = leaf.Left;
        }
        pages.Free(id);
        RemoveChild(path);
        return true;
    }

    /// <summary>
    /// Every key from <paramref name="from"/> up to, not including, <paramref name="to"/>, in
    /// order; null means no bound. The tree must not change while the scan is under way.
    /// </summary>
    public IEnumerable<KeyValuePair<byte[], byte[]>> Scan(byte[]? from = null, byte[]? to = null)
    {
        uint id = FindLeaf(from ?? [], path: null);
        int index = from is null ? 0 : new NodePage(pages.Read(id)).LowerBound(from, out _);
        byte[]? previous = null;
        while (id != Page.None)
        {
            var (entry, right) = EntryAt(id, index);
            if (entry is null)
            {
                id = right;
                index = 0;
                continue;
            }
            byte[] key = entry.Value.Key;
            // Keys only ever go up along the leaves. If they do not, the links are damaged, and
            // following them further could go round in a circle for ever.
            if (previous is not null && previous.AsSpan().SequenceCompareTo(key) >= 0)
            {
                throw Broken(id, "breaks the order of keys along the leaves: the tree is damaged");
            }
            if (to is not null && key.AsSpan().SequenceCompareTo(to) >= 0)
            {
                yield break;
            }
            yield return entry.Value;
            previous = key;
            index++;
        }
    }

    /// <summary>
    /// Check every invariant of the tree's shape, throwing <see cref="InvalidDataException"/>
    /// with what is wrong and where, and return the pages the tree uses. Reads every page; for
    /// tests and for diagnosing damage.
    /// </summary>
    public IReadOnlySet<uint> Verify()
    {
        var leaves = new List<uint>();
        var seen = new HashSet<uint>();
        int leafDepth = -1;
        Check(pages.Root, lower: null, upper: null, depth: 0);

        for (int i = 0; i < leaves.Count; i++)
        {
            var leaf = new NodePage(pages.Read(leaves[i]));
            uint left = i == 0 ? Page.None : leaves[i - 1];
            uint right = i == leaves.Count - 1 ? Page.None : leaves[i + 1];
            if (leaf.Left != left || leaf.Right != right)
            {
                throw Broken(leaves[i], $"is linked to {leaf.Left} and {leaf.Right}, not {left} and {right}");
            }
        }
        return seen;

        void Check(uint id, byte[]? lower, byte[]? upper, int depth)
        {
            if (id == Page.None || !seen.Add(id))
            {
                throw Broken(id, "is reached twice, or is not a page");
            }
            var node = new NodePage(pages.Read(id));
            byte[]? previous = null;
            for (int i = 0; i < node.Count; i++)
            {
                byte[] key = node.KeyAt(i).ToArray();
                if (previous is not null && previous.AsSpan().SequenceCompareTo(key) >= 0)
                {
                    throw Broken(id, $"has keys out of order at slot {i}");
                }
                if ((lower is not null && key.AsSpan().SequenceCompareTo(lower) < 0)
                    || (upper is not null && key.AsSpan().SequenceCompareTo(upper) >= 0))
                {
                    throw Broken(id, $"has a key outside the range its parent gives it, at slot {i}");
                }
                previous = key;
            }

            if (node.Type == PageType.Leaf)
            {
                if (node.Count == 0 && depth > 0)
                {
                    throw Broken(id, "is an empty leaf that is not the root");
                }
                if (leafDepth >= 0 && leafDepth != depth)
                {
                    throw Broken(id, $"is a leaf at depth {depth}; others are at {leafDepth}");
                }
                leafDepth = depth;
                leaves.Add(id);
                return;
            }
            if (node.Type != PageType.Internal)
            {
                throw Broken(id, $"is a {node.Type} page inside the tree");
            }
            if (node.Count == 0 && depth == 0)
            {
                // Below the root a single child is allowed; the root would be one level too many.
                throw Broken(id, "is an internal root with a single child");
            }

            var children = new List<(uint Child, byte[] Key)>();
            for (int i = 0; i < node.Count; i++)
            {
                children.Add((InternalCell.Child(node.Cell(i)), node.KeyAt(i).ToArray()));
            }
            uint rightmost = node.Right;

            byte[]? from = lower;
            foreach (var (child, key) in children)
            {
                Check(child, from, key, depth + 1);
                from = key;
            }
            Check(rightmost, from, upper, depth + 1);
        }
    }

    private static InvalidDataException Broken(uint page, string what) => new($"page {page} {what}");

    /// <summary>
    /// The page the last step of <paramref name="path"/> led to is gone: take its entry out of
    /// the parent, freeing the parent in turn if that was its last child.
    /// </summary>
    private void RemoveChild(List<(uint Page, int Slot)> path)
    {
        var (parentId, slot) = path[^1];
        path.RemoveAt(path.Count - 1);
        var parent = new NodePage(pages.Modify(parentId));

        if (slot < parent.Count)
        {
            // The child and its separator go; its keys, if any arrive, route to the next child.
            parent.Remove(slot);
        }
        else if (parent.Count > 0)
        {
            // The rightmost child went: the last cell's child takes its place.
            parent.Right = InternalCell.Child(parent.Cell(parent.Count - 1));
            parent.Remove(parent.Count - 1);
        }
        else
        {
            // That was the only child. Below the root, an internal page may route everything
            // to a single child - replacing it by the child would make one branch shorter than
            // the rest - but one with no children at all goes too.
            pages.Free(parentId);
            RemoveChild(path);
            return;
        }

        if (path.Count == 0)
        {
            ShortenFromTheRoot();
        }
    }

    /// <summary>
    /// While the root is an internal page with a single child, that child becomes the root. This
    /// shortens every path from the root at once, so every leaf stays at the same depth.
    /// </summary>
    private void ShortenFromTheRoot()
    {
        while (true)
        {
            uint root = pages.Root;
            var node = new NodePage(pages.Read(root));
            if (node.Type != PageType.Internal || node.Count > 0)
            {
                return;
            }
            pages.Root = node.Right;
            pages.Free(root);
        }
    }

    private static void CheckSize(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        if (key.IsEmpty || key.Length > MaxKeySize)
        {
            throw new ArgumentException($"a key is 1 to {MaxKeySize} bytes, not {key.Length}", nameof(key));
        }
        int size = LeafCell.SizeOf(key.Length, value.Length);
        if (size > NodePage.MaxCellSize)
        {
            throw new ArgumentException(
                $"key and value take {size} bytes stored; a page takes at most {NodePage.MaxCellSize}",
                nameof(value));
        }
    }

    /// <summary>The leaf where <paramref name="key"/> is or would be, noting the way down in <paramref name="path"/>.</summary>
    private uint FindLeaf(ReadOnlySpan<byte> key, List<(uint Page, int Slot)>? path)
    {
        uint id = pages.Root;
        for (int depth = 0; ; depth++)
        {
            var node = new NodePage(pages.Read(id));
            if (node.Type == PageType.Leaf)
            {
                return id;
            }
            if (node.Type != PageType.Internal || depth > MaxDepth)
            {
                throw Broken(id, $"is a {node.Type} page {depth} levels down: the tree is damaged");
            }
            int slot = node.UpperBound(key);
            path?.Add((id, slot));
            id = slot == node.Count ? node.Right : InternalCell.Child(node.Cell(slot));
        }
    }

    private (KeyValuePair<byte[], byte[]>? Entry, uint Right) EntryAt(uint id, int index)
    {
        var leaf = new NodePage(pages.Read(id));
        if (index >= leaf.Count)
        {
            return (null, leaf.Right);
        }
        var cell = leaf.Cell(index);
        return (new(LeafCell.Key(cell).ToArray(), LeafCell.Value(cell).ToArray()), leaf.Right);
    }

    /// <summary>The leaf is full: move part of it to a new leaf, and put a separator in the parent.</summary>
    private void SplitLeaf(uint id, int index, byte[] cell, List<(uint Page, int Slot)> path)
    {
        byte[] bytes = pages.Modify(id);
        var cells = CellsWith(new NodePage(bytes), index, cell);
        var (oldLeft, oldRight, count) = Links(bytes);

        // Keys arriving in increasing order always land past the end of the last leaf. Splitting
        // that leaf in half would leave every page half empty forever, so the new key moves
        // alone and the full page stays full.
        bool appending = oldRight == Page.None && index == count;
        int split = appending ? cells.Count - 1 : SplitPoint(cells);

        uint rightId = pages.Allocate();
        var left = Refill(bytes, PageType.Leaf, cells[..split]);
        left.Left = oldLeft;
        left.Right = rightId;
        var right = Refill(pages.Modify(rightId), PageType.Leaf, cells[split..]);
        right.Left = id;
        right.Right = oldRight;
        if (oldRight != Page.None)
        {
            new NodePage(pages.Modify(oldRight)).Left = rightId;
        }

        InsertSeparator(path, id, LeafCell.Key(cells[split]).ToArray(), rightId);
    }

    /// <summary>
    /// <paramref name="leftId"/> has split: it keeps the keys below <paramref name="separator"/>
    /// and <paramref name="rightId"/> has the rest. Record that in the parent, splitting it in
    /// turn if it is full, or grow a new root if there is no parent.
    /// </summary>
    private void InsertSeparator(List<(uint Page, int Slot)> path, uint leftId, byte[] separator, uint rightId)
    {
        var cell = new byte[InternalCell.SizeOf(separator.Length)];
        InternalCell.Write(cell, leftId, separator);

        if (path.Count == 0)
        {
            uint rootId = pages.Allocate();
            var root = NodePage.Format(pages.Modify(rootId), PageType.Internal);
            root.TryInsert(0, cell);
            root.Right = rightId;
            pages.Root = rootId;
            return;
        }

        var (parentId, slot) = path[^1];
        path.RemoveAt(path.Count - 1);
        byte[] bytes = pages.Modify(parentId);
        var parent = new NodePage(bytes);

        // What pointed at the page that split now points at its right half, and the left half
        // goes in just before it, with the separator as its upper bound.
        if (slot == parent.Count)
        {
            parent.Right = rightId;
        }
        else
        {
            parent.SetChild(slot, rightId);
        }
        if (!parent.TryInsert(slot, cell))
        {
            SplitInternal(parentId, slot, cell, path);
        }
    }

    /// <summary>A full internal page splits around its middle cell, whose key moves up.</summary>
    private void SplitInternal(uint id, int index, byte[] cell, List<(uint Page, int Slot)> path)
    {
        byte[] bytes = pages.Modify(id);
        var cells = CellsWith(new NodePage(bytes), index, cell);
        uint rightmost = new NodePage(bytes).Right;

        // The middle cell's child becomes the left half's rightmost child, and its key the
        // separator between the halves; each half keeps at least one cell.
        int middle = Math.Clamp(SplitPoint(cells), 1, cells.Count - 2);
        byte[] up = InternalCell.Key(cells[middle]).ToArray();
        uint middleChild = InternalCell.Child(cells[middle]);

        uint rightId = pages.Allocate();
        var left = Refill(bytes, PageType.Internal, cells[..middle]);
        left.Right = middleChild;
        var right = Refill(pages.Modify(rightId), PageType.Internal, cells[(middle + 1)..]);
        right.Right = rightmost;

        InsertSeparator(path, id, up, rightId);
    }

    private static (uint Left, uint Right, int Count) Links(byte[] bytes)
    {
        var node = new NodePage(bytes);
        return (node.Left, node.Right, node.Count);
    }

    /// <summary>The page's cells, copied out, with <paramref name="cell"/> inserted at <paramref name="index"/>.</summary>
    private static List<byte[]> CellsWith(NodePage node, int index, byte[] cell)
    {
        var cells = new List<byte[]>(node.Count + 1);
        for (int i = 0; i < node.Count; i++)
        {
            cells.Add(node.Cell(i).ToArray());
        }
        cells.Insert(index, cell);
        return cells;
    }

    /// <summary>Where to divide the cells so that each side holds about half of the bytes.</summary>
    private static int SplitPoint(List<byte[]> cells)
    {
        int total = cells.Sum(c => c.Length + NodePage.SlotSize);
        int sum = 0;
        for (int i = 0; i < cells.Count; i++)
        {
            sum += cells[i].Length + NodePage.SlotSize;
            if (sum * 2 >= total)
            {
                return Math.Clamp(i + 1, 1, cells.Count - 1);
            }
        }
        return cells.Count - 1;
    }

    private static NodePage Refill(byte[] bytes, PageType type, List<byte[]> cells)
    {
        var node = NodePage.Format(bytes, type);
        foreach (var cell in cells)
        {
            if (!node.TryInsert(node.Count, cell))
            {
                throw new InvalidOperationException("half of a split page did not fit in a page");
            }
        }
        return node;
    }
}
