using Nitrogenesis.Sim.Brain;
using Nitrogenesis.Sim.Rng;

namespace Nitrogenesis.Sim.Evolution;

/// <summary>
/// All genomes of one generation in one flat array (PLAN §2.1): genome i is
/// [i·<see cref="GenomeLength"/>, (i+1)·<see cref="GenomeLength"/>) of <see cref="Weights"/>. Genome i drives agent i.
/// </summary>
public sealed class Population
{
    public Population(BrainShape shape, int size)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        Shape = shape;
        Size = size;
        Weights = new float[size * shape.WeightCount];
    }

    /// <summary>Wraps existing weights (e.g. loaded from a session); the array is used, not copied.</summary>
    public Population(BrainShape shape, float[] weights)
    {
        ArgumentNullException.ThrowIfNull(weights);
        int length = shape.WeightCount;
        if (weights.Length == 0 || weights.Length % length != 0)
            throw new ArgumentException($"Weights must be a whole number (≥ 1) of {length}-weight genomes.", nameof(weights));
        Shape = shape;
        Size = weights.Length / length;
        Weights = weights;
    }

    public BrainShape Shape { get; }
    public int Size { get; }
    public int GenomeLength => Shape.WeightCount;
    public float[] Weights { get; }

    public Span<float> Genome(int index) => Weights.AsSpan(CheckIndex(index) * GenomeLength, GenomeLength);

    /// <summary>A standalone copy of genome <paramref name="index"/>.</summary>
    public Genome CopyGenome(int index) => new(Shape, Genome(index).ToArray());

    /// <summary>
    /// Generation 0: genome i is <see cref="Evolution.Genome.Randomize"/> with its own stream
    /// <c>SeedHash.Derive(seed, i)</c>, so the result does not depend on the order genomes are made in.
    /// </summary>
    public void Randomize(ulong seed, ReadOnlySpan<float> outputBias)
    {
        var rng = new Xoshiro128StarStar(0);
        for (int i = 0; i < Size; i++)
        {
            rng.Reseed(SeedHash.Derive(seed, (ulong)i));
            Evolution.Genome.Randomize(Genome(i), Shape, outputBias, rng);
        }
    }

    /// <summary>64-bit FNV-1a over the shape and the bit pattern of every weight (cross-platform fingerprint).</summary>
    public ulong ContentHash()
    {
        ulong h = 0xCBF29CE484222325UL;
        h = Fnv(h, (uint)Shape.Inputs);
        h = Fnv(h, (uint)Shape.Hidden1);
        h = Fnv(h, (uint)Shape.Hidden2);
        h = Fnv(h, (uint)Shape.Outputs);
        foreach (float w in Weights) h = Fnv(h, BitConverter.SingleToUInt32Bits(w));
        return h;
    }

    /// <summary>Same length and the same bit pattern in every slot (so NaN equals NaN and −0 differs from +0).</summary>
    public static bool BitEquals(ReadOnlySpan<float> a, ReadOnlySpan<float> b) =>
        System.Runtime.InteropServices.MemoryMarshal.AsBytes(a).SequenceEqual(System.Runtime.InteropServices.MemoryMarshal.AsBytes(b));

    private static ulong Fnv(ulong h, uint v) => (h ^ v) * 0x100000001B3UL;

    private int CheckIndex(int i) => (uint)i < (uint)Size ? i : throw new ArgumentOutOfRangeException(nameof(i));
}
