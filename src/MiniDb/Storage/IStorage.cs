namespace MiniDb.Storage;

/// <summary>
/// Where a database's files live. The database only ever touches files through this, so that
/// tests can put a simulated disk underneath it and crash it at any moment.
/// </summary>
internal interface IStorage
{
    /// <summary>Open a file for reading and writing, creating it empty if it does not exist.</summary>
    /// <exception cref="IOException">The file is already open.</exception>
    IStorageFile Open(string name);

    bool Exists(string name);

    /// <summary>Give a file a new name; there must be no file with that name already.</summary>
    void Rename(string from, string to);

    void Delete(string name);
}

internal interface IStorageFile : IDisposable
{
    long Length { get; }

    /// <summary>Fill <paramref name="buffer"/> from <paramref name="offset"/>; fewer bytes only at the end of the file.</summary>
    int Read(long offset, Span<byte> buffer);

    void Write(long offset, ReadOnlySpan<byte> data);

    void SetLength(long length);

    /// <summary>Make every write so far durable: from now on, it survives a crash.</summary>
    void Flush();
}
