namespace MiniDb.Tests;

/// <summary>Random tests run once per seed, and a failure names the seed that reproduces it.</summary>
internal static class Seeds
{
    public static void Each(int count, Action<int> test)
    {
        for (int seed = 0; seed < count; seed++)
        {
            try
            {
                test(seed);
            }
            catch (Exception failure)
            {
                throw new SeedFailure(seed, failure);
            }
        }
    }
}

internal sealed class SeedFailure(int seed, Exception inner)
    : Exception($"failed with seed {seed}: {inner.Message}", inner);
