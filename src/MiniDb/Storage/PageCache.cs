using MiniDb.Pages;

namespace MiniDb.Storage;

/// <summary>
/// Committed pages in memory over the data file, with the versions of them that open
/// transactions may still read.
/// </summary>
/// <remarks>
/// <para>
/// Every commit publishes its pages under the next sequence number. A transaction begins by
/// taking the last number as its snapshot, and reading a page gives it the newest version at or
/// below that number. When a commit replaces a page while a transaction is open, the version it
/// replaces is kept; once no open snapshot can see a version, it is dropped.
/// </para>
/// <para>
/// Pages are evicted with the CLOCK algorithm, a changed page being written to the data file as
/// it goes. Only a page with a single version is ever evicted: a page read from the data file
/// is therefore always the version every open snapshot sees. If every page has older versions
/// kept for open transactions, the cache grows past its size rather than drop one.
/// </para>
/// <para>
/// One lock guards all of it, the last sequence number and the open snapshots included, so a
/// transaction beginning and a commit publishing can never interleave. An array handed out is
/// never changed or reused for another page afterwards, so a reader can use it without the lock.
/// </para>
/// </remarks>
internal sealed class PageCache
{
    /// <summary>A snapshot that sees every published version: what a commit reads and changes.</summary>
    public const long Latest = long.MaxValue;

    private readonly object gate = new();
    private readonly IStorageFile file;
    private readonly int capacity;
    private readonly List<Frame?> frames;
    private readonly Dictionary<uint, int> slots = [];
    private readonly HashSet<Frame> withOldVersions = [];
    private readonly SortedDictionary<long, int> openSnapshots = [];
    private long lastCommit;
    private int hand;

    public PageCache(IStorageFile file, int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        this.file = file;
        this.capacity = capacity;
        frames = [.. new Frame?[capacity]];
    }

    public int Count
    {
        get
        {
            lock (gate)
            {
                return slots.Count;
            }
        }
    }

    /// <summary>Old page versions kept for open transactions, across all pages.</summary>
    public int KeptVersions
    {
        get
        {
            lock (gate)
            {
                return withOldVersions.Sum(frame => frame.Versions.Count - 1);
            }
        }
    }

    public long Hits { get; private set; }

    public long Misses { get; private set; }

    public long Evictions { get; private set; }

    /// <summary>Changed pages written to the data file because they were evicted, not at a checkpoint.</summary>
    public long WrittenOnEviction { get; private set; }

    public int DirtyCount
    {
        get
        {
            lock (gate)
            {
                return frames.Count(frame => frame is { Dirty: true });
            }
        }
    }

    /// <summary>The oldest snapshot still open, or <see cref="Latest"/> if none is.</summary>
    public long OldestOpenSnapshot
    {
        get
        {
            lock (gate)
            {
                return openSnapshots.Count == 0 ? Latest : openSnapshots.First().Key;
            }
        }
    }

    /// <summary>Take the last commit's number as a snapshot, and keep what it can see until it ends.</summary>
    public long BeginSnapshot()
    {
        lock (gate)
        {
            long snapshot = lastCommit;
            openSnapshots[snapshot] = openSnapshots.GetValueOrDefault(snapshot) + 1;
            return snapshot;
        }
    }

    /// <summary>A snapshot is no longer read: drop the versions nobody can see any more.</summary>
    public void EndSnapshot(long snapshot)
    {
        lock (gate)
        {
            int count = openSnapshots[snapshot] - 1;
            if (count == 0)
            {
                openSnapshots.Remove(snapshot);
            }
            else
            {
                openSnapshots[snapshot] = count;
            }
            DropUnseenVersions();
        }
    }

    /// <summary>
    /// The version of a page that <paramref name="snapshot"/> sees: from memory, or read from the
    /// data file and checked. The array must not be changed.
    /// </summary>
    /// <exception cref="InvalidDataException">The page in the data file is damaged or missing.</exception>
    public byte[] Get(uint id, long snapshot)
    {
        lock (gate)
        {
            if (slots.TryGetValue(id, out int slot))
            {
                Hits++;
                var frame = frames[slot]!;
                frame.Referenced = true;
                return frame.At(snapshot);
            }
            Misses++;
            var loaded = Load(id);
            Place(loaded);
            return loaded.Newest;
        }
    }

    /// <summary>
    /// Make one commit's pages the newest versions, under the next sequence number, keeping the
    /// versions they replace while an open transaction may still read them. Pages for which
    /// <paramref name="isNew"/> is true did not exist before this commit.
    /// </summary>
    /// <returns>The commit's sequence number.</returns>
    public long Publish(IEnumerable<(uint Id, byte[] Bytes)> pages, Func<uint, bool> isNew)
    {
        lock (gate)
        {
            long sequence = lastCommit + 1;
            bool keepOld = openSnapshots.Count > 0;
            foreach (var (id, bytes) in pages)
            {
                Frame frame;
                if (slots.TryGetValue(id, out int slot))
                {
                    frame = frames[slot]!;
                }
                else
                {
                    // Not in memory: an open transaction may still need the version the data
                    // file holds, unless the page did not exist until this commit.
                    frame = keepOld && !isNew(id) ? Load(id) : new Frame(id);
                    Place(frame);
                }
                frame.Install(sequence, bytes, keepOld);
                frame.Dirty = true;
                frame.Referenced = true;
                if (frame.Versions.Count > 1)
                {
                    withOldVersions.Add(frame);
                }
            }
            lastCommit = sequence;
            return sequence;
        }
    }

    /// <summary>Write the newest version of every changed page to the data file. It does not flush the file.</summary>
    public void WriteBack()
    {
        lock (gate)
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
    }

    private Frame Load(uint id)
    {
        var bytes = new byte[Page.Size];
        int read = file.Read((long)id * Page.Size, bytes);
        if (read != Page.Size || !Page.IsIntact(bytes))
        {
            throw new InvalidDataException($"page {id} in the data file is damaged or missing");
        }
        // The data file holds the version every open snapshot sees: sequence 0 is below them all.
        var frame = new Frame(id);
        frame.Install(0, bytes, keepPrevious: false);
        return frame;
    }

    private void Place(Frame frame)
    {
        int slot = TakeSlot();
        frame.Referenced = true;
        frames[slot] = frame;
        slots[frame.Id] = slot;
    }

    private int TakeSlot()
    {
        // Two turns of the clock clear every second chance; a third finds nothing if every page
        // is kept for an open transaction, and then the cache grows instead.
        for (int visited = 0; visited < 3 * frames.Count; visited++)
        {
            int slot = hand;
            hand = (hand + 1) % frames.Count;
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
            if (frame.Versions.Count > 1)
            {
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
        frames.Add(null);
        return frames.Count - 1;
    }

    private void DropUnseenVersions()
    {
        long oldest = openSnapshots.Count == 0 ? Latest : openSnapshots.First().Key;
        withOldVersions.RemoveWhere(frame =>
        {
            frame.DropBelow(oldest);
            return frame.Versions.Count == 1;
        });
        // A cache grown past its size for old versions shrinks back once they are gone, from the
        // end, as far as the pages there can go without being written.
        while (frames.Count > capacity && frames[^1] is null or { Versions.Count: 1, Dirty: false })
        {
            if (frames[^1] is { } last)
            {
                slots.Remove(last.Id);
            }
            frames.RemoveAt(frames.Count - 1);
        }
        if (hand >= frames.Count)
        {
            hand = 0;
        }
    }

    private void Write(Frame frame) => file.Write((long)frame.Id * Page.Size, frame.Newest);

    private sealed class Frame(uint id)
    {
        public uint Id => id;

        /// <summary>Versions in the order they were published, the newest last.</summary>
        public List<(long Sequence, byte[] Bytes)> Versions { get; } = [];

        public byte[] Newest => Versions[^1].Bytes;

        public bool Dirty { get; set; }

        public bool Referenced { get; set; }

        public byte[] At(long snapshot)
        {
            for (int i = Versions.Count - 1; i >= 0; i--)
            {
                if (Versions[i].Sequence <= snapshot)
                {
                    return Versions[i].Bytes;
                }
            }
            throw new InvalidOperationException($"no version of page {id} is visible at snapshot {snapshot}");
        }

        public void Install(long sequence, byte[] bytes, bool keepPrevious)
        {
            if (!keepPrevious)
            {
                Versions.Clear();
            }
            Versions.Add((sequence, bytes));
        }

        /// <summary>Keep the version <paramref name="oldest"/> sees and every newer one.</summary>
        public void DropBelow(long oldest)
        {
            int seen = Versions.Count - 1;
            while (seen > 0 && Versions[seen].Sequence > oldest)
            {
                seen--;
            }
            Versions.RemoveRange(0, seen);
        }
    }
}
