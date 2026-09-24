using MiniDb.Pages;

namespace MiniDb.Storage;

/// <summary>
/// Committed pages in memory, a fixed number of them, over the data file. When it is full, the
/// CLOCK algorithm picks a page to evict: the frames are visited in a circle, a page used since
/// the last visit gets a second chance, and the first one that has had it goes. A changed page
/// is written to the data file as it goes.
/// </summary>
/// <remarks>
/// An array handed out is never reused for another page. Code still holding an evicted page's
/// bytes keeps seeing that page, so nothing has to be pinned while the tree works.
/// </remarks>
internal sealed class PageCache
{
    private readonly IStorageFile file;
    private readonly Frame?[] frames;
    private readonly Dictionary<uint, int> slots = [];
    private int hand;

    public PageCache(IStorageFile file, int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        this.file = file;
        frames = new Frame?[capacity];
    }

    public int Count => slots.Count;

    public long Hits { get; private set; }

    public long Misses { get; private set; }

    public long Evictions { get; private set; }

    /// <summary>Changed pages written to the data file because they were evicted, not at a checkpoint.</summary>
    public long WrittenOnEviction { get; private set; }

    public int DirtyCount => frames.Count(frame => frame is { Dirty: true });

    /// <summary>The current version of a page: from memory, or read from the data file and checked.</summary>
    /// <exception cref="InvalidDataException">The page in the data file is damaged or missing.</exception>
    public byte[] Get(uint id)
    {
        if (slots.TryGetValue(id, out int slot))
        {
            Hits++;
            var frame = frames[slot]!;
            frame.Referenced = true;
            return frame.Bytes;
        }

        Misses++;
        var bytes = new byte[Page.Size];
        int read = file.Read((long)id * Page.Size, bytes);
        if (read != Page.Size || !Page.IsIntact(bytes))
        {
            throw new InvalidDataException($"page {id} in the data file is damaged or missing");
        }
        Place(id, bytes, dirty: false);
        return bytes;
    }

    /// <summary>
    /// Make <paramref name="bytes"/> the current version of the page, to reach the data file on
    /// eviction or at the next write-back. The caller must not change the array afterwards.
    /// </summary>
    public void Put(uint id, byte[] bytes)
    {
        if (slots.TryGetValue(id, out int slot))
        {
            frames[slot] = new Frame(id, bytes) { Dirty = true, Referenced = true };
            return;
        }
        Place(id, bytes, dirty: true);
    }

    /// <summary>Write every changed page to the data file. It does not flush the file.</summary>
    public void WriteBack()
    {
        foreach (var frame in frames)
        {
            if (frame is { Dirty: true })
            {
                Write(frame);
                frame.Dirty = false;
            }
        }
    }

    private void Place(uint id, byte[] bytes, bool dirty)
    {
        int slot = TakeSlot();
        frames[slot] = new Frame(id, bytes) { Dirty = dirty, Referenced = true };
        slots[id] = slot;
    }

    private int TakeSlot()
    {
        while (true)
        {
            int slot = hand;
            hand = (hand + 1) % frames.Length;
            var frame = frames[slot];
            if (frame is null)
            {
                return slot;
            }
            if (frame.Referenced)
            {
                frame.Referenced = false;
                continue;
            }
            if (frame.Dirty)
            {
                Write(frame);
                WrittenOnEviction++;
            }
            slots.Remove(frame.Id);
            Evictions++;
            return slot;
        }
    }

    private void Write(Frame frame) => file.Write((long)frame.Id * Page.Size, frame.Bytes);

    private sealed class Frame(uint id, byte[] bytes)
    {
        public uint Id => id;

        public byte[] Bytes => bytes;

        public bool Dirty { get; set; }

        public bool Referenced { get; set; }
    }
}
