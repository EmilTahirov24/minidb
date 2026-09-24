using Microsoft.Data.Sqlite;

namespace MiniDb.Bench;

/// <summary>
/// What every benchmark does to a key-value store, so that MiniDB and SQLite run exactly the
/// same work through the same calls.
/// </summary>
internal interface IStore : IDisposable
{
    string DataFile { get; }

    string LogFile { get; }

    /// <summary>Put every pair, <paramref name="batch"/> to a transaction.</summary>
    void Load(IReadOnlyList<byte[]> keys, byte[] value, int batch);

    /// <summary>One transaction that puts one key: the latency of a durable commit.</summary>
    void PutAndCommit(byte[] key, byte[] value);

    void Delete(IReadOnlyList<byte[]> keys, int batch);

    byte[]? Get(byte[] key);

    /// <summary>Read <paramref name="count"/> pairs in key order from <paramref name="from"/>; how many there were.</summary>
    int Scan(byte[] from, int count);

    void Checkpoint();
}

internal static class Stores
{
    public static IStore Open(string engine, string directory) => engine switch
    {
        "MiniDB" => new MiniDbStore(directory),
        "SQLite" => new SqliteStore(directory),
        _ => throw new ArgumentException($"no engine {engine}", nameof(engine)),
    };

    public static string NewDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "minidb-bench", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}

internal sealed class MiniDbStore(string directory, DatabaseOptions? options = null) : IStore
{
    public Database Database { get; } = Database.Open(Path.Combine(directory, "bench"), options);

    public string DataFile => Path.Combine(directory, "bench.db");

    public string LogFile => Path.Combine(directory, "bench.wal");

    public void Load(IReadOnlyList<byte[]> keys, byte[] value, int batch)
    {
        for (int start = 0; start < keys.Count; start += batch)
        {
            using var tx = Database.BeginWrite();
            for (int i = start; i < Math.Min(start + batch, keys.Count); i++)
            {
                tx.Put(keys[i], value);
            }
            tx.Commit();
        }
    }

    public void PutAndCommit(byte[] key, byte[] value)
    {
        using var tx = Database.BeginWrite();
        tx.Put(key, value);
        tx.Commit();
    }

    public void Delete(IReadOnlyList<byte[]> keys, int batch)
    {
        for (int start = 0; start < keys.Count; start += batch)
        {
            using var tx = Database.BeginWrite();
            for (int i = start; i < Math.Min(start + batch, keys.Count); i++)
            {
                tx.Delete(keys[i]);
            }
            tx.Commit();
        }
    }

    public byte[]? Get(byte[] key)
    {
        using var tx = Database.BeginRead();
        return tx.Get(key);
    }

    public int Scan(byte[] from, int count)
    {
        using var tx = Database.BeginRead();
        return tx.Scan(from).Take(count).Count();
    }

    public void Checkpoint() => Database.Checkpoint();

    public void Dispose() => Database.Dispose();
}

/// <summary>
/// SQLite set up for the same guarantees: its write-ahead log, a flush on every commit, the
/// same 4 MiB of cache, and a table stored as a B-tree on the key itself.
/// </summary>
internal sealed class SqliteStore : IStore
{
    private readonly SqliteConnection connection;
    private readonly SqliteCommand get, put, delete, scan;

    public SqliteStore(string directory, bool autoCheckpoint = true)
    {
        DataFile = Path.Combine(directory, "bench.sqlite");
        connection = new SqliteConnection($"Data Source={DataFile};Pooling=False");
        connection.Open();
        Execute("PRAGMA journal_mode=WAL");
        Execute("PRAGMA synchronous=FULL");
        Execute("PRAGMA cache_size=-4096");
        if (!autoCheckpoint)
        {
            Execute("PRAGMA wal_autocheckpoint=0");
        }
        Execute("CREATE TABLE IF NOT EXISTS kv (k BLOB PRIMARY KEY, v BLOB NOT NULL) WITHOUT ROWID");
        get = Prepare("SELECT v FROM kv WHERE k = $k", "$k");
        put = Prepare("INSERT OR REPLACE INTO kv (k, v) VALUES ($k, $v)", "$k", "$v");
        delete = Prepare("DELETE FROM kv WHERE k = $k", "$k");
        scan = Prepare("SELECT k, v FROM kv WHERE k >= $k ORDER BY k LIMIT $n", "$k", "$n");
    }

    public string DataFile { get; }

    public string LogFile => DataFile + "-wal";

    public void Load(IReadOnlyList<byte[]> keys, byte[] value, int batch)
    {
        for (int start = 0; start < keys.Count; start += batch)
        {
            using var transaction = connection.BeginTransaction();
            put.Transaction = transaction;
            for (int i = start; i < Math.Min(start + batch, keys.Count); i++)
            {
                put.Parameters["$k"].Value = keys[i];
                put.Parameters["$v"].Value = value;
                put.ExecuteNonQuery();
            }
            transaction.Commit();
            put.Transaction = null;
        }
    }

    public void PutAndCommit(byte[] key, byte[] value)
    {
        // Outside an explicit transaction, every statement is its own, committed and flushed.
        put.Parameters["$k"].Value = key;
        put.Parameters["$v"].Value = value;
        put.ExecuteNonQuery();
    }

    public void Delete(IReadOnlyList<byte[]> keys, int batch)
    {
        for (int start = 0; start < keys.Count; start += batch)
        {
            using var transaction = connection.BeginTransaction();
            delete.Transaction = transaction;
            for (int i = start; i < Math.Min(start + batch, keys.Count); i++)
            {
                delete.Parameters["$k"].Value = keys[i];
                delete.ExecuteNonQuery();
            }
            transaction.Commit();
            delete.Transaction = null;
        }
    }

    public byte[]? Get(byte[] key)
    {
        get.Parameters["$k"].Value = key;
        return get.ExecuteScalar() as byte[];
    }

    public int Scan(byte[] from, int count)
    {
        scan.Parameters["$k"].Value = from;
        scan.Parameters["$n"].Value = count;
        using var reader = scan.ExecuteReader();
        int read = 0;
        while (reader.Read())
        {
            _ = (byte[])reader[0];
            _ = (byte[])reader[1];
            read++;
        }
        return read;
    }

    public void Checkpoint() => Execute("PRAGMA wal_checkpoint(TRUNCATE)");

    public long Pragma(string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {name}";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public void Dispose()
    {
        get.Dispose();
        put.Dispose();
        delete.Dispose();
        scan.Dispose();
        connection.Dispose();
    }

    private void Execute(string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private SqliteCommand Prepare(string sql, params string[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (string name in parameters)
        {
            command.Parameters.Add(new SqliteParameter { ParameterName = name });
        }
        command.Prepare();
        return command;
    }
}
