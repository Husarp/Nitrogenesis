using Nitrogenesis.Sim.Brain;
using Nitrogenesis.Sim.Core;
using Nitrogenesis.Sim.Evolution;
using Nitrogenesis.Sim.History;
using Nitrogenesis.Sim.Io;
using Nitrogenesis.Sim.Racing;

/// <summary>M7: "cars per generation" as a new-segment setting (PLAN §6.4), the family-colour parents, and the host's live settings.</summary>
public sealed class PopulationSizeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ng-popsize-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static void AssertSame(float[] expected, Population actual, string what) =>
        Assert.True(Population.BitEquals(expected, actual.Weights), $"{what}: the rebuilt population differs");

    // ---------------------------------------------------------------- PopulationResize

    [Fact]
    public void ASmallerPopulationKeepsTheFirstGenomesAndABiggerOneAddsMutatedCopies()
    {
        var shape = BrainShape.Default(22, 2);
        var source = new Population(shape, 10);
        source.Randomize(3, RacingSettings.InitialOutputBias());
        var p = EvolutionParams.From(new EvolutionSettings(), boost: false);

        var smaller = PopulationResize.To(source, 6, p, 99);
        Assert.Equal(6, smaller.Size);
        Assert.True(Population.BitEquals(source.Weights.AsSpan(0, 6 * shape.WeightCount), smaller.Weights));

        var bigger = PopulationResize.To(source, 25, p, 99);
        Assert.Equal(25, bigger.Size);
        Assert.True(Population.BitEquals(source.Weights, bigger.Weights.AsSpan(0, source.Weights.Length)));
        for (int c = 10; c < 25; c++)
        {
            var original = source.Genome((c - 10) % 10);
            int same = 0;
            for (int i = 0; i < shape.WeightCount; i++) if (bigger.Genome(c)[i] == original[i]) same++;
            Assert.InRange(same, shape.WeightCount / 2, shape.WeightCount - 1); // mostly the parent, with some mutated weights
        }

        Assert.True(Population.BitEquals(bigger.Weights, PopulationResize.To(source, 25, p, 99).Weights)); // deterministic
        Assert.False(Population.BitEquals(bigger.Weights, PopulationResize.To(source, 25, p, 100).Weights));
        Assert.True(Population.BitEquals(source.Weights, PopulationResize.To(source, 10, p, 1).Weights)); // same size: a copy
    }

    // ---------------------------------------------------------------- Breeder.Parents

    [Fact]
    public void TheBreederRecordsWhoBredEachChild()
    {
        var shape = BrainShape.Default(22, 2);
        var parents = new Population(shape, 40);
        parents.Randomize(1, RacingSettings.InitialOutputBias());
        var children = new Population(shape, 40);
        var scores = Enumerable.Range(0, 40).Select(i => (float)((i * 7) % 40)).ToArray();
        var breeder = new Breeder(40);
        var p = EvolutionParams.From(new EvolutionSettings { ElitismFraction = 0.1f, RandomFraction = 0.1f }, boost: false);
        breeder.Breed(parents, scores, children, p, 5, RacingSettings.InitialOutputBias());

        Assert.Equal(4, breeder.EliteCount);
        var recorded = breeder.Parents.ToArray();
        for (int c = 0; c < 4; c++)
        {
            Assert.Equal(breeder.Order[c], recorded[c]);
            Assert.True(Population.BitEquals(parents.Genome(recorded[c]), children.Genome(c)));
        }
        for (int c = 4; c < 36; c++) Assert.InRange(recorded[c], 0, 39);
        for (int c = 36; c < 40; c++) Assert.Equal(-1, recorded[c]);
    }

    // ---------------------------------------------------------------- the session with a resized segment

    [Fact]
    public void ARebuildCrossesASegmentThatChangedTheNumberOfCars()
    {
        using var d = SessionDriver.New(population: 20);
        d.Steps(8);
        d.ChangeSettings(new RacingSettings(), 12); // fewer cars, same physics: still a new segment
        Assert.Equal(2, d.Session.Segments.Count);
        Assert.Equal(12, d.Session.Segments[1].PopulationSize);
        Assert.Equal(12, d.Session.Current.Population.Size);
        d.Steps(6);
        d.ChangeSettings(new RacingSettings { MaxSpeed = 20f }, 31); // more cars and other physics
        Assert.Equal(3, d.Session.Segments.Count);
        d.Steps(6);
        // Another number of cars starts a new run (divider, best-ever and stagnation restart) like another config: a resumed
        // training replays the stagnation from there, as the live one started it afresh.
        Assert.Equal(new[] { 8, 14 }, d.Session.SegmentRunStarts("main"));
        Assert.Equal(8, d.Session.SegmentRunStart("main", 12));

        var originals = SessionCompare.Snapshots(d.Session);
        Assert.Equal(20, originals[("main", 7)].Length / d.Training.Shape.WeightCount);
        Assert.Equal(12, originals[("main", 8)].Length / d.Training.Shape.WeightCount);
        Assert.Equal(31, originals[("main", 14)].Length / d.Training.Shape.WeightCount);

        d.Session.SetMemoryLimit(0); // thin everything that may go
        foreach (int g in new[] { 3, 7, 8, 9, 13, 14, 15, 19 })
        {
            Population rebuilt = Rebuilder.Rebuild(d.Session, "main", g, threads: 3);
            AssertSame(originals[("main", g)], rebuilt, $"generation {g}");
        }
        // The current (not yet evaluated) generation 20.
        Assert.Equal(31, Rebuilder.Rebuild(d.Session, "main", 20).Size);
    }

    [Fact]
    public void ASavedSessionWithResizedSegmentsLoadsRebuildsAndForks()
    {
        var folder = new SessionFolder(Path.Combine(_root, "s"));
        Dictionary<(string, int), float[]> originals;
        string fingerprint;
        using (var d = SessionDriver.New(population: 20))
        {
            d.Steps(5);
            d.ChangeSettings(new RacingSettings(), 14);
            d.Steps(5);
            d.ChangeSettings(new RacingSettings(), 26);
            d.Steps(4);
            originals = SessionCompare.Snapshots(d.Session);
            fingerprint = SessionCompare.Fingerprint(d.Session);
            d.Save(folder);
        }

        var loaded = Session.Load(folder);
        Assert.Equal(fingerprint, SessionCompare.Fingerprint(loaded));
        Assert.Equal(new[] { 20, 14, 26 }, loaded.Segments.Select(s => s.PopulationSize).ToArray());
        loaded.SetMemoryLimit(0);
        foreach (int g in new[] { 4, 5, 9, 10, 13 })
            AssertSame(originals[("main", g)], Rebuilder.Rebuild(loaded, "main", g), $"generation {g} after loading");

        using var resumed = SessionDriver.Resume(loaded);
        Assert.Equal(26, resumed.Training.Runner.PopulationSize);
        var fork = resumed.ForkFull("main", 6); // inside the 14-car segment
        Assert.Equal(14, resumed.Session.Current.Population.Size);
        Assert.Equal(fork.Name, resumed.Session.ActiveBranch);
        resumed.Steps(2);
        var best = resumed.ForkBest("main", 11); // inside the 26-car segment
        Assert.Equal(26, resumed.Session.Current.Population.Size);
        resumed.Steps(1);
        Assert.NotNull(best);
    }

    [Fact]
    public void AFullForkFromTheGenerationThatChangedTheNumberOfCarsRebuildsAfterThinning()
    {
        using var d = SessionDriver.New(population: 20);
        d.Steps(5);
        d.ChangeSettings(new RacingSettings(), 30); // 10 mutated copies added at generation 5, with main's seed
        d.Steps(4);
        var fork = d.ForkFull("main", 5); // the fork's first population is main's generation 5
        float[] forked = (float[])d.Session.Current.Population.Weights.Clone();
        var mainOriginals = SessionCompare.Snapshots(d.Session);
        d.Activate("main"); // the fork is left before its first generation ran
        d.Session.SetMemoryLimit(0);
        d.Activate(fork.Name); // rebuilt from generation 0
        AssertSame(forked, d.Session.Current.Population, "the fork's first population");
        AssertSame(mainOriginals[("main", 5)], Rebuilder.Rebuild(d.Session, "main", 5, threads: 2), "main generation 5");
        d.Session.SetMemoryLimit(HistoryStore.DefaultMemoryLimit);
        d.Steps(4);
        var originals = SessionCompare.Snapshots(d.Session);
        d.Session.SetMemoryLimit(0);
        foreach (int g in new[] { 5, 6, 8 })
            AssertSame(originals[(fork.Name, g)], Rebuilder.Rebuild(d.Session, fork.Name, g, threads: 2), $"fork generation {g}");
    }

    [Fact]
    public void ASegmentThatNeverRanIsDroppedWhenItsBranchIsMadeActiveAgain()
    {
        using var d = SessionDriver.New(population: 20);
        d.Steps(6);
        d.ChangeSettings(new RacingSettings { MaxSpeed = 20f }, 12); // a segment at 6 that main leaves before generation 6 ends
        d.ForkBest("main", 3);
        d.Steps(2);
        d.Activate("main"); // resumes at 5 under segment 0: generation 6 runs there again with 20 cars
        Assert.DoesNotContain(d.Session.Segments, s => s.Branch == "main" && s.FirstGeneration == 6);
        d.Steps(4);
        Assert.Equal(20, d.Session.Current.Population.Size);
        var originals = SessionCompare.Snapshots(d.Session);
        d.Session.SetMemoryLimit(0);
        foreach (int g in new[] { 6, 7, 8 })
            AssertSame(originals[("main", g)], Rebuilder.Rebuild(d.Session, "main", g, threads: 2), $"generation {g}");
    }

    [Fact]
    public void ATrainingResumedAfterAResizeContinuesBitIdentically()
    {
        // Window 5: the stagnation boost comes and goes, so a resumed training must restart it where the live one did.
        var boosty = new EvolutionSettings { StagnationWindow = 5 };
        using var a = SessionDriver.New(population: 20, evolution: boosty);
        a.Steps(6);
        a.ChangeSettings(new RacingSettings(), 14); // same physics, fewer cars
        a.Steps(14);

        var folder = new SessionFolder(Path.Combine(_root, "r"));
        using (var b = SessionDriver.New(population: 20, evolution: boosty))
        {
            b.Steps(6);
            b.ChangeSettings(new RacingSettings(), 14);
            b.Steps(5);
            b.Save(folder);
        }
        using var c = SessionDriver.Resume(Session.Load(folder));
        c.Steps(9);
        Assert.Equal(SessionCompare.Fingerprint(a.Session), SessionCompare.Fingerprint(c.Session));
    }

    // ---------------------------------------------------------------- the host

    private static TrainingHostOptions Options(SessionFolder? folder = null) => new()
    {
        Threads = 2, HeadlessThreads = 3, PopulationSize = 24, Seed = 99, Speed = double.PositiveInfinity, Folder = folder,
        Evolution = new EvolutionSettings { StagnationWindow = 5 },
    };

    private static TrainingStats Wait(TrainingHost host, Func<TrainingStats, bool> until, double seconds = 120)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            host.Frame(1.0 / 60);
            host.WaitForFrame(TimeSpan.FromSeconds(5));
            host.ReadStats(out var s);
            Assert.Null(s.Error);
            if (s.Ready && until(s)) return s;
            Assert.True(DateTime.UtcNow < deadline, "timed out");
        }
    }

    [Fact]
    public void TheHostStartsASegmentWithAnotherNumberOfCarsAndKeepsTraining()
    {
        using var host = new TrainingHost(TestTracks.Load("sprint.track"), Options());
        Wait(host, s => s.Generation >= 2);
        host.ChangeSettings(new RacingSettings(), 10);
        var s = Wait(host, x => x.PopulationSize == 10 && x.Generation >= 6);
        Assert.Null(host.PendingChange); // applied at the boundary
        Assert.Equal(10, host.Training!.Mode.AgentCount);
        Assert.Equal(10, host.Snapshots!.Capacity);
        Assert.Equal(2, host.Session!.Segments.Count);
        Assert.Equal(10, host.Session.Segments[1].PopulationSize);
        Assert.Equal(1, s.Segment);

        host.ChangeSettings(new RacingSettings(), 40);
        s = Wait(host, x => x.PopulationSize == 40 && x.Generation >= s.Generation + 2);
        Assert.Equal(3, host.Session.Segments.Count);
        Assert.Equal(40, host.Training.Mode.AgentCount);
    }

    [Fact]
    public void LiveThreadAndHistoryLimitChangesApplyAtOnce()
    {
        using var host = new TrainingHost(TestTracks.Load("sprint.track"), Options());
        Wait(host, s => s.Generation >= 3);
        host.SetThreads(1, 2);
        var s = Wait(host, x => x.Threads == 1);
        Assert.Equal(1, s.Threads);
        host.SetThreads(3, 0);
        s = Wait(host, x => x.Threads == 3);
        Assert.Equal(3, s.Threads);

        host.SetHistoryLimit(0);
        s = Wait(host, x => x.HistoryLimit == 0);
        Assert.Equal(0, host.Session!.History.MemoryLimit);
        host.SetHistoryLimit(1L << 30);
        Wait(host, x => x.HistoryLimit == 1L << 30);
        Assert.Equal(1L << 30, host.Session.History.MemoryLimit);
    }

    [Fact]
    public void ANewTrainingCanStartWithAnotherBrainShape()
    {
        using var host = new TrainingHost(TestTracks.Load("sprint.track"), Options() with { Hidden1 = 16, Hidden2 = 8 });
        Wait(host, s => s.Generation >= 1);
        Assert.Equal((16, 8), (host.Training!.Shape.Hidden1, host.Training.Shape.Hidden2));
    }

    [Fact]
    public void TheWatchedCarsBrainAndSpeedAreCopiedForTheView()
    {
        using var host = new TrainingHost(TestTracks.Load("sprint.track"), Options() with { Speed = 1 });
        Wait(host, s => s.ElapsedTicks >= 5);
        host.Watch(3);
        var s = Wait(host, x => x.ElapsedTicks >= 40);
        var shape = host.Training!.Shape;
        var values = new float[BrainProbe.ValueCount(shape)];
        var weights = new float[shape.WeightCount];
        int n = host.CopyBrain(values, weights, out var copiedShape, out int agent, out int generation, out int version);
        Assert.Equal(values.Length, n);
        Assert.Equal((shape, 3), (copiedShape, agent));
        Assert.Equal(host.Training.Runner.Generation, generation);
        Assert.Equal(host.Training.Runner.Population.Genome(3).ToArray(), weights);
        Assert.All(values[^2..], v => Assert.InRange(v, -1f, 1f));
        Assert.True(s.MaxSpeed > 0);
        Assert.True(s.WatchedSpeed >= 0);

        host.Watch(-1);
        Wait(host, x => x.ElapsedTicks >= 60);
        Assert.Equal(0, host.CopyBrain(values, weights, out _, out agent, out _, out int version2));
        Assert.Equal(-1, agent);
        Assert.Equal(version, version2);
    }

    [Fact]
    public void TheParentsOfANewGenerationAreAvailableForTheFamilyColours()
    {
        using var host = new TrainingHost(TestTracks.Load("sprint.track"), Options());
        Wait(host, x => x.Generation >= 2);
        var parents = new int[24];
        int n = host.CopyParents(parents, out int generation, out int elites);
        Assert.Equal(24, n);
        Assert.True(generation >= 1);
        Assert.Equal(1, elites); // 5 % of 24 rounds to 1
        Assert.All(parents, p => Assert.InRange(p, -1, 23));
        Assert.Equal(0, host.CopyParents(new int[3], out _, out _)); // a too small array gets nothing
    }
}
