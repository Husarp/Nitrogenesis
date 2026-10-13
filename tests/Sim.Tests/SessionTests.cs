using Nitrogenesis.Sim.Evolution;
using Nitrogenesis.Sim.History;
using Nitrogenesis.Sim.Io;
using Nitrogenesis.Sim.Racing;

public sealed class SessionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ng-sess-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static readonly EvolutionSettings Boosty = new() { StagnationWindow = 5 };

    private static string Entries(Session s, string branch) =>
        string.Join("\n", s.History.Find(branch)!.Entries.Select(SessionCompare.Describe));

    [Fact]
    public void ASavedAndLoadedSessionContinuesBitIdentically()
    {
        // Uninterrupted: 16 generations (window 5 makes the stagnation boost come and go).
        using var a = SessionDriver.New(population: 24, evolution: Boosty);
        a.Steps(16);
        Assert.Contains(a.Session.History.Find("main")!.Entries, e => e.Params.Boost);

        // Saved after 8, loaded, resumed for 8 more.
        var folder = new SessionFolder(_dir);
        using (var b = SessionDriver.New(population: 24, evolution: Boosty))
        {
            b.Steps(8);
            b.Save(folder);
        }
        var loaded = Session.Load(folder);
        Assert.Empty(loaded.Warnings);
        Assert.False(loaded.HasUnsavedChanges);
        using var c = SessionDriver.Resume(loaded);
        Assert.Equal(8, c.Runner.Generation);
        c.Steps(8);
        Assert.Equal(Entries(a.Session, "main"), Entries(c.Session, "main"));
        Assert.Equal(a.Runner.PopulationHash, c.Runner.PopulationHash);
        Assert.Equal(SessionCompare.Fingerprint(a.Session), SessionCompare.Fingerprint(c.Session));
    }

    [Fact]
    public void ASaveInTheMiddleOfAGenerationRestartsItAndGivesTheSameResult()
    {
        using var a = SessionDriver.New(population: 20);
        a.Steps(4);
        var folder = new SessionFolder(_dir);
        a.Training.Runner.BeginGeneration();
        a.Training.Runner.RunTicks(150);
        a.Save(folder); // the population of generation 4 is saved, its partial run is not
        while (!a.Training.Runner.RunTicks(int.MaxValue)) { }
        var record = a.Training.Runner.EndGeneration();
        a.Session.AddGeneration(record, a.Training.Runner.Population, a.Training.Runner.PopulationParams);
        a.Steps(2);

        using var c = SessionDriver.Resume(Session.Load(folder));
        Assert.Equal(4, c.Runner.Generation);
        c.Steps(3);
        Assert.Equal(Entries(a.Session, "main"), Entries(c.Session, "main"));
    }

    [Fact]
    public void ForksSegmentsAndBranchesSurviveASaveAndLoad()
    {
        var folder = new SessionFolder(_dir);
        using var d = SessionDriver.New(population: 20);
        d.Steps(12);
        d.ChangeSettings(new RacingSettings { MaxSpeed = 21f });
        d.Steps(4);
        d.ForkFull("main", 14);
        d.Steps(3);
        d.ForkBest("main", 6);
        d.Steps(2);
        d.SetEvolution(new EvolutionSettings { MutationSigma = 0.33f });
        d.Save(folder);
        var loaded = Session.Load(folder);
        Assert.Equal(SessionCompare.Fingerprint(d.Session), SessionCompare.Fingerprint(loaded));
        Assert.Equal("fork-2", loaded.ActiveBranch);
        Assert.Equal(0.33f, loaded.Evolution.MutationSigma);

        // Both continue the same way.
        d.Steps(2);
        using var c = SessionDriver.Resume(loaded);
        c.Steps(2);
        Assert.Equal(SessionCompare.Fingerprint(d.Session), SessionCompare.Fingerprint(c.Session));
    }

    [Fact]
    public void LineageOwnershipAndSegmentRuns()
    {
        using var d = SessionDriver.New(population: 16);
        d.Steps(10);
        d.ChangeSettings(new RacingSettings { MaxSpeed = 20f }); // segment 1 from 10
        d.Steps(6);
        d.ChangeSettings(new RacingSettings()); // back to the defaults at 16: a new run, although the hash equals segment 0's
        d.Steps(3);
        var s = d.Session;
        Assert.Equal(0, s.SegmentRunStart("main", 9));
        Assert.Equal(10, s.SegmentRunStart("main", 15));
        Assert.Equal(16, s.SegmentRunStart("main", 18));
        Assert.Equal(s.Segments[0].Config.Hash, s.SegmentAt("main", 17).Config.Hash);
        Assert.Equal(2, s.SegmentRunStats("main", 18).Count);

        d.ForkFull("main", 12);
        d.Steps(2);
        // The fork shares segment 1's config, so its run (best time, stagnation, graph baseline) starts at 10 in main.
        Assert.Equal(10, s.SegmentRunStart("fork-1", 13));
        Assert.Equal(3, s.SegmentRunStats("fork-1", 13).Count);
        Assert.Equal([new LineagePart("main", 0, 12), new LineagePart("fork-1", 12, 14)], s.Lineage("fork-1"));
        Assert.Equal(Enumerable.Range(0, 14), s.LineageEntries("fork-1").Select(e => e.Generation));
        Assert.Same(s.History.Get("main", 11), s.EntryAt("fork-1", 11));
        Assert.Same(s.History.Get("fork-1", 12), s.EntryAt("fork-1", 12));
    }

    [Fact]
    public void ANewSegmentResetsStagnationAndBestTime()
    {
        using var d = SessionDriver.New(population: 20, evolution: Boosty);
        d.Steps(12);
        Assert.True(d.Session.SegmentRunStats("main", 12).Count == 12);
        d.ChangeSettings(new RacingSettings { MaxSpeed = 24f });
        Assert.Empty(d.Session.SegmentRunStats("main", 12));
        Assert.Equal(float.PositiveInfinity, d.Training.Runner.Stagnation.BestTime);
        Assert.Equal(0f, d.Training.Runner.Stagnation.BestProgress);

        // A generation that already ran keeps its segment: main made active again resumes at its stored generation 12.
        d.Step();
        d.ForkFull("main", 12);
        d.Activate("main");
        Assert.Equal(12, d.Runner.Generation);
        Assert.Throws<InvalidOperationException>(() => d.Session.StartSegment(d.Training.Config, d.Training.Track.Track, 20));
    }

    [Fact]
    public void SwitchingBackToABranchContinuesItExactly()
    {
        // Uninterrupted main.
        using var a = SessionDriver.New(population: 20, evolution: Boosty);
        a.Steps(14);

        // Main for 10, a fork trained for a while, then back to main for 4: main's generations are the same.
        using var b = SessionDriver.New(population: 20, evolution: Boosty);
        b.Steps(10);
        b.ForkFull("main", 6);
        b.Steps(5);
        b.Activate("main");
        Assert.Equal(("main", 9), (b.Session.ActiveBranch, b.Runner.Generation)); // its last stored generation, evaluated again
        b.Steps(5); // 9 (again, not stored twice), 10…13
        Assert.Equal(Entries(a.Session, "main"), Entries(b.Session, "main"));
        Assert.Equal(a.Runner.PopulationHash, b.Runner.PopulationHash);
    }

    [Fact]
    public void ADifferentResultForAStoredGenerationIsRefused()
    {
        using var d = SessionDriver.New(population: 16);
        d.Steps(3);
        d.Activate("main"); // re-evaluates generation 2
        var record = d.Training.Runner.RunGeneration();
        var tampered = new GenerationRecord
        {
            Generation = record.Generation, Seed = record.Seed, Params = record.Params, BestGenome = record.BestGenome,
            BestRecording = record.BestRecording, Ticks = record.Ticks, Stats = record.Stats with { BestScore = record.Stats.BestScore + 1 },
        };
        Assert.Throws<InvalidOperationException>(() => d.Session.AddGeneration(tampered, d.Training.Runner.Population, d.Training.Runner.PopulationParams));
        d.Session.AddGeneration(record, d.Training.Runner.Population, d.Training.Runner.PopulationParams);
        Assert.Equal(3, d.Session.History.Find("main")!.Count);
        Assert.Throws<ArgumentException>(() => d.Session.AddGeneration(record, d.Training.Runner.Population, d.Training.Runner.PopulationParams));
    }

    [Fact]
    public void ForksOnlyFromEvaluatedGenerationsWithUniqueNamesAndSeeds()
    {
        using var d = SessionDriver.New(population: 16);
        d.Steps(5);
        Assert.Throws<ArgumentException>(() => d.Session.CreateFork("main", 5, ForkKind.BestOnly)); // generation 5 has not run
        var f1 = d.ForkBest("main", 2);
        var f2 = d.ForkBest("main", 2);
        Assert.Equal(("fork-1", "fork-2"), (f1.Name, f2.Name));
        Assert.NotEqual(f1.Seed, f2.Seed);
        Assert.NotEqual(d.Session.Branch("main").Seed, f1.Seed);
    }

    [Fact]
    public void ASaveWritesOnlyNewChunksAndThinnedOnes()
    {
        var folder = new SessionFolder(_dir);
        using var d = SessionDriver.New(population: 16);
        d.Steps(30);
        d.Save(folder);
        string branch = folder.BranchFolder("main");
        Assert.Equal(["gen-0-24.bin", "gen-25-29.bin"], Directory.GetFiles(branch).Select(f => Path.GetFileName(f)!).Order());
        string full = Path.Combine(branch, "gen-0-24.bin");
        var stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(full, stamp);

        d.Steps(25); // up to 54
        var plan = d.Session.PrepareSave(DateTime.UtcNow);
        Assert.Equal([1, 2], plan.Windows.Select(w => w.Window)); // window 0 is complete and unchanged
        folder.Save(plan);
        d.Session.MarkSaved(plan);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(full));
        Assert.Equal(["gen-0-24.bin", "gen-25-49.bin", "gen-50-54.bin"], Directory.GetFiles(branch).Select(f => Path.GetFileName(f)!).Order());

        // Nothing new: nothing to write but the header.
        Assert.False(d.Session.HasUnsavedChanges);
        Assert.Empty(d.Session.PrepareSave(DateTime.UtcNow).Windows);

        // Thinning makes the saved windows dirty: they are rewritten without the dropped snapshots.
        long before = new FileInfo(full).Length;
        d.Session.SetMemoryLimit(0);
        Assert.True(d.Session.HasUnsavedChanges);
        plan = d.Session.PrepareSave(DateTime.UtcNow);
        Assert.Equal([0, 1, 2], plan.Windows.Select(w => w.Window));
        folder.Save(plan);
        d.Session.MarkSaved(plan);
        Assert.True(new FileInfo(full).Length < before / 5);
        var loaded = Session.Load(folder);
        Assert.Equal(SessionCompare.Fingerprint(d.Session), SessionCompare.Fingerprint(loaded));
        Assert.Equal(1, loaded.History.SnapshotCount); // generation 0
    }

    [Fact]
    public void ThinningWhileASaveIsBeingWrittenIsRewrittenByTheNextSave()
    {
        var folder = new SessionFolder(_dir);
        using var d = SessionDriver.New(population: 16);
        d.Steps(30);
        var plan = d.Session.PrepareSave(DateTime.UtcNow); // the saver thread holds it with all 30 snapshots
        d.Session.SetMemoryLimit(0); // training thins meanwhile
        folder.Save(plan);
        d.Session.MarkSaved(plan);

        Assert.True(d.Session.HasUnsavedChanges);
        plan = d.Session.PrepareSave(DateTime.UtcNow);
        Assert.Equal([0, 1], plan.Windows.Select(w => w.Window)); // the windows that save wrote, now without the snapshots
        folder.Save(plan);
        d.Session.MarkSaved(plan);
        var loaded = Session.Load(folder);
        Assert.Equal(SessionCompare.Fingerprint(d.Session), SessionCompare.Fingerprint(loaded));
        Assert.Equal(1, loaded.History.SnapshotCount);
    }

    [Fact]
    public void ALoadedSessionIsThinnedToItsLimitAndTheChunksAreRewritten()
    {
        var folder = new SessionFolder(_dir);
        using (var d = SessionDriver.New(population: 16))
        {
            d.Steps(40);
            d.Save(folder);
        }
        var loaded = Session.Load(folder, memoryLimit: 0);
        Assert.Equal(1, loaded.History.SnapshotCount);
        Assert.True(loaded.HasUnsavedChanges);
        var plan = loaded.PrepareSave(DateTime.UtcNow);
        Assert.Equal([0, 1], plan.Windows.Select(w => w.Window));
        folder.Save(plan);
        loaded.MarkSaved(plan);
        Assert.Equal(1, Session.Load(folder).History.SnapshotCount);
    }

    [Fact]
    public void TheMemoryLimitHoldsDuringTraining()
    {
        using var d = SessionDriver.New(population: 16, memoryLimit: 600_000);
        long snapshot = 16L * d.Training.Shape.WeightCount * 4;
        for (int g = 0; g < 60; g++)
        {
            d.Step();
            Assert.True(d.Session.History.Bytes <= 600_000 || d.Session.History.SnapshotCount <= 1, $"{d.Session.History.Bytes} B after generation {g}");
        }
        Assert.Equal(60, d.Session.History.Find("main")!.Count); // every generation is still there
        Assert.True(d.Session.History.SnapshotCount < 600_000 / snapshot + 1);
    }
}
