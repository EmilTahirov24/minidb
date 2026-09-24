using MiniDb.Pages;

namespace MiniDb.Tests.Pages;

public class ChecksumTests
{
    [Fact]
    public void Crc32C_matches_the_published_check_value()
    {
        // The standard check: CRC-32C of the nine ASCII digits.
        Assert.Equal(0xE3069283u, Crc32C.Compute("123456789"u8));
        Assert.Equal(0u, Crc32C.Compute([]));
    }

    [Fact]
    public void Crc32C_agrees_with_a_bit_by_bit_reference_at_every_length() => Seeds.Each(300, seed =>
    {
        var random = new Random(seed);
        var data = new byte[random.Next(0, 5000)];
        random.NextBytes(data);
        Assert.Equal(Reference(data), Crc32C.Compute(data));
    });

    [Fact]
    public void A_sealed_page_is_intact()
    {
        var page = RandomPage(seed: 1);
        Page.Seal(page);
        Assert.True(Page.IsIntact(page));
    }

    [Fact]
    public void Every_single_flipped_bit_is_detected()
    {
        var page = RandomPage(seed: 2);
        Page.Seal(page);
        for (int bit = 0; bit < Page.Size * 8; bit++)
        {
            page[bit / 8] ^= (byte)(1 << (bit % 8));
            Assert.False(Page.IsIntact(page), $"bit {bit} flipped and not detected");
            page[bit / 8] ^= (byte)(1 << (bit % 8));
        }
    }

    [Fact]
    public void A_page_torn_between_two_versions_is_detected()
    {
        var before = RandomPage(seed: 3);
        var after = RandomPage(seed: 4);
        Page.Seal(before);
        Page.Seal(after);

        // A crash while the new version is written: the first sectors are new, the rest old.
        for (int sectors = 1; sectors < Page.Size / 512; sectors++)
        {
            var torn = after[..(sectors * 512)].Concat(before[(sectors * 512)..]).ToArray();
            Assert.False(Page.IsIntact(torn), $"torn after {sectors} sectors and not detected");
        }
    }

    [Fact]
    public void A_page_that_was_never_written_is_not_intact() =>
        Assert.False(Page.IsIntact(new byte[Page.Size]));

    private static byte[] RandomPage(int seed)
    {
        var page = new byte[Page.Size];
        new Random(seed).NextBytes(page);
        return page;
    }

    private static uint Reference(ReadOnlySpan<byte> data)
    {
        uint crc = uint.MaxValue;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0x82F63B78u : crc >> 1;
            }
        }
        return ~crc;
    }
}
