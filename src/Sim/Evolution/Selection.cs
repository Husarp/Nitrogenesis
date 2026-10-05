using Nitrogenesis.Sim.Rng;

namespace Nitrogenesis.Sim.Evolution;

/// <summary>Ranking and tournament selection (PLAN §3.7). Runs single-threaded after the parallel step (§2.1).</summary>
public static class Selection
{
    /// <summary>
    /// Fills <paramref name="order"/> with genome indices, best first: higher score first, NaN last, ties broken
    /// by the lower index. That is a strict total order, so the result is unique whatever the sort algorithm.
    /// </summary>
    public static void Rank(float[] scores, int[] order)
    {
        ArgumentNullException.ThrowIfNull(scores);
        ArgumentNullException.ThrowIfNull(order);
        if (order.Length != scores.Length) throw new ArgumentException("One slot per score is needed.", nameof(order));
        for (int i = 0; i < order.Length; i++) order[i] = i;
        Array.Sort(order, new BestFirst(scores));
    }

    /// <summary>
    /// Tournament of <paramref name="size"/> genomes drawn uniformly with replacement; returns the winner's
    /// <em>rank</em> (0 = best), i.e. the lowest rank drawn. Pair with the order from <see cref="Rank"/>.
    /// </summary>
    public static int Tournament(Xoshiro128StarStar rng, int populationSize, int size)
    {
        int best = rng.NextInt(populationSize);
        for (int k = 1; k < size; k++)
        {
            int r = rng.NextInt(populationSize);
            if (r < best) best = r;
        }
        return best;
    }

    private sealed class BestFirst(float[] scores) : IComparer<int>
    {
        public int Compare(int a, int b)
        {
            float sa = scores[a], sb = scores[b];
            bool na = float.IsNaN(sa), nb = float.IsNaN(sb);
            if (na != nb) return na ? 1 : -1;
            if (!na && sa != sb) return sa > sb ? -1 : 1;
            return a.CompareTo(b);
        }
    }
}
