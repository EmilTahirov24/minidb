using System.Buffers.Binary;

namespace MiniDb.Pages;

/// <summary>Unsigned integers in 7-bit groups, low first: lengths under 128 take one byte.</summary>
internal static class Varint
{
    public static int SizeOf(int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        int size = 1;
        while (value >= 0x80)
        {
            value >>= 7;
            size++;
        }
        return size;
    }

    public static int Write(Span<byte> destination, int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        int written = 0;
        while (value >= 0x80)
        {
            destination[written++] = (byte)(value | 0x80);
            value >>= 7;
        }
        destination[written++] = (byte)value;
        return written;
    }

    public static int Read(ReadOnlySpan<byte> source, out int value)
    {
        value = 0;
        for (int i = 0, shift = 0; i < 5; i++, shift += 7)
        {
            byte b = source[i];
            value |= (b & 0x7F) << shift;
            if (b < 0x80)
            {
                return i + 1;
            }
        }
        throw new InvalidDataException("a length in a cell is longer than five bytes");
    }
}

/// <summary>A key and its value, in a leaf: key length, value length, key, value.</summary>
internal static class LeafCell
{
    public static int SizeOf(int keyLength, int valueLength) =>
        Varint.SizeOf(keyLength) + Varint.SizeOf(valueLength) + keyLength + valueLength;

    public static int Write(Span<byte> destination, ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        int at = Varint.Write(destination, key.Length);
        at += Varint.Write(destination[at..], value.Length);
        key.CopyTo(destination[at..]);
        at += key.Length;
        value.CopyTo(destination[at..]);
        return at + value.Length;
    }

    /// <summary>How many bytes the cell starting at <paramref name="cell"/> takes.</summary>
    public static int SizeAt(ReadOnlySpan<byte> cell)
    {
        int at = Varint.Read(cell, out int keyLength);
        at += Varint.Read(cell[at..], out int valueLength);
        return at + keyLength + valueLength;
    }

    public static ReadOnlySpan<byte> Key(ReadOnlySpan<byte> cell)
    {
        int at = Varint.Read(cell, out int keyLength);
        at += Varint.Read(cell[at..], out _);
        return cell.Slice(at, keyLength);
    }

    public static ReadOnlySpan<byte> Value(ReadOnlySpan<byte> cell)
    {
        int at = Varint.Read(cell, out int keyLength);
        at += Varint.Read(cell[at..], out int valueLength);
        return cell.Slice(at + keyLength, valueLength);
    }
}

/// <summary>
/// A separator in an internal page: the child page, then the key. Every key in the child's
/// subtree is smaller than this key.
/// </summary>
internal static class InternalCell
{
    public static int SizeOf(int keyLength) => sizeof(uint) + Varint.SizeOf(keyLength) + keyLength;

    public static int Write(Span<byte> destination, uint child, ReadOnlySpan<byte> key)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination, child);
        int at = sizeof(uint) + Varint.Write(destination[sizeof(uint)..], key.Length);
        key.CopyTo(destination[at..]);
        return at + key.Length;
    }

    public static int SizeAt(ReadOnlySpan<byte> cell)
    {
        int at = sizeof(uint) + Varint.Read(cell[sizeof(uint)..], out int keyLength);
        return at + keyLength;
    }

    public static uint Child(ReadOnlySpan<byte> cell) => BinaryPrimitives.ReadUInt32LittleEndian(cell);

    public static void SetChild(Span<byte> cell, uint child) =>
        BinaryPrimitives.WriteUInt32LittleEndian(cell, child);

    public static ReadOnlySpan<byte> Key(ReadOnlySpan<byte> cell)
    {
        int at = sizeof(uint) + Varint.Read(cell[sizeof(uint)..], out int keyLength);
        return cell.Slice(at, keyLength);
    }
}
