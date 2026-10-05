using Nitrogenesis.Sim.Rng;

namespace Nitrogenesis.Sim.Evolution;

/// <summary>Uniform crossover (PLAN §3.7): each weight of the child comes from either parent with 50 % chance.</summary>
public static class Crossover
{
    /// <summary>
    /// Writes the child weight by weight. One 32-bit draw decides 32 consecutive weights (bit k set → weight from
    /// <paramref name="b"/>), so a genome of n weights uses ⌈n / 32⌉ draws. <paramref name="child"/> may alias a parent.
    /// </summary>
    public static void Uniform(ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> child, Xoshiro128StarStar rng)
    {
        if (a.Length != child.Length || b.Length != child.Length) throw new ArgumentException("Parents and child must have the same length.");
        uint bits = 0;
        for (int i = 0; i < child.Length; i++)
        {
            if ((i & 31) == 0) bits = rng.NextUInt();
            child[i] = (bits & 1) != 0 ? b[i] : a[i];
            bits >>= 1;
        }
    }
}
