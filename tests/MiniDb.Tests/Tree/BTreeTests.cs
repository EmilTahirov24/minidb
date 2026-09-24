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
    }

    [Theory]
    [InlineData(KeyOrder.Random)]
    [InlineData(KeyOrder.Ascending)]
    [InlineData(KeyOrder.Descending)]
    public void The_tree_answers_every_read_the_way_a_sorted_dictionary_does(KeyOrder order) =>
        Seeds.Each(30, seed =>
        {
            var random = new Random(seed);
            var tree = new BTree(new MemoryPages());
            var model = new SortedDictionary<byte[], byte[]>(ByteComparer.Instance);
            var known = new List<byte[]>(); // the model's keys again, for picking one at random quickly
            var keys = new KeySource(order, random);

            for (int step = 0; step < 3000; step++)
            {
                double roll = random.NextDouble();
                if (roll < 0.65)
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
                    tree.Verify();
                }
            }

            tree.Verify();
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

    private static byte[] Key(int n)
    {
        var key = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(key, n);
        return key;
    }

    private static byte[] RandomValue(Random random, int keyLength)
    {
        // Mostly small, now and then large enough to force splits early.
        int room = NodePage.MaxCellSize - keyLength - 4;
        var value = new byte[random.NextDouble() < 0.05 ? random.Next(room / 2, room) : random.Next(0, 60)];
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
