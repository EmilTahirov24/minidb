using MiniDb.Pages;

namespace MiniDb.Tests.Pages;

public class NodePageTests
{
    [Fact]
    public void Cells_stay_in_slot_order_and_free_space_adds_up_through_any_inserts_and_removes() =>
        Seeds.Each(300, seed =>
        {
            var random = new Random(seed);
            var bytes = new byte[Page.Size];
            var page = NodePage.Format(bytes, PageType.Leaf);
            var model = new List<byte[]>();

            for (int step = 0; step < 400; step++)
            {
                if (model.Count == 0 || random.NextDouble() < 0.6)
                {
                    var cell = RandomLeafCell(random);
                    int index = random.Next(model.Count + 1);
                    bool fits = page.FreeSpace >= cell.Length + NodePage.SlotSize;
                    Assert.Equal(fits, page.TryInsert(index, cell));
                    if (fits)
                    {
                        model.Insert(index, cell);
                    }
                }
                else
                {
                    int index = random.Next(model.Count);
                    page.Remove(index);
                    model.RemoveAt(index);
                }

                Assert.Equal(model.Count, page.Count);
                for (int i = 0; i < model.Count; i++)
                {
                    Assert.True(page.Cell(i).SequenceEqual(model[i]), $"step {step}: cell {i} differs");
                }
                int used = NodePage.HeaderSize + model.Sum(cell => cell.Length + NodePage.SlotSize);
                Assert.Equal(Page.Size - used, page.FreeSpace);
            }
        });

    [Fact]
    public void A_page_that_cannot_take_a_cell_is_left_exactly_as_it_was()
    {
        var random = new Random(6);
        var bytes = new byte[Page.Size];
        var page = NodePage.Format(bytes, PageType.Leaf);
        while (page.TryInsert(page.Count, RandomLeafCell(random)))
        {
        }

        var before = bytes.ToArray();
        // A cell about as large as all the free space: with its slot, it cannot fit.
        Assert.False(page.TryInsert(0, LeafCellOfSize(page.FreeSpace)));
        Assert.Equal(before, bytes);
    }

    [Fact]
    public void Compaction_keeps_every_cell_and_joins_the_holes_into_one_free_run()
    {
        var random = new Random(7);
        var bytes = new byte[Page.Size];
        var page = NodePage.Format(bytes, PageType.Leaf);
        while (page.TryInsert(page.Count, RandomLeafCell(random)))
        {
        }
        for (int i = page.Count - 2; i >= 0; i -= 2)
        {
            page.Remove(i);
        }
        var cells = CellsOf(page);
        int free = page.FreeSpace;

        page.Compact();

        Assert.Equal(free, page.FreeSpace);
        Assert.Equal(cells, CellsOf(page));
        // All of the free space is usable by one cell now (up to the size limit on a cell).
        int size = Math.Min(free - NodePage.SlotSize, NodePage.MaxCellSize);
        Assert.True(page.TryInsert(0, LeafCellOfSize(size)));
    }

    [Fact]
    public void A_cell_over_a_quarter_of_the_page_is_refused()
    {
        var cell = new byte[NodePage.MaxCellSize + 1];
        Assert.Throws<ArgumentOutOfRangeException>(
            () => NodePage.Format(new byte[Page.Size], PageType.Leaf).TryInsert(0, cell));
    }

    [Fact]
    public void An_internal_cell_keeps_its_child_and_key_and_the_child_can_be_repointed()
    {
        var bytes = new byte[Page.Size];
        var page = NodePage.Format(bytes, PageType.Internal);
        var cell = new byte[InternalCell.SizeOf(3)];
        InternalCell.Write(cell, child: 42, "abc"u8);
        Assert.True(page.TryInsert(0, cell));
        page.Right = 77;

        Assert.Equal(42u, InternalCell.Child(page.Cell(0)));
        Assert.True(page.KeyAt(0).SequenceEqual("abc"u8));
        page.SetChild(0, 43);
        Assert.Equal(43u, InternalCell.Child(page.Cell(0)));
        Assert.Equal(77u, page.Right);
    }

    [Fact]
    public void A_leaf_cell_gives_back_its_key_and_value()
    {
        var cell = LeafCellOf("key"u8.ToArray(), new byte[300]);
        Assert.Equal(cell.Length, LeafCell.SizeAt(cell));
        Assert.True(LeafCell.Key(cell).SequenceEqual("key"u8));
        Assert.Equal(300, LeafCell.Value(cell).Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(16_383)]
    [InlineData(16_384)]
    [InlineData(int.MaxValue)]
    public void A_length_reads_back_as_written_in_as_few_bytes_as_it_needs(int value)
    {
        var buffer = new byte[5];
        int written = Varint.Write(buffer, value);
        Assert.Equal(Varint.SizeOf(value), written);
        Assert.Equal(written, Varint.Read(buffer, out int read));
        Assert.Equal(value, read);
    }

    private static byte[] RandomLeafCell(Random random)
    {
        var key = new byte[random.Next(1, 40)];
        // Mostly small values, now and then one close to the largest a page takes.
        var value = new byte[random.NextDouble() < 0.1 ? random.Next(500, 950) : random.Next(0, 200)];
        random.NextBytes(key);
        random.NextBytes(value);
        return LeafCellOf(key, value);
    }

    // A page lives on the stack, so it cannot be captured by a lambda; a loop copies its cells out.
    private static List<byte[]> CellsOf(NodePage page)
    {
        var cells = new List<byte[]>(page.Count);
        for (int i = 0; i < page.Count; i++)
        {
            cells.Add(page.Cell(i).ToArray());
        }
        return cells;
    }

    /// <summary>A leaf cell of <paramref name="size"/> bytes, or one fewer where a length's own size gets in the way.</summary>
    private static byte[] LeafCellOfSize(int size)
    {
        int value = size - 3;
        while (LeafCell.SizeOf(1, value) > size)
        {
            value--;
        }
        return LeafCellOf(new byte[1], new byte[value]);
    }

    private static byte[] LeafCellOf(byte[] key, byte[] value)
    {
        var cell = new byte[LeafCell.SizeOf(key.Length, value.Length)];
        LeafCell.Write(cell, key, value);
        return cell;
    }
}
