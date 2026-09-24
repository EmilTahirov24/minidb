namespace MiniDb.Tests;

/// <summary>
/// Comparisons of bytes and of key-value lists. Assert.Equal can compare these too, but it
/// walks byte arrays one boxed byte at a time: with it, the test suite took 24 seconds instead of 3.
/// </summary>
internal static class Same
{
    public static void Bytes(byte[]? expected, byte[]? actual, string what = "value")
    {
        if (expected is null || actual is null)
        {
            Assert.True(
                expected is null && actual is null,
                $"{what}: expected {(expected is null ? "none" : "one")}, got {(actual is null ? "none" : "one")}");
            return;
        }
        Assert.True(expected.AsSpan().SequenceEqual(actual), $"{what} differs");
    }

    public static void Entries(
        IReadOnlyList<KeyValuePair<byte[], byte[]>> expected,
        IReadOnlyList<KeyValuePair<byte[], byte[]>> actual)
    {
        Assert.True(expected.Count == actual.Count, $"{actual.Count} entries, expected {expected.Count}");
        for (int i = 0; i < expected.Count; i++)
        {
            if (!expected[i].Key.AsSpan().SequenceEqual(actual[i].Key))
            {
                Assert.Fail(
                    $"entry {i} has key {Convert.ToHexString(actual[i].Key)}, expected {Convert.ToHexString(expected[i].Key)}");
            }
            if (!expected[i].Value.AsSpan().SequenceEqual(actual[i].Value))
            {
                Assert.Fail($"entry {i}, key {Convert.ToHexString(expected[i].Key)}, has the wrong value");
            }
        }
    }
}
