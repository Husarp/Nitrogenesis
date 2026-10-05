namespace Nitrogenesis.Sim.Rng;

/// <summary>
/// SplitMix64 (Steele, Lea, Flood 2014; reference code by Vigna). Used to expand a 64-bit seed into
/// xoshiro128** state and to derive independent per-child seeds. Pure integer arithmetic, so it is
/// bit-identical on every platform.
/// </summary>
public static class SplitMix64
{
    /// <summary>The golden-ratio increment added to the state before each output.</summary>
    public const ulong Gamma = 0x9E3779B97F4A7C15UL;

    /// <summary>Advances <paramref name="state"/> and returns the next 64-bit output.</summary>
    public static ulong Next(ref ulong state)
    {
        state += Gamma;
        return Mix(state);
    }

    /// <summary>
    /// The SplitMix64 output finalizer. It is a bijection on 64-bit values with strong avalanche,
    /// so it maps distinct inputs to distinct, well-scrambled outputs.
    /// </summary>
    public static ulong Mix(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
