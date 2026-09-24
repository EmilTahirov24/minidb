using Microsoft.Win32.SafeHandles;

namespace MiniDb.Storage;

/// <summary>Files in a directory of the real file system.</summary>
internal sealed class FileSystemStorage : IStorage
{
    private readonly string directory;

    public FileSystemStorage(string directory)
    {
        this.directory = directory;
        Directory.CreateDirectory(directory);
    }

    // Opened exclusively: a second handle, from this process or another, gets an IOException.
    public IStorageFile Open(string name) =>
        new OsFile(File.OpenHandle(
            PathOf(name),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            FileOptions.RandomAccess));

    public bool Exists(string name) => File.Exists(PathOf(name));

    public void Rename(string from, string to) => File.Move(PathOf(from), PathOf(to), overwrite: false);

    public void Delete(string name) => File.Delete(PathOf(name));

    private string PathOf(string name) => Path.Combine(directory, name);

    private sealed class OsFile(SafeFileHandle handle) : IStorageFile
    {
        public long Length => RandomAccess.GetLength(handle);

        public int Read(long offset, Span<byte> buffer)
        {
            int total = 0;
            while (total < buffer.Length)
            {
                int read = RandomAccess.Read(handle, buffer[total..], offset + total);
                if (read == 0)
                {
                    break;
                }
                total += read;
            }
            return total;
        }

        public void Write(long offset, ReadOnlySpan<byte> data) => RandomAccess.Write(handle, data, offset);

        public void SetLength(long length) => RandomAccess.SetLength(handle, length);

        // FlushFileBuffers on Windows, fsync on Linux: back from here, the data is on the disk.
        public void Flush() => RandomAccess.FlushToDisk(handle);

        public void Dispose() => handle.Dispose();
    }
}
