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
    private readonly int[] _parents;
    private readonly Xoshiro128StarStar _rng = new(0);

    public Breeder(int populationSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(populationSize, 1);
        _order = new int[populationSize];
        _parents = new int[populationSize];
    }

    /// <summary>Genome indices of the last bred parents, best first.</summary>
    public ReadOnlySpan<int> Order => _order;

    /// <summary>
    /// For each child of the last <see cref="Breed"/>, the index (in the parent generation) of the parent it came from
    /// (parent A of a crossover), or −1 for a fresh random genome. Only for the view (family colours); it changes no result.
    /// </summary>
    public ReadOnlySpan<int> Parents => _parents;

    /// <summary>The first <see cref="EliteCount"/> children of the last <see cref="Breed"/> are unchanged copies of their parent.</summary>
    public int EliteCount { get; private set; }

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

        EliteCount = elites;
        for (int c = 0; c < elites; c++)
        {
            parents.Genome(_order[c]).CopyTo(children.Genome(c));
            _parents[c] = _order[c];
        }

        for (int c = elites; c < firstRandom; c++)
        {
            _rng.Reseed(SeedHash.Derive(generationSeed, (ulong)c));
            Span<float> child = children.Genome(c);
            int parentA = _order[Selection.Tournament(_rng, n, p.TournamentSize)];
            _parents[c] = parentA;
            ReadOnlySpan<float> a = parents.Genome(parentA);
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
            _parents[c] = -1;
            Genome.Randomize(children.Genome(c), children.Shape, outputBias, _rng);
        }
    }

    /// <summary>
    /// "Fork from best only" (PLAN §5): child 0 is <paramref name="best"/> unchanged; every other child is a copy
    /// of it mutated with <paramref name="p"/>'s chance and σ, drawing from its own stream
    /// <c>SeedHash.Derive(generationSeed, c)</c>. Deterministic, so the fork's first generation can be rebuilt.
    /// </summary>
    public static void FromBest(ReadOnlySpan<float> best, Population children, EvolutionParams p, ulong generationSeed)
    {
        ArgumentNullException.ThrowIfNull(children);
        if (best.Length != children.GenomeLength) throw new ArgumentException("The genome does not match the population's shape.", nameof(best));
        var rng = new Xoshiro128StarStar(0);
        best.CopyTo(children.Genome(0));
        for (int c = 1; c < children.Size; c++)
        {
            rng.Reseed(SeedHash.Derive(generationSeed, (ulong)c));
            Span<float> child = children.Genome(c);
            best.CopyTo(child);
            Mutation.Apply(child, p.MutationChance, p.MutationSigma, rng);
        }
    }
}
