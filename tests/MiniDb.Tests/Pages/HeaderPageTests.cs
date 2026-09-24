using MiniDb.Pages;

namespace MiniDb.Tests.Pages;

public class HeaderPageTests
{
    [Fact]
    public void A_new_header_describes_an_empty_tree_and_reads_back_what_was_written()
    {
        var bytes = new byte[Page.Size];
        var header = HeaderPage.Format(bytes);
        Assert.Equal(1u, header.Root);
        Assert.Equal(2u, header.PageCount);

        header.Root = 7;
        header.PageCount = 12;
        header.FirstFree = 9;
        header.FreeCount = 3;
        Page.Seal(bytes);

        var read = new HeaderPage(bytes);
        read.Validate();
        Assert.Equal((7u, 12u, 9u, 3u), (read.Root, read.PageCount, read.FirstFree, read.FreeCount));
    }

    [Fact]
    public void A_file_that_is_not_a_database_is_named_as_such()
    {
        var bytes = new byte[Page.Size];
        new Random(5).NextBytes(bytes);
        var error = Assert.Throws<InvalidDataException>(() => new HeaderPage(bytes).Validate());
        Assert.Equal("not a MiniDB database", error.Message);
    }

    [Fact]
    public void A_damaged_header_is_refused()
    {
        var bytes = new byte[Page.Size];
        var header = HeaderPage.Format(bytes);
        Page.Seal(bytes);
        header.Root = 99; // changed after sealing, as a bad write would
        var error = Assert.Throws<InvalidDataException>(() => new HeaderPage(bytes).Validate());
        Assert.Equal("the database header is damaged", error.Message);
    }

    [Fact]
    public void A_format_this_build_does_not_know_is_refused()
    {
        var bytes = new byte[Page.Size];
        HeaderPage.Format(bytes);
        bytes[5] = HeaderPage.FormatVersion + 1;
        Page.Seal(bytes);
        var error = Assert.Throws<InvalidDataException>(() => new HeaderPage(bytes).Validate());
        Assert.StartsWith("format version 2", error.Message);
    }
}
