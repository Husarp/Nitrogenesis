using Nitrogenesis.Sim.Brain;
using Nitrogenesis.Sim.Evolution;
using Nitrogenesis.Sim.Rng;

public class EvolutionTests
{
    private static readonly BrainShape Shape = BrainShape.Default(22, 2);
    private static readonly float[] Bias = [0.3f, 0f];

    // ---- selection ----

    [Fact]
    public void RankIsBestFirstWithTiesByIndexAndNaNLast()
    {
        float[] scores = [0.5f, float.NaN, 2.1f, 0.5f, -1f, 2.1f, float.NaN];
        var order = new int[scores.Length];
        Selection.Rank(scores, order);
        Assert.Equal([2, 5, 0, 3, 4, 1, 6], order);
    }

    [Fact]
    public void TournamentReturnsTheLowestRankDrawn()
    {
        var a = new Xoshiro128StarStar(5);
        var b = new Xoshiro128StarStar(5);
        for (int t = 0; t < 1000; t++)
        {
            int winner = Selection.Tournament(a, 50, 3);
            int expected = Math.Min(b.NextInt(50), Math.Min(b.NextInt(50), b.NextInt(50)));
            Assert.Equal(expected, winner);
        }
    }

    [Fact]
    public void TournamentOfThreeFavoursTheTop()
    {
        // P(rank r wins) = ((n−r)³ − (n−r−1)³) / n³: the best wins ~3/n, the worst ~1/n³.
        var rng = new Xoshiro128StarStar(1);
        const int n = 100, draws = 300_000;
        var wins = new int[n];
        for (int t = 0; t < draws; t++) wins[Selection.Tournament(rng, n, 3)]++;
        Assert.InRange(wins[0] / (double)draws, 0.028, 0.0315);
        Assert.True(wins[n - 1] < 10);
        Assert.True(wins[0] > wins[n / 2] && wins[n / 2] > wins[n - 5]);
    }

    // ---- crossover and mutation shapes ----

    [Fact]
    public void UniformCrossoverTakesEachWeightFromAParentAboutHalfEach()
    {
        var a = Enumerable.Range(0, Shape.WeightCount).Select(i => (float)i).ToArray();
        var b = a.Select(v => -v - 1f).ToArray();
        var child = new float[a.Length];
        var rng = new Xoshiro128StarStar(3);
        int fromB = 0, total = 0;
        for (int round = 0; round < 200; round++)
        {
            Crossover.Uniform(a, b, child, rng);
            for (int i = 0; i < child.Length; i++)
            {
                Assert.True(child[i] == a[i] || child[i] == b[i]);
                if (child[i] == b[i]) fromB++;
                total++;
            }
        }
        Assert.InRange(fromB / (double)total, 0.49, 0.51);
        Assert.Throws<ArgumentException>(() => Crossover.Uniform(a, b, new float[3], rng));
    }

    [Fact]
    public void UniformCrossoverUsesOneDrawPer32Weights()
    {
        var rng = new Xoshiro128StarStar(9);
        var probe = new Xoshiro128StarStar(9);
        var a = new float[302];
        Crossover.Uniform(a, a, new float[302], rng);
        for (int i = 0; i < 10; i++) probe.NextUInt(); // ⌈302 / 32⌉ = 10
        Assert.Equal(probe.NextUInt(), rng.NextUInt());
    }

    [Fact]
    public void MutationChangesAboutTheChanceShareWithSigma()
    {
        var rng = new Xoshiro128StarStar(11);
        var genome = new float[Shape.WeightCount];
        int changed = 0, total = 0;
        double sumSq = 0;
        for (int round = 0; round < 300; round++)
        {
            Array.Clear(genome);
            int mutated = Mutation.Apply(genome, 0.1f, 0.2f, rng);
            int nonZero = genome.Count(v => v != 0f);
            Assert.True(nonZero <= mutated); // a Gaussian of exactly 0 is possible, so ≤
            changed += mutated;
            total += genome.Length;
            sumSq += genome.Sum(v => (double)v * v);
        }
        Assert.InRange(changed / (double)total, 0.095, 0.105);
        Assert.InRange(Math.Sqrt(sumSq / changed), 0.19, 0.21);
        Assert.Equal(0, Mutation.Apply(genome, 0f, 0.2f, rng));
        Assert.Equal(genome.Length, Mutation.Apply(genome, 1f, 0.2f, rng));
    }

    [Fact]
    public void RandomGenomeHasSigmaHalfAndTheThrottleBias()
    {
        var pop = new Population(Shape, 400);
        pop.Randomize(77, Bias);
        int throttle = Shape.OutputBiasIndex(0), steer = Shape.OutputBiasIndex(1);
        double sum = 0, sumSq = 0, throttleSum = 0, steerSum = 0;
        int n = 0;
        for (int g = 0; g < pop.Size; g++)
        {
            var genome = pop.Genome(g);
            throttleSum += genome[throttle];
            steerSum += genome[steer];
            for (int i = 0; i < genome.Length; i++)
            {
                if (i == throttle) continue;
                sum += genome[i];
                sumSq += genome[i] * genome[i];
                n++;
            }
        }
        Assert.InRange(sum / n, -0.01, 0.01);
        Assert.InRange(Math.Sqrt(sumSq / n), 0.49, 0.51);
        Assert.InRange(throttleSum / pop.Size, 0.3 - 0.08, 0.3 + 0.08); // N(0.3, 0.5) mean over 400
        Assert.InRange(steerSum / pop.Size, -0.08, 0.08);
    }

    [Fact]
    public void RandomizeIsPerGenomeStreams()
    {
        var a = new Population(Shape, 10);
        var b = new Population(Shape, 3);
        a.Randomize(5, Bias);
        b.Randomize(5, Bias);
        Assert.True(Population.BitEquals(a.Genome(2), b.Genome(2)));
        Assert.False(Population.BitEquals(a.Genome(1), a.Genome(2)));
    }

    // ---- breeding ----

    private static (Population Parents, float[] Scores) Scored(int n, ulong seed)
    {
        var parents = new Population(Shape, n);
        parents.Randomize(seed, Bias);
        var rng = new Xoshiro128StarStar(seed);
        var scores = new float[n];
        for (int i = 0; i < n; i++) scores[i] = rng.NextFloat();
        return (parents, scores);
    }

    [Fact]
    public void EliteAndRandomCountsFollowTheFractions()
    {
        var p = EvolutionParams.From(new EvolutionSettings(), false);
        Assert.Equal(10, p.EliteCount(200));
        Assert.Equal(4, p.RandomCount(200));
        Assert.Equal(1, p.EliteCount(5));   // 0.25 rounds to 0, but a positive fraction keeps the best
        Assert.Equal(1, p.RandomCount(5));
        var none = EvolutionParams.From(new EvolutionSettings { ElitismFraction = 0, RandomFraction = 0 }, false);
        Assert.Equal(0, none.EliteCount(200));
        Assert.Equal(0, none.RandomCount(200));
        var big = EvolutionParams.From(new EvolutionSettings { ElitismFraction = 0.5f, RandomFraction = 0.5f }, false);
        Assert.Equal(1, big.EliteCount(1));
        Assert.Equal(0, big.RandomCount(1));
    }

    [Fact]
    public void BoostDoublesChanceAndSigma()
    {
        var s = new EvolutionSettings { MutationChance = 0.1f, MutationSigma = 0.2f };
        var normal = EvolutionParams.From(s, false);
        var boosted = EvolutionParams.From(s, true);
        Assert.Equal(0.2f, boosted.MutationChance);
        Assert.Equal(0.4f, boosted.MutationSigma);
        Assert.True(boosted.Boost && !normal.Boost);
        Assert.Equal(1f, EvolutionParams.From(s with { MutationChance = 0.8f }, true).MutationChance);
    }

    [Fact]
    public void SettingsAreClamped()
    {
        var s = new EvolutionSettings
        {
            ElitismFraction = 2f, TournamentSize = 0, CrossoverRate = float.NaN, MutationChance = -1f,
            MutationSigma = 100f, RandomFraction = 0.9f, StagnationWindow = 1,
        }.Clamped();
        Assert.Equal(0.5f, s.ElitismFraction);
        Assert.Equal(1, s.TournamentSize);
        Assert.Equal(0.5f, s.CrossoverRate);
        Assert.Equal(0f, s.MutationChance);
        Assert.Equal(2f, s.MutationSigma);
        Assert.Equal(0.5f, s.RandomFraction);
        Assert.Equal(5, s.StagnationWindow);
    }

    [Fact]
    public void BreedKeepsElitesUnchangedBestFirst()
    {
        var (parents, scores) = Scored(200, 1);
        var children = new Population(Shape, 200);
        var breeder = new Breeder(200);
        var p = EvolutionParams.From(new EvolutionSettings(), false);
        breeder.Breed(parents, scores, children, p, 99, Bias);

        var order = new int[200];
        Selection.Rank(scores, order);
        Assert.Equal(order, breeder.Order.ToArray());
        for (int c = 0; c < 10; c++) Assert.True(Population.BitEquals(parents.Genome(order[c]), children.Genome(c)));
        // Offspring are new genomes (mutated), not copies.
        for (int c = 10; c < 196; c++)
            Assert.DoesNotContain(Enumerable.Range(0, 200), i => Population.BitEquals(parents.Genome(i), children.Genome(c)));
    }

    [Fact]
    public void BreedFillsTheTailWithFreshRandomGenomes()
    {
        var (parents, scores) = Scored(200, 2);
        var children = new Population(Shape, 200);
        new Breeder(200).Breed(parents, scores, children, EvolutionParams.From(new EvolutionSettings(), false), 1234, Bias);
        var expected = new float[Shape.WeightCount];
        var rng = new Xoshiro128StarStar(0);
        for (int c = 196; c < 200; c++)
        {
            rng.Reseed(SeedHash.Derive(1234, (ulong)c));
            Genome.Randomize(expected, Shape, Bias, rng);
            Assert.True(Population.BitEquals(expected, children.Genome(c)));
        }
    }

    [Fact]
    public void OffspringWithoutCrossoverOrMutationAreCopiesOfTournamentWinners()
    {
        var (parents, scores) = Scored(50, 3);
        var children = new Population(Shape, 50);
        var settings = new EvolutionSettings { CrossoverRate = 0, MutationChance = 0, RandomFraction = 0 };
        var breeder = new Breeder(50);
        breeder.Breed(parents, scores, children, EvolutionParams.From(settings, false), 8, Bias);
        var rng = new Xoshiro128StarStar(0);
        for (int c = 3; c < 50; c++) // 5 % of 50 = 2.5 → 3 elites
        {
            rng.Reseed(SeedHash.Derive(8, (ulong)c));
            int parent = breeder.Order[Selection.Tournament(rng, 50, 3)];
            Assert.True(Population.BitEquals(parents.Genome(parent), children.Genome(c)));
        }
    }

    [Fact]
    public void BreedIsDeterministicAndSeedDependent()
    {
        var (parents, scores) = Scored(120, 4);
        var p = EvolutionParams.From(new EvolutionSettings(), false);
        var x = new Population(Shape, 120);
        var y = new Population(Shape, 120);
        var z = new Population(Shape, 120);
        new Breeder(120).Breed(parents, scores, x, p, 10, Bias);
        new Breeder(120).Breed(parents, scores, y, p, 10, Bias);
        new Breeder(120).Breed(parents, scores, z, p, 11, Bias);
        Assert.Equal(x.ContentHash(), y.ContentHash());
        Assert.NotEqual(x.ContentHash(), z.ContentHash());
    }

    [Fact]
    public void BreedRejectsMismatchedBuffers()
    {
        var (parents, scores) = Scored(10, 5);
        var breeder = new Breeder(10);
        var p = EvolutionParams.From(new EvolutionSettings(), false);
        Assert.Throws<ArgumentException>(() => breeder.Breed(parents, scores, parents, p, 1, Bias));
        Assert.Throws<ArgumentException>(() => breeder.Breed(parents, scores, new Population(Shape, 9), p, 1, Bias));
        Assert.Throws<ArgumentException>(() => breeder.Breed(parents, scores, new Population(new BrainShape(22, 4, 0, 2), 10), p, 1, Bias));
    }
}
