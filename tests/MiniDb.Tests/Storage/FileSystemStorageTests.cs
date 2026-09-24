using MiniDb.Storage;

namespace MiniDb.Tests.Storage;

public sealed class FileSystemStorageTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "minidb-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void What_was_written_and_flushed_is_there_after_the_file_is_reopened()
    {
        var storage = new FileSystemStorage(directory);
        var data = Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray();
        using (var file = storage.Open("f"))
        {
            file.Write(100, data);
            file.Flush();
        }

        using var reopened = storage.Open("f");
        Assert.Equal(5100, reopened.Length);
        var read = new byte[5000];
        Assert.Equal(5000, reopened.Read(100, read));
        Assert.Equal(data, read);
    }

    [Fact]
    public void A_read_comes_up_short_only_at_the_end_of_the_file()
    {
        var storage = new FileSystemStorage(directory);
        using var file = storage.Open("f");
        file.Write(0, new byte[1000]);
        Assert.Equal(200, file.Read(800, new byte[500]));
        Assert.Equal(0, file.Read(1000, new byte[10]));
    }

    [Fact]
    public void A_file_that_is_open_cannot_be_opened_again()
    {
        var storage = new FileSystemStorage(directory);
        using var file = storage.Open("f");
        Assert.ThrowsAny<IOException>(() => storage.Open("f"));
    }

    [Fact]
    public void A_rename_never_replaces_a_file_that_is_already_there()
    {
        var storage = new FileSystemStorage(directory);
        storage.Open("a").Dispose();
        storage.Open("b").Dispose();
        Assert.ThrowsAny<IOException>(() => storage.Rename("a", "b"));
        Assert.True(storage.Exists("a"));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
