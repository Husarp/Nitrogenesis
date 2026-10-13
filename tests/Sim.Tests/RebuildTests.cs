using Nitrogenesis.Sim.Evolution;
using Nitrogenesis.Sim.History;
using Nitrogenesis.Sim.Io;
using Nitrogenesis.Sim.Racing;

/// <summary>PLAN §5: any generation can be rebuilt deterministically, so every generation is forkable even after thinning.</summary>
public class RebuildTests
{
    private static void AssertSame(float[] expected, Population actual, string what) =>
        Assert.True(Population.BitEquals(expected, actual.Weights), $"{what}: the rebuilt population differs");

    [Fact]
    public void RebuildEqualsTheOriginalBitForBitAlsoAfterThinning()
    {
        using var d = SessionDriver.New(population: 24, evolution: new EvolutionSettings { StagnationWindow = 5 });
        d.Steps(40);
        var originals = SessionCompare.Snapshots(d.Session);
        Assert.Equal(40, originals.Count);
        Assert.Contains(d.Session.History.Find("main")!.Entries, e => e.Params.Boost); // the boost is part of what is rebuilt

        long perEntry = d.Session.History.Bytes - d.Session.History.SnapshotBytes;
        d.Session.SetMemoryLimit(perEntry + 5 * originals[("main", 0)].Length * 4L);
        Assert.InRange(d.Session.History.SnapshotCount, 1, 5);
        SessionCompare.AssertSnapshotsMatch(d.Session, originals);

        foreach (int g in new[] { 0, 1, 7, 13, 24, 38, 39 })
        {
            bool thinned = d.Session.History.Get("main", g)!.Snapshot is null;
            Assert.Equal(thinned, Rebuilder.StepsNeeded(d.Session, "main", g) > 0);
            AssertSame(originals[("main", g)], Rebuilder.Rebuild(d.Session, "main", g, threads: 3), $"generation {g}");
        }
        // The current population (generation 40, not evaluated yet) is at hand.
        Assert.Equal(0, Rebuilder.StepsNeeded(d.Session, "main", 40));
        Assert.Equal(d.Runner.PopulationHash, Rebuilder.Rebuild(d.Session, "main", 40).ContentHash());
    }

    [Fact]
    public void GenerationZeroIsRebuiltFromTheSeed()
    {
        using var d = SessionDriver.New(population: 16);
        d.Steps(3);
        var original = d.Session.History.Get("main", 0)!.Snapshot!;
        d.Session.SetMemoryLimit(0);
        Assert.Null(d.Session.History.Get("main", 1)!.Snapshot);
        // Generation 0's snapshot stays (a multiple of every spacing), but the seed alone rebuilds it too.
        var copy = d.Session.History.Find("main")!;
        copy.Replace(0, copy.Entries[0].WithoutSnapshot());
        AssertSame(original, Rebuilder.Rebuild(d.Session, "main", 0), "generation 0");
    }

    [Fact]
    public void RebuildCrossesASegmentChange()
    {
        using var d = SessionDriver.New(population: 20);
        d.Steps(10);
        d.ChangeSettings(new RacingSettings { MaxSpeed = 22f, TurnRateDeg = 240f });
        d.Steps(10);
        Assert.Equal(2, d.Session.Segments.Count);
        var originals = SessionCompare.Snapshots(d.Session);
        d.Session.SetMemoryLimit(0);
        Assert.Null(d.Session.History.Get("main", 13)!.Snapshot);
        foreach (int g in new[] { 9, 10, 11, 19 })
            AssertSame(originals[("main", g)], Rebuilder.Rebuild(d.Session, "main", g), $"generation {g}");
    }

    [Fact]
    public void AGenerationEndedEarlyIsRebuiltWithItsOwnTicks()
    {
        using var d = SessionDriver.New(population: 20);
        d.Steps(3);
        var early = d.StepEndedEarly(140); // "Next generation" after 140 ticks
        Assert.Equal(140, early.Ticks);
        var instant = d.StepEndedEarly(0); // pressed before any tick ran
        Assert.Equal(0, instant.Ticks);
        d.Steps(3);
        var originals = SessionCompare.Snapshots(d.Session);
        d.Session.SetMemoryLimit(0);
        AssertSame(originals[("main", 7)], Rebuilder.Rebuild(d.Session, "main", 7), "generation 7");
    }

    [Fact]
    public void FullForkFromAThinnedGenerationStartsFromItsExactPopulation()
    {
        // M3 acceptance: "fork from a thinned generation works".
        using var d = SessionDriver.New(population: 24);
        d.Steps(30);
        var originals = SessionCompare.Snapshots(d.Session);
        d.Session.SetMemoryLimit(0);
        Assert.Null(d.Session.History.Get("main", 17)!.Snapshot);
        Assert.True(Rebuilder.StepsNeeded(d.Session, "main", 17) > 10);

        var fork = d.ForkFull("main", 17);
        d.Session.SetMemoryLimit(HistoryStore.DefaultMemoryLimit);
        Assert.Equal(("fork-1", "main", 17, ForkKind.Full), (fork.Name, fork.Parent, fork.FirstGeneration, fork.Kind));
        Assert.Equal("fork-1", d.Session.ActiveBranch);
        Assert.Equal(17, d.Runner.Generation);
        AssertSame(originals[("main", 17)], d.Session.Current.Population, "fork start");

        // The fork re-evaluates generation 17 exactly as main did, then goes its own way (its own seed).
        var first = d.Step();
        Assert.True(HistoryEntry.SameStats(d.Session.History.Get("main", 17)!.Stats, first.Stats));
        d.Steps(5);
        Assert.NotEqual(d.Session.History.Get("main", 19)!.Stats, d.Session.History.Get("fork-1", 19)!.Stats);
        Assert.Equal([new LineagePart("main", 0, 17), new LineagePart("fork-1", 17, 23)], d.Session.Lineage("fork-1"));

        // Rebuilding a thinned fork generation walks back through the fork's first generation into main.
        var forkOriginals = SessionCompare.Snapshots(d.Session);
        d.Session.SetMemoryLimit(0);
        AssertSame(forkOriginals[("fork-1", 21)], Rebuilder.Rebuild(d.Session, "fork-1", 21), "fork generation 21");
        AssertSame(forkOriginals[("fork-1", 17)], Rebuilder.Rebuild(d.Session, "fork-1", 17), "fork generation 17");
    }

    [Fact]
    public void BestOnlyForkIsTheBestGenomePlusMutatedCopiesAndIsRebuilt()
    {
        using var d = SessionDriver.New(population: 24);
        d.Steps(12);
        var best = d.Session.History.Get("main", 9)!.BestGenome;
        // (memory limit stays at the default until the end)
        var fork = d.ForkBest("main", 9);
        Assert.Equal(ForkKind.BestOnly, fork.Kind);
        Assert.Equal(EvolutionParams.From(d.Session.Evolution, false), fork.ForkParams);
        var population = d.Session.Current.Population;
        Assert.True(Population.BitEquals(best.Weights, population.Genome(0)));
        for (int i = 1; i < population.Size; i++) Assert.False(Population.BitEquals(best.Weights, population.Genome(i)), $"genome {i} is not mutated");
        float[] start = (float[])population.Weights.Clone();

        d.Steps(6);
        var originals = SessionCompare.Snapshots(d.Session);
        d.Session.SetMemoryLimit(0);
        // Make the root snapshot go too, so it must come from main's best genome.
        var history = d.Session.History.Find(fork.Name)!;
        history.Replace(0, history.Entries[0].WithoutSnapshot());
        AssertSame(start, Rebuilder.Rebuild(d.Session, fork.Name, 9), "best-only fork start");
        AssertSame(originals[(fork.Name, 14)], Rebuilder.Rebuild(d.Session, fork.Name, 14), "best-only fork generation 14");
    }

    [Fact]
    public void ForkOfAForkIsRebuiltThroughBothLevels()
    {
        using var d = SessionDriver.New(population: 20);
        d.Steps(8);
        d.ForkFull("main", 5);
        d.Steps(6); // fork-1: 5…10
        d.ForkBest("fork-1", 8);
        d.Steps(4); // fork-2: 8…11
        d.ForkFull("fork-2", 10);
        d.Steps(3); // fork-3: 10…12
        Assert.Equal([new LineagePart("main", 0, 5), new LineagePart("fork-1", 5, 8), new LineagePart("fork-2", 8, 10), new LineagePart("fork-3", 10, 13)],
            d.Session.Lineage("fork-3"));
        var originals = SessionCompare.Snapshots(d.Session);
        d.Session.SetMemoryLimit(0);
        AssertSame(originals[("fork-3", 12)], Rebuilder.Rebuild(d.Session, "fork-3", 12), "fork-3 generation 12");
        AssertSame(originals[("fork-1", 7)], Rebuilder.Rebuild(d.Session, "fork-3", 7), "generation 7 in fork-3's lineage");
        Assert.Equal("fork-1", d.Session.OwnerOf("fork-3", 7));
        Assert.Equal("main", d.Session.OwnerOf("fork-3", 4));
    }

    [Fact]
    public void ARebuildThatDisagreesWithTheRecordThrows()
    {
        using var d = SessionDriver.New(population: 16);
        d.Steps(6);
        d.Session.SetMemoryLimit(0);
        var history = d.Session.History.Find("main")!;
        var e = history.Entries[2];
        history.Replace(2, new HistoryEntry
        {
            Generation = e.Generation, Seed = e.Seed, Params = e.Params, Ticks = e.Ticks, BestGenome = e.BestGenome,
            RecordingBytes = e.RecordingBytes, Stats = e.Stats with { AverageScore = e.Stats.AverageScore + 1e-6f },
        });
        var ex = Assert.Throws<InvalidOperationException>(() => Rebuilder.Rebuild(d.Session, "main", 5));
        Assert.Contains("not deterministic", ex.Message);
    }

    [Fact]
    public void RebuildingCanBeCancelled()
    {
        using var d = SessionDriver.New(population: 16);
        d.Steps(8);
        d.Session.SetMemoryLimit(0);
        using var cancel = new CancellationTokenSource();
        int calls = 0;
        Assert.Throws<OperationCanceledException>(() => Rebuilder.Rebuild(d.Session, "main", 7, 1, (done, total) =>
        {
            calls++;
            if (done == 2) cancel.Cancel();
        }, cancel.Token));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void ASessionOfAnotherSimVersionCannotBeRebuiltForkedOrResumed()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ng-rebuild-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var d = SessionDriver.New(population: 12))
            {
                d.Steps(4);
                d.Save(new SessionFolder(dir));
            }
            string path = Path.Combine(dir, SessionFolder.HeaderFileName);
            File.WriteAllText(path, File.ReadAllText(path).Replace($"\"simVersion\": {Nitrogenesis.Sim.SimInfo.SimVersion},", $"\"simVersion\": {Nitrogenesis.Sim.SimInfo.SimVersion - 1},"));
            var s = Session.Load(new SessionFolder(dir));
            Assert.True(s.ReadOnly);
            Assert.Equal(4, s.History.Find("main")!.Count); // recordings still play
            Assert.NotNull(s.History.Get("main", 3)!.DecodeRecording());
            Assert.Throws<InvalidOperationException>(() => Rebuilder.Rebuild(s, "main", 2));
            Assert.Throws<InvalidOperationException>(() => s.CreateFork("main", 2, ForkKind.BestOnly));
            Assert.Throws<InvalidOperationException>(() => RacingTraining.ResumeFrom(s, 1));
            Assert.Throws<InvalidOperationException>(() => s.PrepareSave(DateTime.UtcNow));
            Assert.Throws<ArgumentException>(() => new TrainingHost(s, new TrainingHostOptions()));
            Assert.Contains("older physics version", s.Compatibility.Message);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
