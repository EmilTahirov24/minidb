using MiniDb.Pages;
using MiniDb.Tree;

namespace MiniDb;

/// <summary>
/// Changes to the database, made all together or not at all. Nothing is visible to later
/// transactions, or durable, until <see cref="Commit"/> returns; disposed without it, the
/// transaction is abandoned.
/// </summary>
public sealed class WriteTransaction : IDisposable
{
    private readonly Database database;
    private readonly TransactionPages pages;
    private readonly BTree tree;
    private bool done;
    private bool broken;

    internal WriteTransaction(Database database)
    {
        this.database = database;
        pages = new TransactionPages(database, writable: true);
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

    public void Put(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        Check();
        try
        {
            tree.Put(key, value);
        }
        catch (Exception error) when (error is not ArgumentException)
        {
            // Pages may be half changed; this transaction must not commit.
            broken = true;
            throw;
        }
    }

    /// <summary>Remove a key; false if it was not there.</summary>
    public bool Delete(ReadOnlySpan<byte> key)
    {
        Check();
        try
        {
            return tree.Delete(key);
        }
        catch
        {
            broken = true;
            throw;
        }
    }

    /// <summary>Make every change durable and visible. When this returns, a crash cannot undo it.</summary>
    public void Commit()
    {
        Check();
        if (broken)
        {
            throw new InvalidOperationException("an earlier operation in this transaction failed; it cannot commit");
        }
        done = true;
        try
        {
            if (pages.Changed.Count > 0)
            {
                database.Commit(pages.Changed);
            }
        }
        finally
        {
            Close();
        }
    }

    /// <summary>Abandon the transaction unless it committed.</summary>
    public void Dispose()
    {
        if (!done)
        {
            done = true;
            Close();
        }
    }

    private void Close()
    {
        pages.Closed = true;
        database.Leave();
    }

    private void Check()
    {
        ObjectDisposedException.ThrowIf(done, this);
        database.ThrowIfFailed();
    }
}

/// <summary>A consistent view of the database as of its last commit.</summary>
public sealed class ReadTransaction : IDisposable
{
    private readonly Database database;
    private readonly TransactionPages pages;
    private readonly BTree tree;
    private bool done;

    internal ReadTransaction(Database database)
    {
        this.database = database;
        pages = new TransactionPages(database, writable: false);
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
        if (!done)
        {
            done = true;
            pages.Closed = true;
            database.Leave();
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
        ObjectDisposedException.ThrowIf(done, this);
        database.ThrowIfFailed();
    }
}

/// <summary>
/// The pages one transaction sees: the committed versions in the cache, and the transaction's
/// own copies of the pages it changed, which nothing else sees until it commits.
/// </summary>
internal sealed class TransactionPages(Database database, bool writable) : PageSpace
{
    // Pages from here on did not exist when the transaction began: they start out as zeros.
    private readonly uint existing = new HeaderPage(database.Cache.Get(0)).PageCount;

    public SortedDictionary<uint, byte[]> Changed { get; } = [];

    /// <summary>Once the transaction is over, its pages are gone; a scan still under way stops.</summary>
    public bool Closed { get; set; }

    public override byte[] Read(uint page)
    {
        ObjectDisposedException.ThrowIf(Closed, this);
        return Changed.TryGetValue(page, out var copy) ? copy : database.Cache.Get(page);
    }

    public override byte[] Modify(uint page)
    {
        ObjectDisposedException.ThrowIf(Closed, this);
        if (!writable)
        {
            throw new InvalidOperationException("a read transaction cannot change pages");
        }
        if (!Changed.TryGetValue(page, out var copy))
        {
            copy = page < existing ? database.Cache.Get(page).ToArray() : new byte[Page.Size];
            Changed[page] = copy;
        }
        return copy;
    }
}
