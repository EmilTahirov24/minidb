using MiniDb.Log;
using MiniDb.Pages;
using MiniDb.Storage;
using MiniDb.Tree;

namespace MiniDb;

public sealed class DatabaseOptions
{
    /// <summary>Pages the cache holds; 4 MiB of them by default.</summary>
    public int CachePages { get; init; } = 1024;

    /// <summary>How large the log may grow before a checkpoint copies it into the data file.</summary>
    public long CheckpointAfterBytes { get; init; } = 4 * 1024 * 1024;
}

/// <summary>
/// A key-value store on disk: sorted keys in a B+tree, every committed change made durable
/// through a write-ahead log, and recovery to the last acknowledged commit after a crash.
/// </summary>
/// <remarks>
/// Any number of transactions run at once, each on a snapshot of the database as of when it
/// began: snapshot isolation. Reading never waits. Commits go one at a time, and a commit that
/// wrote a key another transaction wrote and committed first fails with
/// <see cref="WriteConflictException"/>.
/// </remarks>
public sealed class Database : IDisposable
{
    private readonly IStorageFile dataFile;
    private readonly IStorageFile logFile;
    private readonly WriteAheadLog log;
    private readonly DatabaseOptions options;

    // One commit or checkpoint at a time, held across the flush. The record of written keys
    // below is only ever touched under it.
    private readonly object commitLock = new();

    // For each key written by a commit that an open transaction may not have seen, the sequence
    // number of the last such commit - and the commits in order, to drop entries as the oldest
    // open snapshot moves past them.
    private readonly Dictionary<byte[], long> lastWritten = new(ByteOrder.Instance);
    private readonly Queue<(long Sequence, byte[][] Keys)> written = new();

    private int openTransactions;
    private volatile bool failed;
    private volatile bool disposed;

    private Database(IStorageFile dataFile, IStorageFile logFile, WriteAheadLog log, DatabaseOptions options)
    {
        this.dataFile = dataFile;
        this.logFile = logFile;
        this.log = log;
        this.options = options;
        Cache = new PageCache(dataFile, options.CachePages);
    }

    internal PageCache Cache { get; }

    /// <summary>
    /// Open the database at <paramref name="path"/> - <c>data/app</c> is <c>data/app.db</c> and
    /// <c>data/app.wal</c> - creating it if it does not exist, and recovering it if the last
    /// run did not close it.
    /// </summary>
    public static Database Open(string path, DatabaseOptions? options = null)
    {
        string full = Path.GetFullPath(path);
        var storage = new FileSystemStorage(Path.GetDirectoryName(full)!);
        return Open(storage, Path.GetFileName(full), options ?? new DatabaseOptions());
    }

    internal static Database Open(IStorage storage, string name, DatabaseOptions options)
    {
        string dataName = name + ".db", logName = name + ".wal";
        if (!storage.Exists(dataName))
        {
            Create(storage, dataName, logName);
        }

        IStorageFile? dataFile = null, logFile = null;
        try
        {
            dataFile = storage.Open(dataName);
            logFile = storage.Open(logName);
            var log = WriteAheadLog.Open(logFile);
            Recover(dataFile, log);

            var database = new Database(dataFile, logFile, log, options);
            new HeaderPage(database.Cache.Get(0, PageCache.Latest)).Validate();
            return database;
        }
        catch
        {
            dataFile?.Dispose();
            logFile?.Dispose();
            throw;
        }
    }

    public WriteTransaction BeginWrite()
    {
        Opening();
        return new WriteTransaction(this);
    }

    public ReadTransaction BeginRead()
    {
        Opening();
        return new ReadTransaction(this);
    }

    /// <summary>Copy everything the log holds into the data file, and empty the log.</summary>
    public void Checkpoint()
    {
        lock (commitLock)
        {
            ThrowIfFailed();
            RunCheckpoint();
        }
    }

    /// <summary>
    /// Checkpoint and close. After an I/O error, close without touching the files. Every
    /// transaction has to be finished first.
    /// </summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        if (Volatile.Read(ref openTransactions) > 0)
        {
            throw new InvalidOperationException("transactions are still open; finish them before closing the database");
        }
        lock (commitLock)
        {
            try
            {
                if (!failed)
                {
                    RunCheckpoint();
                }
            }
            finally
            {
                disposed = true;
                dataFile.Dispose();
                logFile.Dispose();
            }
        }
    }

    /// <summary>
    /// Check the tree's shape and that every page is accounted for, throwing
    /// <see cref="InvalidDataException"/> if anything is wrong. Reads every page.
    /// </summary>
    internal void Verify()
    {
        using var transaction = BeginRead();
        transaction.Verify();
    }

    /// <summary>Pages in the data file and how many of them are free, as the header counts them.</summary>
    internal (uint Pages, uint Free) PageCounts()
    {
        using var transaction = BeginRead();
        return transaction.PageCounts();
    }

    /// <summary>
    /// Commit a write transaction: check it against the commits it did not see, apply its
    /// changes to the newest tree, make them durable, then publish them.
    /// </summary>
    internal void Commit(WriteTransaction transaction)
    {
        lock (commitLock)
        {
            ThrowIfFailed();
            foreach (var key in transaction.Writes.Keys)
            {
                if (lastWritten.TryGetValue(key, out long sequence) && sequence > transaction.Snapshot)
                {
                    throw new WriteConflictException(
                        $"key {Convert.ToHexString(key)} was written by a transaction that committed after this one began");
                }
            }

            var pages = new CommitPages(Cache);
            var tree = new BTree(pages);
            foreach (var (key, value) in transaction.Writes)
            {
                if (value is null)
                {
                    tree.Delete(key);
                }
                else
                {
                    tree.Put(key, value);
                }
            }
            if (pages.Changed.Count == 0)
            {
                return;
            }

            var frames = new List<(uint, byte[])>(pages.Changed.Count);
            foreach (var (id, bytes) in pages.Changed)
            {
                Page.Seal(bytes);
                frames.Add((id, bytes));
            }
            long committed;
            try
            {
                log.Append(frames);
                log.Flush();
                // Publishing can evict a page, and writing it can fail. The commit is durable
                // already, but a cache left half published must never reach the data file at a
                // checkpoint, which would then empty the log.
                committed = Cache.Publish(frames, pages.IsNew);
            }
            catch
            {
                // After a failed write or flush the operating system may already have dropped the
                // data, and a second flush can report success anyway; only recovery is known right.
                failed = true;
                throw;
            }

            var keys = transaction.Writes.Keys.ToArray();
            foreach (var key in keys)
            {
                lastWritten[key] = committed;
            }
            written.Enqueue((committed, keys));
            ForgetWritesOlderThan(Cache.OldestOpenSnapshot);

            if (log.Size >= options.CheckpointAfterBytes)
            {
                RunCheckpoint();
            }
        }
    }

    internal void Closed() => Interlocked.Decrement(ref openTransactions);

    internal void ThrowIfFailed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (failed)
        {
            throw new InvalidOperationException("the database hit an I/O error and has to be reopened, which recovers it");
        }
    }

    private void Opening()
    {
        ThrowIfFailed();
        Interlocked.Increment(ref openTransactions);
    }

    /// <summary>
    /// A commit at or below the oldest open snapshot has been seen by every open transaction,
    /// and every later one will begin after it: it can never conflict again.
    /// </summary>
    private void ForgetWritesOlderThan(long oldest)
    {
        while (written.Count > 0 && written.Peek().Sequence <= oldest)
        {
            var (sequence, keys) = written.Dequeue();
            foreach (var key in keys)
            {
                if (lastWritten.TryGetValue(key, out long last) && last == sequence)
                {
                    lastWritten.Remove(key);
                }
            }
        }
    }

    private void RunCheckpoint()
    {
        try
        {
            Cache.WriteBack();
            dataFile.Flush();
            log.Reset();
        }
        catch
        {
            failed = true;
            throw;
        }
    }

    /// <summary>Put every committed page image in the log into the data file, then empty the log.</summary>
    private static void Recover(IStorageFile dataFile, WriteAheadLog log)
    {
        var committed = log.ReadCommitted();
        foreach (var (page, image) in committed)
        {
            dataFile.Write((long)page * Page.Size, image);
        }
        if (committed.Count > 0)
        {
            dataFile.Flush();
        }
        log.Reset();
    }

    /// <summary>
    /// Write a new, empty database under a temporary name and rename it into place, so that a
    /// crash while it is created leaves either no database or a complete one.
    /// </summary>
    private static void Create(IStorage storage, string dataName, string logName)
    {
        // A log left from a database that is gone would be replayed onto the new one.
        if (storage.Exists(logName))
        {
            storage.Delete(logName);
        }
        string temporary = dataName + ".new";
        if (storage.Exists(temporary))
        {
            storage.Delete(temporary);
        }
        using (var file = storage.Open(temporary))
        {
            var pages = new byte[2 * Page.Size];
            HeaderPage.Format(pages.AsSpan(0, Page.Size));
            NodePage.Format(pages.AsSpan(Page.Size, Page.Size), PageType.Leaf);
            Page.Seal(pages.AsSpan(0, Page.Size));
            Page.Seal(pages.AsSpan(Page.Size, Page.Size));
            file.Write(0, pages);
            file.Flush();
        }
        storage.Rename(temporary, dataName);
    }
}
