using Nitrogenesis.Sim.Evolution;

namespace Nitrogenesis.Sim.History;

/// <summary>Results of one evaluated generation (PLAN §5: best/avg score, best time, finished count).</summary>
/// <param name="BestScore">Highest score.</param>
/// <param name="AverageScore">Mean score (summed in agent order, in double).</param>
/// <param name="BestIndex">Genome index of the best car (lowest index on a tie).</param>
/// <param name="FinishedCount">Cars that finished.</param>
/// <param name="BestFinishTicks">Fastest finish in ticks (sub-tick precision), +∞ when no car finished.</param>
/// <param name="BestProgress">Largest progress in the mode's distance units (racing: cells).</param>
public readonly record struct GenerationStats(
    float BestScore,
    float AverageScore,
    int BestIndex,
    int FinishedCount,
    float BestFinishTicks,
    float BestProgress);

/// <summary>
/// Everything kept about one generation (PLAN §5): the seed and effective evolution parameters that bred its
/// population, its stats, its best genome, the best car's recording and, until thinned, the full population.
/// </summary>
/// <remarks>
/// Generation g's population = breed(population g−1, its scores, <see cref="Seed"/>, <see cref="Params"/>); for
/// generation 0 the seed is the initialisation seed. So any generation can be rebuilt from an earlier
/// <see cref="PopulationSnapshot"/> by re-evaluating and re-breeding (same SimVersion only).
/// </remarks>
public sealed class GenerationRecord
{
    public required int Generation { get; init; }
    /// <summary>The generation seed its population was bred (or, for generation 0, initialised) with.</summary>
    public required ulong Seed { get; init; }
    /// <summary>Evolution parameters that bred its population, boost included.</summary>
    public required EvolutionParams Params { get; init; }
    public required GenerationStats Stats { get; init; }
    public required Genome BestGenome { get; init; }
    public required Recording BestRecording { get; init; }
    /// <summary>All genomes of the generation (agent-major, as in <see cref="Population.Weights"/>), or null once thinned.</summary>
    public float[]? PopulationSnapshot { get; set; }
}
