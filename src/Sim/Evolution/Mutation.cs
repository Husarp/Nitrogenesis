using Nitrogenesis.Sim.Rng;

namespace Nitrogenesis.Sim.Evolution;

/// <summary>Gaussian weight mutation (PLAN §3.7: per-weight chance 10 %, σ 0.2 by default).</summary>
public static class Mutation
{
    /// <summary>
    /// For each weight in index order: one uniform draw; if it is below <paramref name="chance"/>, add
    /// <paramref name="sigma"/> × N(0, 1) (two more draws). Returns how many weights changed.
    /// </summary>
    public static int Apply(Span<float> genome, float chance, float sigma, Xoshiro128StarStar rng)
    {
        int mutated = 0;
        for (int i = 0; i < genome.Length; i++)
        {
            if (rng.NextFloat() < chance)
            {
                genome[i] += sigma * Gaussian.Next(rng);
                mutated++;
            }
        }
        return mutated;
    }
}
