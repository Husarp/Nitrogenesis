using Nitrogenesis.Sim.Rng;

namespace Nitrogenesis.Sim.Evolution;

/// <summary>
/// A population of another size, for a "cars per generation" change (PLAN §6.4: a new-segment setting): smaller keeps the
/// first genomes (a bred population lists the elites first and the fresh random cars last, so the tail goes first); bigger
/// adds mutated copies of the existing genomes in turn. Deterministic, so a rebuild reproduces it.
/// </summary>
public static class PopulationResize
{
    /// <summary>Stream tag mixed into the generation seed, so the fill draws differ from the breeding draws.</summary>
    private const ulong Stream = 0x5E51_2E00_0000_0001;

    /// <summary>The seed for resizing the population that generation <paramref name="generationSeed"/> evaluates.</summary>
    public static ulong SeedFor(ulong generationSeed) => SeedHash.Derive(generationSeed, Stream);

    /// <param name="source">The population (not changed).</param>
    /// <param name="size">The new number of genomes (≥ 1).</param>
    /// <param name="p">Mutation chance and σ of the added copies (the parameters that bred <paramref name="source"/>).</param>
    /// <param name="seed">See <see cref="SeedFor"/>.</param>
    public static Population To(Population source, int size, EvolutionParams p, ulong seed)
    {
        ArgumentNullException.ThrowIfNull(source);
        var result = new Population(source.Shape, size);
        int keep = Math.Min(size, source.Size);
        source.Weights.AsSpan(0, keep * source.GenomeLength).CopyTo(result.Weights);
        var rng = new Xoshiro128StarStar(0);
        for (int c = keep; c < size; c++)
        {
            rng.Reseed(SeedHash.Derive(seed, (ulong)c));
            Span<float> child = result.Genome(c);
            source.Genome((c - keep) % source.Size).CopyTo(child);
            Mutation.Apply(child, p.MutationChance, p.MutationSigma, rng);
        }
        return result;
    }
}
