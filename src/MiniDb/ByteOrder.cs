namespace MiniDb;

/// <summary>Keys compared and hashed as unsigned bytes: the order of the tree.</summary>
internal sealed class ByteOrder : IComparer<byte[]>, IEqualityComparer<byte[]>
{
    public static readonly ByteOrder Instance = new();

    public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y);

    public bool Equals(byte[]? x, byte[]? y) => x.AsSpan().SequenceEqual(y);

    public int GetHashCode(byte[] key)
    {
        var hash = new HashCode();
        hash.AddBytes(key);
        return hash.ToHashCode();
    }
}
