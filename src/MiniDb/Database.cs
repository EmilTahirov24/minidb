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
/// One transaction runs at a time, reading or writing; the others wait. A thread that already
/// holds a transaction and asks for a second one gets an exception, rather than waiting for
/// itself for ever. Holding a transaction across an <c>await</c> is not supported yet.
/// </remarks>
public sealed class Database : IDisposable
{
    private readonly IStorageFile dataFile;
    private readonly IStorageFile logFile;
    private readonly WriteAheadLog log;
    private readonly DatabaseOptions options;
    private readonly SemaphoreSlim turn = new(1, 1);

    // The thread holding the turn, or 0. A thread that holds it and asks for it again could only
    // wait for itself; any other thread simply waits its turn.
    private int holder;
    private bool failed;
    private bool disposed;

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
            new HeaderPage(database.Cache.Get(0)).Validate();
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
        Enter();
        return new WriteTransaction(this);
    }

    public ReadTransaction BeginRead()
    {
        Enter();
        return new ReadTransaction(this);
    }

    /// <summary>Copy everything the log holds into the data file, and empty the log.</summary>
    public void Checkpoint()
    {
        Enter();
        try
        {
            RunCheckpoint();
        }
        finally
        {
            Leave();
        }
    }

    /// <summary>Checkpoint and close. After an I/O error, close without touching the files.</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        ThrowIfHolding("close the database");
        turn.Wait();
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
            turn.Release();
            turn.Dispose();
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
    /// Make one transaction's pages durable and current. Called with the turn held; when it
    /// returns, the transaction is committed.
    /// </summary>
    internal void Commit(SortedDictionary<uint, byte[]> pages)
    {
        ThrowIfFailed();
        var frames = new List<(uint, byte[])>(pages.Count);
        foreach (var (id, bytes) in pages)
        {
            Page.Seal(bytes);
            frames.Add((id, bytes));
        }
        try
        {
            log.Append(frames);
            log.Flush();
        }
        catch
        {
            // After a failed write or flush the operating system may already have dropped the
            // data, and a second flush can report success anyway; only recovery is known right.
            failed = true;
            throw;
        }
        try
        {
            // Putting a page in the cache can evict another, and writing that one can fail. The
            // transaction is durable already, but a cache left half updated must never reach
            // the data file at a checkpoint, which would then empty the log.
            foreach (var (id, bytes) in frames)
            {
                Cache.Put(id, bytes);
            }
        }
        catch
        {
            failed = true;
            throw;
        }
        if (log.Size >= options.CheckpointAfterBytes)
        {
            RunCheckpoint();
        }
    }

    internal void Leave()
    {
        Volatile.Write(ref holder, 0);
        turn.Release();
    }

    internal void ThrowIfFailed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (failed)
        {
            throw new InvalidOperationException("the database hit an I/O error and has to be reopened, which recovers it");
        }
    }

    private void Enter()
    {
        ThrowIfFailed();
        ThrowIfHolding("open another transaction");
        turn.Wait();
        if (failed || disposed)
        {
            turn.Release();
            ThrowIfFailed();
        }
        Volatile.Write(ref holder, Environment.CurrentManagedThreadId);
    }

    /// <summary>
    /// Only one transaction runs at a time, so code that already holds one and asks for another
    /// would wait for itself for ever. It gets an exception instead.
    /// </summary>
    private void ThrowIfHolding(string what)
    {
        if (Volatile.Read(ref holder) == Environment.CurrentManagedThreadId)
        {
            throw new InvalidOperationException(
                $"a transaction is still open here; one runs at a time, so trying to {what} would wait for ever");
        }
    }

    private void RunCheckpoint()
    {
        ThrowIfFailed();
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
