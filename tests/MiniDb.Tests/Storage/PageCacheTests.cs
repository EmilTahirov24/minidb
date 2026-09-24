using MiniDb.Pages;
using MiniDb.Storage;
using MiniDb.Tests.Simulation;

namespace MiniDb.Tests.Storage;

public class PageCacheTests
{
    private const long Latest = PageCache.Latest;

    [Fact]
    public void A_page_is_read_from_the_file_once_and_then_from_memory()
    {
        var (_, file) = FileWithPages(3);
        var cache = new PageCache(file, capacity: 8);

        var first = cache.Get(2, Latest);
        var second = cache.Get(2, Latest);

        Assert.Same(first, second);
        Assert.Equal((1, 1), (cache.Misses, cache.Hits));
        Assert.Equal(2, first[100]);
    }

    [Fact]
    public void With_no_transaction_open_the_cache_never_holds_more_pages_than_it_has_room_for()
    {
        var (_, file) = FileWithPages(10);
        var cache = new PageCache(file, capacity: 4);
        for (uint id = 0; id < 10; id++)
        {
            cache.Get(id, Latest);
            Assert.True(cache.Count <= 4);
        }
        Assert.Equal(6, cache.Evictions);
    }

    [Fact]
    public void A_page_used_since_the_clock_last_passed_gets_a_second_chance()
    {
        var (_, file) = FileWithPages(5);
        var cache = new PageCache(file, capacity: 3);
        cache.Get(0, Latest);
        cache.Get(1, Latest);
        cache.Get(2, Latest);
        cache.Get(3, Latest);   // every page had been used: one full turn of the clock, and page 0 goes
        cache.Get(1, Latest);   // page 1 is used again; page 2 is not
        cache.Get(4, Latest);   // so page 2 goes, not page 1

        long misses = cache.Misses;
        cache.Get(1, Latest);
        Assert.Equal(misses, cache.Misses);
        cache.Get(2, Latest);
        Assert.Equal(misses + 1, cache.Misses);
    }

    [Fact]
    public void A_changed_page_reaches_the_file_when_it_is_evicted()
    {
        var (disk, file) = FileWithPages(4);
        var cache = new PageCache(file, capacity: 2);
        var changed = PageFilledWith(99);
        Publish(cache, 1, changed);

        cache.Get(2, Latest);
        cache.Get(3, Latest);
        cache.Get(0, Latest);

        Assert.Equal(1, cache.WrittenOnEviction);
        Assert.Equal(changed, disk.Contents("db").AsSpan(Page.Size, Page.Size).ToArray());
        Assert.Equal(99, cache.Get(1, Latest)[100]);
    }

    [Fact]
    public void Writing_back_puts_every_changed_page_in_the_file_and_leaves_them_clean()
    {
        var (disk, file) = FileWithPages(4);
        var cache = new PageCache(file, capacity: 8);
        Publish(cache, 1, PageFilledWith(51));
        Publish(cache, 3, PageFilledWith(53));
        Assert.Equal(2, cache.DirtyCount);

        cache.WriteBack();

        Assert.Equal(0, cache.DirtyCount);
        Assert.Equal(51, disk.Contents("db")[Page.Size + 100]);
        Assert.Equal(53, disk.Contents("db")[3 * Page.Size + 100]);
    }

    [Fact]
    public void Bytes_handed_out_are_never_changed_or_reused_for_another_page()
    {
        var (_, file) = FileWithPages(6);
        var cache = new PageCache(file, capacity: 2);
        var page1 = cache.Get(1, Latest);
        var copy = page1.ToArray();

        Publish(cache, 1, PageFilledWith(77));
        for (uint id = 2; id < 6; id++)
        {
            cache.Get(id, Latest);
        }

        Assert.Equal(copy, page1);
    }

    [Fact]
    public void A_damaged_page_in_the_file_is_refused()
    {
        var (_, file) = FileWithPages(3);
        var damaged = PageFilledWith(2);
        damaged[500] ^= 1;
        file.Write(2 * Page.Size, damaged);

        var cache = new PageCache(file, capacity: 4);
        var error = Assert.Throws<InvalidDataException>(() => cache.Get(2, Latest));
        Assert.Contains("page 2", error.Message);
    }

    [Fact]
    public void A_snapshot_keeps_seeing_the_version_of_a_page_it_began_with()
    {
        var (_, file) = FileWithPages(3);
        var cache = new PageCache(file, capacity: 8);
        long before = cache.BeginSnapshot();

        Publish(cache, 1, PageFilledWith(10));
        long after = cache.BeginSnapshot();
        Publish(cache, 1, PageFilledWith(20));

        Assert.Equal(1, cache.Get(1, before)[100]);   // the version in the file when it began
        Assert.Equal(10, cache.Get(1, after)[100]);
        Assert.Equal(20, cache.Get(1, Latest)[100]);
        Assert.Equal(20, cache.Get(1, cache.BeginSnapshot())[100]);
    }

    [Fact]
    public void An_old_version_goes_once_no_open_snapshot_can_see_it()
    {
        var (_, file) = FileWithPages(3);
        var cache = new PageCache(file, capacity: 8);
        long first = cache.BeginSnapshot();
        Publish(cache, 1, PageFilledWith(10));
        long second = cache.BeginSnapshot();
        Publish(cache, 1, PageFilledWith(20));
        Assert.Equal(2, cache.KeptVersions);

        cache.EndSnapshot(first);
        Assert.Equal(1, cache.KeptVersions);    // the second still needs version 10
        Assert.Equal(10, cache.Get(1, second)[100]);

        cache.EndSnapshot(second);
        Assert.Equal(0, cache.KeptVersions);
    }

    [Fact]
    public void A_page_with_versions_kept_for_a_snapshot_is_never_evicted_and_the_cache_grows_instead()
    {
        var (_, file) = FileWithPages(6);
        var cache = new PageCache(file, capacity: 2);
        long open = cache.BeginSnapshot();
        // Neither page is in memory: the versions the snapshot needs come from the file.
        Publish(cache, 1, PageFilledWith(71));
        Publish(cache, 2, PageFilledWith(72));

        for (uint id = 3; id < 6; id++)
        {
            cache.Get(id, Latest);
        }

        Assert.True(cache.Count > 2, $"{cache.Count} pages");
        Assert.Equal(1, cache.Get(1, open)[100]);
        Assert.Equal(2, cache.Get(2, open)[100]);

        cache.EndSnapshot(open);
        Assert.Equal(0, cache.KeptVersions);
    }

    [Fact]
    public void A_page_that_did_not_exist_before_has_no_older_version_to_keep()
    {
        var (_, file) = FileWithPages(2);
        var cache = new PageCache(file, capacity: 4);
        long open = cache.BeginSnapshot();

        // Page 5 is past the end of the file: reading an older version of it would fail.
        cache.Publish([(5u, PageFilledWith(55))], isNew: id => id >= 2);

        Assert.Equal(55, cache.Get(5, Latest)[100]);
        Assert.Equal(0, cache.KeptVersions);
        cache.EndSnapshot(open);
    }

    private static void Publish(PageCache cache, uint id, byte[] page) =>
        cache.Publish([(id, page)], isNew: _ => false);

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
