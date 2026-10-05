using Nitrogenesis.Sim.Brain;
using Nitrogenesis.Sim.Rng;

namespace Nitrogenesis.Sim.Evolution;

/// <summary>
/// One brain's weights on their own (PLAN §3.5: genome = flat <c>float[]</c> of weights and biases, layout in
/// <see cref="Mlp"/>). Used where a genome outlives its population, e.g. the best genome of a
/// GenerationRecord. Inside a population all genomes share one array (<see cref="Population"/>).
/// </summary>
public sealed class Genome
{
    public Genome(BrainShape shape, float[] weights)
    {
        ArgumentNullException.ThrowIfNull(weights);
        if (weights.Length != shape.WeightCount)
            throw new ArgumentException($"A {shape} genome has {shape.WeightCount} weights, not {weights.Length}.", nameof(weights));
        Shape = shape;
        Weights = weights;
    }

    public BrainShape Shape { get; }
    public float[] Weights { get; }

    /// <summary>True when both genomes have the same shape and bit-identical weights.</summary>
    public bool BitEquals(Genome other) =>
        Shape == other.Shape && Population.BitEquals(Weights, other.Weights);

    /// <summary>
    /// Fills a genome with generation-0 weights: every value N(0, <see cref="EvolutionSettings.InitialSigma"/>)
    /// drawn in index order, then <paramref name="outputBias"/>[k] added to the bias of output k (racing:
    /// +0.3 on throttle, so cars start moving).
    /// </summary>
    public static void Randomize(Span<float> genome, BrainShape shape, ReadOnlySpan<float> outputBias, Xoshiro128StarStar rng)
    {
        if (genome.Length != shape.WeightCount) throw new ArgumentException("Genome length does not match the shape.", nameof(genome));
        if (outputBias.Length != shape.Outputs) throw new ArgumentException("One bias offset per output is needed.", nameof(outputBias));
        for (int i = 0; i < genome.Length; i++) genome[i] = EvolutionSettings.InitialSigma * Gaussian.Next(rng);
        for (int k = 0; k < outputBias.Length; k++) genome[shape.OutputBiasIndex(k)] += outputBias[k];
    }
}
