using System.Buffers.Binary;
using System.Numerics;

namespace MiniDb.Pages;

/// <summary>
/// CRC-32C (Castagnoli), the checksum on every page and every log frame. The processor computes
/// it directly where it can (SSE4.2 on x86, the CRC extension on Arm), eight bytes at a time.
/// </summary>
internal static class Crc32C
{
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = uint.MaxValue;
        while (data.Length >= sizeof(ulong))
        {
            crc = BitOperations.Crc32C(crc, BinaryPrimitives.ReadUInt64LittleEndian(data));
            data = data[sizeof(ulong)..];
        }
        foreach (byte b in data)
        {
            crc = BitOperations.Crc32C(crc, b);
        }
        return ~crc;
    }
}
