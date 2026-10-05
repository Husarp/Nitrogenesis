namespace Nitrogenesis.Sim.Rng;

/// <summary>
/// Stable seed derivation for independent random streams (PLAN §2.1: each child gets
/// <c>seed = hash(generationSeed, childIndex)</c>). The result depends only on the two inputs, never on
/// thread scheduling or platform, so a child's stream is the same however the work is split.
/// </summary>
public static class SeedHash
{
    /// <summary>Derives the seed of stream <paramref name="index"/> under <paramref name="seed"/>.</summary>
    public static ulong Derive(ulong seed, ulong index)
    {
        // Mix the index first so neighbouring indices land far apart, then mix again with the seed.
        // Mixing twice keeps (seed, index) and (index, seed) from colliding.
        return SplitMix64.Mix(seed ^ SplitMix64.Mix(index + SplitMix64.Gamma));
    }
}
