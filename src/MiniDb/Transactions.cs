using MiniDb.Pages;
using MiniDb.Storage;
using MiniDb.Tree;

namespace MiniDb;

/// <summary>
/// Another transaction wrote one of the keys this one wrote, and committed after this one
/// began. Nothing of this transaction was written; it can be retried from the start.
/// </summary>
public sealed class WriteConflictException(string message) : Exception(message);

/// <summary>
/// A consistent view of the database as of the last commit before it began. It never waits
/// for another transaction, and never makes one wait.
/// </summary>
public sealed class ReadTransaction : IDisposable
{
    private readonly Database database;
    private readonly SnapshotPages pages;
    private readonly BTree tree;

    internal ReadTransaction(Database database)
    {
        this.database = database;
        pages = new SnapshotPages(database.Cache);
        tree = new BTree(pages);
    }

    public byte[]? Get(ReadOnlySpan<byte> key)
    {
        Check();
        return tree.Get(key);
    }

    /// <summary>Every key from <paramref name="from"/> up to, not including, <paramref name="to"/>; null for no bound.</summary>
    public IEnumerable<KeyValuePair<byte[], byte[]>> Scan(byte[]? from = null, byte[]? to = null)
    {
        Check();
        return tree.Scan(from, to);
    }

    public void Dispose()
    {
        if (pages.Close())
        {
            database.Closed();
        }
    }

    internal void Verify()
    {
        Check();
        pages.VerifyAccounting(tree.Verify());
    }

    internal (uint Pages, uint Free) PageCounts()
    {
        Check();
        var header = new HeaderPage(pages.Read(0));
        return (header.PageCount, header.FreeCount);
    }

    private void Check()
    {
        ObjectDisposedException.ThrowIf(pages.Closed, this);
        database.ThrowIfFailed();
    }
}

/// <summary>
/// Changes made all together or not at all. It reads the database as of the last commit before
/// it began, with its own changes on top; nobody else sees them until <see cref="Commit"/>
/// returns, and disposed without it, the transaction is abandoned.
/// </summary>
public sealed class WriteTransaction : IDisposable
{
    private readonly Database database;
    private readonly SnapshotPages pages;
    private readonly BTree tree;
    private bool committed;

    internal WriteTransaction(Database database)
    {
        this.database = database;
        pages = new SnapshotPages(database.Cache);
        tree = new BTree(pages);
    }

    internal long Snapshot => pages.Snapshot;

    /// <summary>Every key this transaction wrote, with its new value, or null where it deleted the key.</summary>
    internal SortedDictionary<byte[], byte[]?> Writes { get; } = new(ByteOrder.Instance);

    public byte[]? Get(ReadOnlySpan<byte> key)
    {
        Check();
        return Writes.TryGetValue(key.ToArray(), out var written) ? written : tree.Get(key);
    }

    /// <summary>
    /// Every key from <paramref name="from"/> up to, not including, <paramref name="to"/>, with
    /// this transaction's own changes merged in; null for no bound.
    /// </summary>
    public IEnumerable<KeyValuePair<byte[], byte[]>> Scan(byte[]? from = null, byte[]? to = null)
    {
        Check();
        var written = Writes.Where(pair =>
            (from is null || ByteOrder.Instance.Compare(pair.Key, from) >= 0)
            && (to is null || ByteOrder.Instance.Compare(pair.Key, to) < 0)).ToList();
        return Merge(tree.Scan(from, to), written);
    }

    public void Put(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        Check();
        BTree.CheckSize(key, value);
        Writes[key.ToArray()] = value.ToArray();
    }

    /// <summary>Remove a key; false if it was not there.</summary>
    public bool Delete(ReadOnlySpan<byte> key)
    {
        Check();
        byte[] copy = key.ToArray();
        bool inSnapshot = tree.Get(key) is not null;
        bool existed = Writes.TryGetValue(copy, out var written) ? written is not null : inSnapshot;
        if (inSnapshot)
        {
            Writes[copy] = null;
        }
        else
        {
            // Never committed: forgetting the put is enough, and writing nothing cannot conflict.
            Writes.Remove(copy);
        }
        return existed;
    }

    /// <summary>Make every change durable and visible. When this returns, a crash cannot undo it.</summary>
    /// <exception cref="WriteConflictException">
    /// Another transaction wrote one of the same keys and committed after this one began.
    /// </exception>
    public void Commit()
    {
        Check();
        committed = true;
        try
        {
            if (Writes.Count > 0)
            {
                database.Commit(this);
            }
        }
        finally
        {
            Dispose();
        }
    }

    /// <summary>End the transaction; unless it committed, its changes are dropped.</summary>
    public void Dispose()
    {
        if (pages.Close())
        {
            database.Closed();
        }
    }

    private void Check()
    {
        ObjectDisposedException.ThrowIf(pages.Closed || committed, this);
        database.ThrowIfFailed();
    }

    private static IEnumerable<KeyValuePair<byte[], byte[]>> Merge(
        IEnumerable<KeyValuePair<byte[], byte[]>> snapshot, List<KeyValuePair<byte[], byte[]?>> written)
    {
        using var stored = snapshot.GetEnumerator();
        int next = 0;
        bool hasStored = stored.MoveNext();
        while (hasStored || next < written.Count)
        {
            int order = !hasStored ? 1
                : next == written.Count ? -1
                : ByteOrder.Instance.Compare(stored.Current.Key, written[next].Key);
            if (order < 0)
            {
                yield return stored.Current;
                hasStored = stored.MoveNext();
                continue;
            }
            // This transaction's own change wins; a deletion hides the key.
            if (written[next].Value is { } value)
            {
                yield return new(written[next].Key, value);
            }
            if (order == 0)
            {
                hasStored = stored.MoveNext();
            }
            next++;
        }
    }
}

/// <summary>The pages as a snapshot sees them: read only, and kept for it until it closes.</summary>
internal sealed class SnapshotPages : PageSpace
{
    private readonly PageCache cache;
    private int closed;

    public SnapshotPages(PageCache cache)
    {
        this.cache = cache;
        Snapshot = cache.BeginSnapshot();
    }

    public long Snapshot { get; }

    public bool Closed => Volatile.Read(ref closed) != 0;

    public override byte[] Read(uint page)
    {
        ObjectDisposedException.ThrowIf(Closed, this);
        return cache.Get(page, Snapshot);
    }

    public override byte[] Modify(uint page) =>
        throw new InvalidOperationException("a snapshot is read only; changes go through a commit");

    /// <summary>End the snapshot; true the first time only.</summary>
    public bool Close()
    {
        if (Interlocked.Exchange(ref closed, 1) != 0)
        {
            return false;
        }
        cache.EndSnapshot(Snapshot);
        return true;
    }
}

/// <summary>
/// The pages a commit works on: the newest versions, and private copies of the ones it changes,
/// which nobody sees until they are published.
/// </summary>
internal sealed class CommitPages(PageCache cache) : PageSpace
{
    // Pages from here on did not exist before this commit: they start out as zeros.
    private readonly uint existing = new HeaderPage(cache.Get(0, PageCache.Latest)).PageCount;

    public SortedDictionary<uint, byte[]> Changed { get; } = [];

    public bool IsNew(uint page) => page >= existing;

    public override byte[] Read(uint page) =>
        Changed.TryGetValue(page, out var copy) ? copy : cache.Get(page, PageCache.Latest);

    public override byte[] Modify(uint page)
    {
        if (!Changed.TryGetValue(page, out var copy))
        {
            copy = IsNew(page) ? new byte[Page.Size] : cache.Get(page, PageCache.Latest).ToArray();
            Changed[page] = copy;
        }
        return copy;
    }
}
