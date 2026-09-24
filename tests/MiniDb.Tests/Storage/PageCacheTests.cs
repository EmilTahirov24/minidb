using MiniDb.Pages;
using MiniDb.Storage;
using MiniDb.Tests.Simulation;

namespace MiniDb.Tests.Storage;

public class PageCacheTests
{
    [Fact]
    public void A_page_is_read_from_the_file_once_and_then_from_memory()
    {
        var (_, file) = FileWithPages(3);
        var cache = new PageCache(file, capacity: 8);

        var first = cache.Get(2);
        var second = cache.Get(2);

        Assert.Same(first, second);
        Assert.Equal((1, 1), (cache.Misses, cache.Hits));
        Assert.Equal(2, first[100]);
    }

    [Fact]
    public void The_cache_never_holds_more_pages_than_it_has_room_for()
    {
        var (_, file) = FileWithPages(10);
        var cache = new PageCache(file, capacity: 4);
        for (uint id = 0; id < 10; id++)
        {
            cache.Get(id);
            Assert.True(cache.Count <= 4);
        }
        Assert.Equal(6, cache.Evictions);
    }

    [Fact]
    public void A_page_used_since_the_clock_last_passed_gets_a_second_chance()
    {
        var (_, file) = FileWithPages(5);
        var cache = new PageCache(file, capacity: 3);
        cache.Get(0);
        cache.Get(1);
        cache.Get(2);
        cache.Get(3);   // every page had been used: one full turn of the clock, and page 0 goes
        cache.Get(1);   // page 1 is used again; page 2 is not
        cache.Get(4);   // so page 2 goes, not page 1

        long misses = cache.Misses;
        cache.Get(1);
        Assert.Equal(misses, cache.Misses);
        cache.Get(2);
        Assert.Equal(misses + 1, cache.Misses);
    }

    [Fact]
    public void A_changed_page_reaches_the_file_when_it_is_evicted()
    {
        var (disk, file) = FileWithPages(4);
        var cache = new PageCache(file, capacity: 2);
        var changed = PageFilledWith(99);
        cache.Put(1, changed);

        cache.Get(2);
        cache.Get(3);
        cache.Get(0);

        Assert.Equal(1, cache.WrittenOnEviction);
        Assert.Equal(changed, disk.Contents("db").AsSpan(Page.Size, Page.Size).ToArray());
        Assert.Equal(99, cache.Get(1)[100]);
    }

    [Fact]
    public void Writing_back_puts_every_changed_page_in_the_file_and_leaves_them_clean()
    {
        var (disk, file) = FileWithPages(4);
        var cache = new PageCache(file, capacity: 8);
        cache.Put(1, PageFilledWith(51));
        cache.Put(3, PageFilledWith(53));
        Assert.Equal(2, cache.DirtyCount);

        cache.WriteBack();

        Assert.Equal(0, cache.DirtyCount);
        Assert.Equal(51, disk.Contents("db")[Page.Size + 100]);
        Assert.Equal(53, disk.Contents("db")[3 * Page.Size + 100]);
    }

    [Fact]
    public void Bytes_handed_out_are_never_reused_for_another_page()
    {
        var (_, file) = FileWithPages(6);
        var cache = new PageCache(file, capacity: 2);
        var page1 = cache.Get(1);
        var copy = page1.ToArray();

        for (uint id = 2; id < 6; id++)
        {
            cache.Get(id);
        }

        Assert.Equal(copy, page1);
    }

    [Fact]
    public void A_damaged_page_in_the_file_is_refused()
    {
        var (disk, file) = FileWithPages(3);
        var damaged = PageFilledWith(2);
        damaged[500] ^= 1;
        file.Write(2 * Page.Size, damaged);

        var cache = new PageCache(file, capacity: 4);
        var error = Assert.Throws<InvalidDataException>(() => cache.Get(2));
        Assert.Contains("page 2", error.Message);
        Assert.NotNull(disk);
    }

    private static (SimulatedDisk Disk, MiniDb.Storage.IStorageFile File) FileWithPages(int count)
    {
        var disk = new SimulatedDisk();
        var file = disk.Open("db");
        for (int id = 0; id < count; id++)
        {
            file.Write((long)id * Page.Size, PageFilledWith((byte)id));
        }
        return (disk, file);
    }

    /// <summary>A sealed page whose bytes after the header are all <paramref name="value"/>.</summary>
    private static byte[] PageFilledWith(byte value)
    {
        var page = new byte[Page.Size];
        page.AsSpan(20).Fill(value);
        page[4] = (byte)PageType.Leaf;
        Page.Seal(page);
        return page;
    }
}
