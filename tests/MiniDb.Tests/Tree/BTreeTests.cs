using System.Buffers.Binary;

using MiniDb.Pages;
using MiniDb.Tree;

namespace MiniDb.Tests.Tree;

public class BTreeTests
{
    public enum KeyOrder
    {
        Random,
        Ascending,
        Descending,

        /// <summary>
        /// Random keys of 600 to 1,000 bytes: a handful per page, internal pages included, so
        /// the tree gets deep with few keys, and deleting them empties internal pages too.
        /// </summary>
        Long,
    }

    [Theory]
    [InlineData(KeyOrder.Random)]
    [InlineData(KeyOrder.Ascending)]
    [InlineData(KeyOrder.Descending)]
    [InlineData(KeyOrder.Long)]
    public void The_tree_answers_every_read_the_way_a_sorted_dictionary_does(KeyOrder order) =>
        Seeds.Each(30, seed =>
        {
            var random = new Random(seed);
            var pages = new MemoryPages();
            var tree = new BTree(pages);
            var model = new SortedDictionary<byte[], byte[]>(ByteComparer.Instance);
            var known = new List<byte[]>(); // the model's keys again, for picking one at random quickly
            var keys = new KeySource(order, random);

            void Delete(int index)
            {
                byte[] key = known[index];
                Assert.True(tree.Delete(key));
                model.Remove(key);
                known[index] = known[^1];
                known.RemoveAt(known.Count - 1);
            }

            for (int step = 0; step < 3000; step++)
            {
                double roll = random.NextDouble();
                if (step % 1000 == 999 && random.NextDouble() < 0.5)
                {
                    // Now and then most of the tree goes at once: pages empty out, parents lose
                    // children, and the root may hand over to a child.
                    for (int n = known.Count * 4 / 5; n > 0; n--)
                    {
                        Delete(random.Next(known.Count));
                    }
                    pages.Verify(tree);
                }
                else if (roll < 0.5)
                {
                    // A new key, or now and then a new value for one already there.
                    byte[] key = known.Count > 0 && random.NextDouble() < 0.2
                        ? known[random.Next(known.Count)]
                        : keys.Next();
                    byte[] value = RandomValue(random, key.Length);
                    tree.Put(key, value);
                    if (!model.ContainsKey(key))
                    {
                        known.Add(key);
                    }
                    model[key] = value;
                }
                else if (roll < 0.7)
                {
                    if (known.Count > 0 && random.NextDouble() < 0.9)
                    {
                        Delete(random.Next(known.Count));
                    }
                    else
                    {
                        byte[] key = keys.Next();
                        if (!model.ContainsKey(key))
                        {
                            Assert.False(tree.Delete(key));
                        }
                    }
                }
                else if (roll < 0.95)
                {
                    byte[] key = known.Count > 0 && random.NextDouble() < 0.7
                        ? known[random.Next(known.Count)]
                        : keys.Next();
                    Same.Bytes(model.GetValueOrDefault(key), tree.Get(key));
                }
                else
                {
                    var (from, to) = RandomRange(random, keys);
                    Same.Entries(ModelScan(model, from, to), tree.Scan(from, to).ToList());
                }

                if (step % 250 == 0)
                {
                    pages.Verify(tree);
                }
            }

            pages.Verify(tree);
            Same.Entries(model.ToList(), tree.Scan().ToList());
        });

    [Fact]
    public void An_empty_tree_finds_nothing_and_scans_to_nothing()
    {
        var tree = new BTree(new MemoryPages());
        Assert.Null(tree.Get("a"u8));
        Assert.Empty(tree.Scan());
        tree.Verify();
    }

    [Fact]
    public void A_new_value_that_no_longer_fits_its_page_splits_it_and_replaces_the_old_one()
    {
        var tree = new BTree(new MemoryPages());
        for (int i = 0; i < 30; i++)
        {
            tree.Put(Key(i), new byte[100]);
        }
        var big = Enumerable.Repeat((byte)7, 900).ToArray();
        tree.Put(Key(3), big);

        Assert.Equal(big, tree.Get(Key(3)));
        Assert.Equal(30, tree.Scan().Count());
        tree.Verify();
    }

    [Fact]
    public void The_tree_grows_several_levels_and_stays_balanced()
    {
        var pages = new MemoryPages();
        var tree = new BTree(pages);
        var random = new Random(8);
        // Values near the largest a page takes: few per page, so the tree gets tall quickly.
        for (int i = 0; i < 3000; i++)
        {
            tree.Put(Key(random.Next()), new byte[800]);
        }
        tree.Verify();
        Assert.True(Depth(pages) >= 3, $"depth {Depth(pages)}");
    }

    [Fact]
    public void Keys_in_increasing_order_fill_their_pages()
    {
        var pages = new MemoryPages();
        var tree = new BTree(pages);
        for (int i = 0; i < 20_000; i++)
        {
            tree.Put(Key(i), new byte[20]);
        }

        // Every leaf but the last is full: the append split leaves no half-empty pages behind.
        var fills = LeafFills(pages).SkipLast(1).ToList();
        Assert.True(fills.Min() > 0.95, $"least full leaf: {fills.Min():P1}");
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(BTree.MaxKeySize + 1, 10)]
    [InlineData(10, NodePage.MaxCellSize)]
    public void A_key_or_value_too_large_for_a_page_is_refused(int keySize, int valueSize)
    {
        var tree = new BTree(new MemoryPages());
        Assert.Throws<ArgumentException>(() => tree.Put(new byte[keySize], new byte[valueSize]));
    }

    [Fact]
    public void A_page_below_the_root_left_with_one_child_keeps_every_leaf_at_the_same_depth()
    {
        // Long keys: about four to a page, internal pages too, so the tree is several levels
        // deep, and deleting keys empties internal pages as well as leaves.
        var pages = new MemoryPages();
        var tree = new BTree(pages);
        var random = new Random(11);
        var keys = new List<byte[]>();
        for (int i = 0; i < 1500; i++)
        {
            var key = new byte[random.Next(800, BTree.MaxKeySize + 1)];
            random.NextBytes(key);
            keys.Add(key);
            tree.Put(key, []);
        }
        Assert.True(Depth(pages) >= 4, $"depth {Depth(pages)}");

        int seen = 0;
        foreach (var key in keys.OrderBy(_ => random.Next()))
        {
            Assert.True(tree.Delete(key));
            pages.Verify(tree);
            seen += SingleChildPagesBelowTheRoot(pages);
        }

        // The case this test is about has to have happened, or the test proves nothing.
        Assert.True(seen > 0, "no internal page below the root was ever left with a single child");
        Assert.Empty(tree.Scan());
    }

    [Fact]
    public void Deleting_every_key_leaves_an_empty_root_and_every_other_page_free()
    {
        var pages = new MemoryPages();
        var tree = new BTree(pages);
        var random = new Random(10);
        var keys = Enumerable.Range(0, 5000).Select(_ => Key(random.Next())).Distinct().ToList();
        foreach (var key in keys)
        {
            tree.Put(key, new byte[random.Next(0, 300)]);
        }

        foreach (var key in keys.OrderBy(_ => random.Next()))
        {
            Assert.True(tree.Delete(key));
        }

        pages.Verify(tree);
        Assert.Empty(tree.Scan());
        Assert.Equal(PageType.Leaf, Page.TypeOf(pages.Read(pages.Root)));
        // Everything but the header and the root is on the free list.
        Assert.Equal(pages.PageCount - 2, pages.FreeCount);
    }

    [Fact]
    public void Freed_pages_are_used_again_before_the_file_grows()
    {
        var pages = new MemoryPages();
        var tree = new BTree(pages);
        for (int i = 0; i < 3000; i++)
        {
            tree.Put(Key(i), new byte[100]);
        }
        uint grown = pages.PageCount;
        for (int i = 0; i < 3000; i++)
        {
            tree.Delete(Key(i));
        }
        for (int i = 0; i < 3000; i++)
        {
            tree.Put(Key(i), new byte[100]);
        }

        pages.Verify(tree);
        Assert.Equal(grown, pages.PageCount);
    }

    [Fact]
    public void Deleting_a_key_that_is_not_there_changes_nothing()
    {
        var pages = new MemoryPages();
        var tree = new BTree(pages);
        for (int i = 0; i < 1000; i += 2)
        {
            tree.Put(Key(i), new byte[50]);
        }
        var before = Snapshot(pages);

        Assert.False(tree.Delete(Key(501)));
        Assert.False(tree.Delete(Key(5000)));

        Assert.Equal(before, Snapshot(pages));
    }

    [Fact]
    public void The_largest_key_and_value_a_page_takes_are_accepted()
    {
        var tree = new BTree(new MemoryPages());
        var random = new Random(9);
        for (int i = 0; i < 200; i++)
        {
            var key = new byte[BTree.MaxKeySize];
            random.NextBytes(key);
            // What is left of the largest cell after the key and the two lengths.
            var value = new byte[NodePage.MaxCellSize - BTree.MaxKeySize - 3];
            tree.Put(key, value);
        }
        tree.Verify();
        Assert.Equal(200, tree.Scan().Count());
    }

    private static int SingleChildPagesBelowTheRoot(MemoryPages pages)
    {
        int count = 0;
        var pending = new Stack<(uint Page, bool Root)>();
        pending.Push((pages.Root, true));
        while (pending.Count > 0)
        {
            var (id, root) = pending.Pop();
            var node = new NodePage(pages.Read(id));
            if (node.Type != PageType.Internal)
            {
                continue;
            }
            if (node.Count == 0 && !root)
            {
                count++;
            }
            for (int i = 0; i < node.Count; i++)
            {
                pending.Push((InternalCell.Child(node.Cell(i)), false));
            }
            pending.Push((node.Right, false));
        }
        return count;
    }

    private static string Snapshot(MemoryPages pages) =>
        string.Join(",", Enumerable.Range(0, (int)pages.PageCount).Select(i => Convert.ToHexString(pages.Read((uint)i))));

    private static byte[] Key(int n)
    {
        var key = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(key, n);
        return key;
    }

    private static byte[] RandomValue(Random random, int keyLength)
    {
        // Mostly small, now and then large enough to force splits early; never more than a page
        // takes alongside this key.
        int room = NodePage.MaxCellSize - keyLength;
        while (LeafCell.SizeOf(keyLength, room) > NodePage.MaxCellSize)
        {
            room--;
        }
        int length = random.NextDouble() < 0.05 ? random.Next(room / 2, room + 1) : random.Next(0, Math.Min(60, room + 1));
        var value = new byte[length];
        random.NextBytes(value);
        return value;
    }

    private static (byte[]? From, byte[]? To) RandomRange(Random random, KeySource keys)
    {
        byte[]? from = random.NextDouble() < 0.2 ? null : keys.Next();
        byte[]? to = random.NextDouble() < 0.2 ? null : keys.Next();
        if (from is not null && to is not null && ByteComparer.Instance.Compare(from, to) > 0)
        {
            (from, to) = (to, from);
        }
        return (from, to);
    }

    private static List<KeyValuePair<byte[], byte[]>> ModelScan(
        SortedDictionary<byte[], byte[]> model, byte[]? from, byte[]? to) =>
        model.Where(entry =>
            (from is null || ByteComparer.Instance.Compare(entry.Key, from) >= 0)
            && (to is null || ByteComparer.Instance.Compare(entry.Key, to) < 0)).ToList();

    private static int Depth(MemoryPages pages)
    {
        int depth = 1;
        uint id = pages.Root;
        while (Page.TypeOf(pages.Read(id)) == PageType.Internal)
        {
            id = InternalCell.Child(new NodePage(pages.Read(id)).Cell(0));
            depth++;
        }
        return depth;
    }

    private static IEnumerable<double> LeafFills(MemoryPages pages)
    {
        uint id = pages.Root;
        while (Page.TypeOf(pages.Read(id)) == PageType.Internal)
        {
            id = InternalCell.Child(new NodePage(pages.Read(id)).Cell(0));
        }
        var fills = new List<double>();
        while (id != Page.None)
        {
            var leaf = new NodePage(pages.Read(id));
            fills.Add(1 - (double)leaf.FreeSpace / (Page.Size - NodePage.HeaderSize));
            id = leaf.Right;
        }
        return fills;
    }

    /// <summary>Keys in the order a test asks for: random bytes, or a counter going up or down.</summary>
    private sealed class KeySource(KeyOrder order, Random random)
    {
        private int counter = order == KeyOrder.Descending ? int.MaxValue : 0;

        public byte[] Next()
        {
            if (order == KeyOrder.Long)
            {
                var key = new byte[random.Next(600, BTree.MaxKeySize + 1)];
                random.NextBytes(key);
                return key;
            }
            if (order == KeyOrder.Random)
            {
                var key = new byte[random.NextDouble() < 0.1 ? random.Next(17, 200) : random.Next(1, 17)];
                random.NextBytes(key);
                return key;
            }
            counter += order == KeyOrder.Ascending ? 1 : -1;
            return Key(counter);
        }
    }
}
