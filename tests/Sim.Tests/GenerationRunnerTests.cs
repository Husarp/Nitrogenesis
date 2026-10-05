using Nitrogenesis.Sim.Core;
using Nitrogenesis.Sim.Evolution;
using Nitrogenesis.Sim.History;
using Nitrogenesis.Sim.Racing;

public class GenerationRunnerTests
{
    private static RacingTraining Sprint(ulong seed, int threads = 1, int population = 200, EvolutionSettings? evolution = null) =>
        new(TestTracks.Load("sprint.track"), new RacingSettings(), seed, evolution, population, threads);

    /// <summary>Everything a record holds, as text with exact bit patterns, for equality checks.</summary>
    private static string Describe(GenerationRecord r) =>
        $"{r.Generation} {r.Seed:X} {r.Params} {r.Stats} " +
        $"{Convert.ToHexString(System.Runtime.InteropServices.MemoryMarshal.AsBytes(r.BestGenome.Weights.AsSpan()))} " +
        $"{Convert.ToHexString(r.BestRecording.Encode())} " +
        $"{(r.PopulationSnapshot is null ? "-" : new Population(r.BestGenome.Shape, r.PopulationSnapshot).ContentHash().ToString("X"))}";

    [Fact]
    public void GenerationZeroSanity()
    {
        // PLAN §9: at least 20 % of generation-0 cars move more than 5 cells.
        using var t = Sprint(seed: 1);
        t.Runner.BeginGeneration();
        while (!t.Runner.RunTicks(int.MaxValue)) { }
        var start = t.Track.Track.Start;
        int moved = 0;
        for (int i = 0; i < t.Mode.AgentCount; i++)
        {
            var c = t.Mode.Car(i);
            float dx = c.X - start.X, dy = c.Y - start.Y;
            if (MathF.Sqrt(dx * dx + dy * dy) > 5f) moved++;
        }
        Assert.True(moved >= 0.2 * t.Mode.AgentCount, $"only {moved} of {t.Mode.AgentCount} cars moved more than 5 cells");
    }

    [Fact]
    public void ElitismKeepsTheBestGenomeUnchanged()
    {
        using var t = Sprint(seed: 2);
        var before = t.Runner.Population;
        var record = t.Runner.RunGeneration();
        Assert.Equal(record.Stats.BestScore, t.Mode.Score(record.Stats.BestIndex));
        Assert.True(Population.BitEquals(before.Genome(record.Stats.BestIndex), record.BestGenome.Weights));
        // The best genome is child 0 of the next generation, bit for bit, and scores the same again.
        Assert.Equal(1, t.Runner.Generation);
        Assert.True(Population.BitEquals(t.Runner.Population.Genome(0), record.BestGenome.Weights));
        var next = t.Runner.RunGeneration();
        Assert.Equal(record.Stats.BestScore, t.Mode.Score(0));
        Assert.True(next.Stats.BestScore >= record.Stats.BestScore);
    }

    [Fact]
    public void ResultsAreBitIdenticalForOneAndSevenThreads()
    {
        string Run(int threads)
        {
            using var t = Sprint(seed: 3, threads);
            var lines = new List<string>();
            for (int g = 0; g < 3; g++) lines.Add(Describe(t.Runner.RunGeneration()));
            lines.Add(t.Runner.Population.ContentHash().ToString("X16"));
            return string.Join("\n", lines);
        }
        Assert.Equal(Run(1), Run(7));
    }

    [Fact]
    public void SameSeedTwiceIsIdenticalAndAnotherSeedIsNot()
    {
        ulong Hash(ulong seed)
        {
            using var t = Sprint(seed, population: 40);
            t.Runner.RunGeneration();
            t.Runner.RunGeneration();
            return t.Runner.Population.ContentHash();
        }
        Assert.Equal(Hash(4), Hash(4));
        Assert.NotEqual(Hash(4), Hash(5));
    }

    /// <summary>
    /// The population after 3 generations on sprint.track with seed 12345 and default settings (PLAN §9). It
    /// covers the Gaussian, initialisation, the brain, the racing sim, ranking and breeding at once. If it changes,
    /// one of those changed: bump SimVersion if the tick math changed, and update this constant on purpose.
    /// </summary>
    public const ulong ExpectedPopulationHashAfter3Generations = 0x0307CFF1F139A13FUL;

    [Fact]
    public void PopulationHashAfterThreeGenerationsIsPinned()
    {
        using var t = Sprint(seed: 12345, threads: 4);
        for (int g = 0; g < 3; g++) t.Runner.RunGeneration();
        Assert.Equal($"0x{ExpectedPopulationHashAfter3Generations:X16}", $"0x{t.Runner.Population.ContentHash():X16}");
    }

    [Fact]
    public void RecordHoldsSeedParamsStatsAndTheBestCarsRecording()
    {
        using var t = Sprint(seed: 6, population: 60);
        var r = t.Runner.RunGeneration();
        Assert.Equal(0, r.Generation);
        Assert.Equal(t.Runner.GenerationSeed(0), r.Seed);
        Assert.Equal(EvolutionParams.From(new EvolutionSettings(), false), r.Params);
        Assert.NotNull(r.PopulationSnapshot);
        Assert.Equal(60 * t.Shape.WeightCount, r.PopulationSnapshot!.Length);

        int best = r.Stats.BestIndex;
        for (int i = 0; i < 60; i++) Assert.True(t.Mode.Score(i) <= r.Stats.BestScore);
        Assert.Equal(Enumerable.Range(0, 60).Count(i => t.Mode.Status(i) == AgentStatus.Finished), r.Stats.FinishedCount);
        Assert.Equal((float)Enumerable.Range(0, 60).Average(i => (double)t.Mode.Score(i)), r.Stats.AverageScore);

        // One sample per tick plus the start; the last one is where the best car ended.
        Assert.Equal(t.Mode.Tick(best) + 1, r.BestRecording.Count);
        var end = t.Mode.Car(best);
        Assert.Equal(end.X, r.BestRecording.X(r.BestRecording.Count - 1), 1f / 256);
        Assert.Equal(end.Y, r.BestRecording.Y(r.BestRecording.Count - 1), 1f / 256);
        Assert.Equal(end.Heading, r.BestRecording.Heading(r.BestRecording.Count - 1));
        Assert.Equal(t.Track.Track.Start.X, r.BestRecording.X(0), 1f / 256);
    }

    [Fact]
    public void SettingsChangesApplyAtTheNextBoundaryAndAreRecorded()
    {
        using var t = Sprint(seed: 7, population: 30);
        var changed = new EvolutionSettings { MutationSigma = 0.5f, MutationChance = 0.3f };
        t.Runner.BeginGeneration();
        t.Runner.RunTicks(100);
        t.Runner.Settings = changed; // mid-generation
        while (!t.Runner.RunTicks(int.MaxValue)) { }
        var r0 = t.Runner.EndGeneration();
        Assert.Equal(0.2f, r0.Params.MutationSigma); // generation 0 was made before the change
        var r1 = t.Runner.RunGeneration();
        Assert.Equal(EvolutionParams.From(changed, false), r1.Params);
    }

    [Fact]
    public void StagnationBoostReachesTheRecords()
    {
        // Sprint finishes in generation 0 and quickly stops improving by 0.2 % per 5 generations.
        using var t = Sprint(seed: 8, population: 40, evolution: new EvolutionSettings { StagnationWindow = 5 });
        var boosted = new List<int>();
        for (int g = 0; g < 20; g++)
            if (t.Runner.RunGeneration().Params.Boost) boosted.Add(g);
        Assert.NotEmpty(boosted);
        Assert.Equal(boosted.Count, boosted.Select(g => g).Distinct().Count());
    }

    [Fact]
    public void SnapshotsArePublishedPerRunTicksCall()
    {
        using var t = Sprint(seed: 9, population: 20);
        var prev = new AgentSnapshot(20);
        var curr = new AgentSnapshot(20);
        t.Runner.BeginGeneration();
        t.Runner.RunTicks(10);
        t.Runner.RunTicks(10);
        t.Runner.Snapshots.CopyTo(prev, curr);
        Assert.Equal(10, prev.Tick);
        Assert.Equal(20, curr.Tick);
        Assert.Equal(0, curr.GenerationId);
        while (!t.Runner.RunTicks(int.MaxValue)) { }
        t.Runner.EndGeneration();
        t.Runner.BeginGeneration();
        t.Runner.Snapshots.CopyTo(prev, curr);
        Assert.Equal(1, curr.GenerationId);
        Assert.Equal(0, curr.Tick);
    }

    [Fact]
    public void RunTicksDoesNotAllocate()
    {
        using var t = Sprint(seed: 10, population: 100);
        t.Runner.RunGeneration(); // warm-up (JIT)
        t.Runner.BeginGeneration();
        t.Runner.RunTicks(5);
        Allocations.AssertSteadyStateFree(() => t.Runner.RunTicks(60));
        Assert.True(t.Runner.Snapshots.Version > 3 && t.Runner.ElapsedTicks <= 5 + 5 * 60);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void ElapsedTicksIsTheLastCarsStopTick(int threads)
    {
        using var t = Sprint(seed: 1, threads, population: 60);
        for (int g = 0; g < 2; g++)
        {
            t.Runner.BeginGeneration();
            while (!t.Runner.RunTicks(int.MaxValue)) { }
            int last = Enumerable.Range(0, t.Mode.AgentCount).Max(t.Mode.Tick);
            Assert.Equal(last, t.Runner.ElapsedTicks);
            Assert.True(t.Runner.ElapsedTicks <= t.TimeLimitTicks);
            var prev = new AgentSnapshot(60);
            var curr = new AgentSnapshot(60);
            t.Runner.Snapshots.CopyTo(prev, curr);
            Assert.Equal(last, curr.Tick);
            t.Runner.EndGeneration();
        }
    }

    [Fact]
    public void ResumesFromAGivenPopulation()
    {
        using var a = Sprint(seed: 11, population: 30);
        a.Runner.RunGeneration();
        var copy = new Population(a.Shape, (float[])a.Runner.Population.Weights.Clone());
        var boostParams = a.Runner.RunGeneration().Params with { Boost = true };
        string Next(GenerationRunner r) => Describe(r.RunGeneration());

        // A second runner started from generation 1's population continues exactly like the first.
        using var b = Sprint(seed: 11, population: 30);
        using var resumed = new GenerationRunner(b.Mode, b.Shape, RacingSettings.InitialOutputBias(), 11, new EvolutionSettings(),
            population: copy, generation: 1, populationParams: boostParams);
        var first = resumed.RunGeneration();
        Assert.Equal(1, first.Generation);
        Assert.Equal(boostParams, first.Params);
        Assert.Equal(Next(a.Runner), Next(resumed));
    }

    [Fact]
    public void RejectsAMismatchedBrain()
    {
        using var t = Sprint(seed: 12, population: 4);
        Assert.Throws<ArgumentException>(() => new GenerationRunner(t.Mode, new Nitrogenesis.Sim.Brain.BrainShape(21, 12, 0, 2),
            RacingSettings.InitialOutputBias(), 1, new EvolutionSettings()));
        Assert.Throws<InvalidOperationException>(() => t.Runner.RunTicks(1));
        Assert.Throws<InvalidOperationException>(() => t.Runner.EndGeneration());
    }
}
