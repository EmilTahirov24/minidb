using System.Buffers.Binary;

namespace MiniDb.Pages;

internal enum PageType : byte
{
    Header = 1,
    Leaf = 2,
    Internal = 3,
    Free = 4,
}

/// <summary>
/// What every page has in common: its size, and a checksum in its first four bytes over the
/// rest, so that a page torn by a crash or damaged on disk is recognised rather than trusted.
/// </summary>
internal static class Page
{
    public const int Size = 4096;

    /// <summary>Page 0 is the header; as a page number anywhere else, 0 means "none".</summary>
    public const uint None = 0;

    private const int ChecksumSize = sizeof(uint);
    private const int TypeOffset = 4;

    public static PageType TypeOf(ReadOnlySpan<byte> page) => (PageType)page[TypeOffset];

    /// <summary>Write the checksum; the last thing done to a page before it is written out.</summary>
    public static void Seal(Span<byte> page) =>
        BinaryPrimitives.WriteUInt32LittleEndian(page, Crc32C.Compute(page[ChecksumSize..Size]));

    /// <summary>True if the page is exactly as it was when it was last sealed.</summary>
    public static bool IsIntact(ReadOnlySpan<byte> page) =>
        page.Length == Size
        && BinaryPrimitives.ReadUInt32LittleEndian(page) == Crc32C.Compute(page[ChecksumSize..]);
}
