namespace Nitrogenesis.Sim.Rng;

/// <summary>
/// xoshiro128** 1.1 (Blackman and Vigna), a small fast 32-bit generator with a 2^128 − 1 period.
/// Seeded through SplitMix64 as the authors recommend. All operations are integer (plus one exact
/// int-to-float scaling), so streams are bit-identical on Linux and Windows.
/// </summary>
/// <remarks>
/// A class rather than a struct, so it can be passed around without accidentally copying the state.
/// Use <see cref="Reseed"/> to reuse an instance without allocating.
/// </remarks>
public sealed class Xoshiro128StarStar
{
    private uint _s0, _s1, _s2, _s3;

    public Xoshiro128StarStar(ulong seed) => Reseed(seed);

    /// <summary>Resets the state from a 64-bit seed (two SplitMix64 outputs, low word first).</summary>
    public void Reseed(ulong seed)
    {
        ulong sm = seed;
        ulong a = SplitMix64.Next(ref sm);
        ulong b = SplitMix64.Next(ref sm);
        // SplitMix64's finalizer is a bijection and two consecutive inputs differ, so at most one of
        // a, b can be zero: the all-zero state (the only invalid one) cannot occur.
        _s0 = (uint)a;
        _s1 = (uint)(a >> 32);
        _s2 = (uint)b;
        _s3 = (uint)(b >> 32);
    }

    /// <summary>Sets the raw state directly. Only for reference test vectors; must not be all zero.</summary>
    internal void SetState(uint s0, uint s1, uint s2, uint s3)
    {
        if ((s0 | s1 | s2 | s3) == 0) throw new ArgumentException("xoshiro state must not be all zero.");
        (_s0, _s1, _s2, _s3) = (s0, s1, s2, s3);
    }

    /// <summary>Next uniformly distributed 32-bit value.</summary>
    public uint NextUInt()
    {
        uint result = RotateLeft(_s1 * 5, 7) * 9;
        uint t = _s1 << 9;
        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;
        _s2 ^= t;
        _s3 = RotateLeft(_s3, 11);
        return result;
    }

    /// <summary>Uniform float in [0, 1): the top 24 bits scaled by 2^-24 (exact, no rounding).</summary>
    public float NextFloat() => (NextUInt() >> 8) * (1f / 16777216f);

    /// <summary>Uniform integer in [0, <paramref name="bound"/>), unbiased (Lemire's multiply-and-reject).</summary>
    public int NextInt(int bound)
    {
        if (bound <= 0) throw new ArgumentOutOfRangeException(nameof(bound), "bound must be positive.");
        uint b = (uint)bound;
        ulong m = (ulong)NextUInt() * b;
        uint low = (uint)m;
        if (low < b)
        {
            uint threshold = (0u - b) % b; // (2^32 − b) mod b
            while (low < threshold)
            {
                m = (ulong)NextUInt() * b;
                low = (uint)m;
            }
        }
        return (int)(m >> 32);
    }

    private static uint RotateLeft(uint x, int k) => (x << k) | (x >> (32 - k));
}
