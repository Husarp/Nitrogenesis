using Nitrogenesis.Sim.Rng;

namespace Nitrogenesis.Sim.Evolution;

/// <summary>
/// Builds the next generation from the scored current one (PLAN §3.7). Single-threaded and deterministic: the
/// result depends only on the parents, their scores, the generation seed and the parameters.
/// </summary>
/// <remarks>
/// Children, in order (n = population size, E = elites, R = fresh random genomes):
/// <list type="bullet">
/// <item>0 … E−1: copies of the E best parents, best first, unchanged (no RNG use).</item>
/// <item>E … n−R−1: offspring. Parent A = tournament winner; with chance <see cref="EvolutionParams.CrossoverRate"/>
/// parent B = another tournament winner and the child is their uniform crossover, else a copy of A; then
/// <see cref="Mutation.Apply"/>. The stagnation boost (already in the params) reaches only these.</item>
/// <item>n−R … n−1: fresh random genomes, as in generation 0.</item>
/// </list>
/// Child c draws from its own stream <c>SeedHash.Derive(generationSeed, c)</c>, in the order listed above.
/// </remarks>
public sealed class Breeder
{
    private readonly int[] _order;
    private readonly Xoshiro128StarStar _rng = new(0);

    public Breeder(int populationSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(populationSize, 1);
        _order = new int[populationSize];
    }

    /// <summary>Genome indices of the last bred parents, best first.</summary>
    public ReadOnlySpan<int> Order => _order;

    public void Breed(Population parents, float[] scores, Population children, EvolutionParams p, ulong generationSeed, ReadOnlySpan<float> outputBias)
    {
        ArgumentNullException.ThrowIfNull(parents);
        ArgumentNullException.ThrowIfNull(children);
        int n = parents.Size;
        if (n != _order.Length || children.Size != n || scores.Length != n)
            throw new ArgumentException("Parents, children, scores and breeder must have the same size.");
        if (children.Shape != parents.Shape) throw new ArgumentException("Parents and children must have the same shape.");
        if (ReferenceEquals(children.Weights, parents.Weights)) throw new ArgumentException("Children must not share the parents' weights.");

        Selection.Rank(scores, _order);
        int elites = p.EliteCount(n);
        int firstRandom = n - p.RandomCount(n);

        for (int c = 0; c < elites; c++) parents.Genome(_order[c]).CopyTo(children.Genome(c));

        for (int c = elites; c < firstRandom; c++)
        {
            _rng.Reseed(SeedHash.Derive(generationSeed, (ulong)c));
            Span<float> child = children.Genome(c);
            ReadOnlySpan<float> a = parents.Genome(_order[Selection.Tournament(_rng, n, p.TournamentSize)]);
            if (_rng.NextFloat() < p.CrossoverRate)
            {
                ReadOnlySpan<float> b = parents.Genome(_order[Selection.Tournament(_rng, n, p.TournamentSize)]);
                Crossover.Uniform(a, b, child, _rng);
            }
            else
            {
                a.CopyTo(child);
            }
            Mutation.Apply(child, p.MutationChance, p.MutationSigma, _rng);
        }

        for (int c = firstRandom; c < n; c++)
        {
            _rng.Reseed(SeedHash.Derive(generationSeed, (ulong)c));
            Genome.Randomize(children.Genome(c), children.Shape, outputBias, _rng);
        }
    }
}
