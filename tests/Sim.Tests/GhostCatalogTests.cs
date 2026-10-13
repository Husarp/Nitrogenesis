using Nitrogenesis.Sim.History;
using Nitrogenesis.Sim.Io;
using Nitrogenesis.Sim.Map;
using Nitrogenesis.Sim.Racing;

/// <summary>Which saved generations can race a track as ghosts (PLAN §6.5): those of sessions and segments with its track hash.</summary>
public sealed class GhostCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ng-ghosts-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static string HashOf(string file) => TrackHash.Compute(TestTracks.Load(file));

    private SessionStore Store() => new(_root);

    private static void Train(SessionStore store, string name, string track, int generations, DateTime savedAt, Action<SessionDriver>? more = null)
    {
        using var d = SessionDriver.New(track, population: 12);
        d.Steps(generations);
        more?.Invoke(d);
        var folder = store.Create(name);
        folder.Save(d.Session.PrepareSave(savedAt));
    }

    [Fact]
    public void OnlySessionsOfTheTracksHashAreListedNewestGenerationFirst()
    {
        var store = Store();
        Train(store, "sprint run", "sprint.track", 4, new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc));
        Train(store, "curve run", "s_curve.track", 3, new DateTime(2026, 10, 2, 10, 0, 0, DateTimeKind.Utc));
        Train(store, "later sprint", "sprint.track", 2, new DateTime(2026, 10, 3, 10, 0, 0, DateTimeKind.Utc));

        var sprint = GhostCatalog.Scan(store, HashOf("sprint.track"));
        Assert.Equal(["later sprint", "later sprint", "sprint run", "sprint run", "sprint run", "sprint run"], sprint.Select(g => g.SessionName)); // newest save first
        Assert.Equal([1, 0, 3, 2, 1, 0], sprint.Select(g => g.Generation));
        Assert.All(sprint, g =>
        {
            Assert.Equal("main", g.Branch);
            Assert.Equal(new RacingSettings(), g.Physics);
            Assert.True(g.DecodeRecording().Count > 1);
        });
        Assert.Equal(3, GhostCatalog.Scan(store, HashOf("s_curve.track")).Count);
        Assert.Empty(GhostCatalog.Scan(store, HashOf("hairpins.track")));
        Assert.Empty(GhostCatalog.Scan(store, new string('0', 64)));
    }

    [Fact]
    public void TheTracksNameDoesNotMatterOnlyItsContent()
    {
        var store = Store();
        Train(store, "sprint run", "sprint.track", 2, DateTime.UtcNow);
        var renamed = TestTracks.Load("sprint.track");
        renamed.Name = "My copy of sprint";
        Assert.Equal(2, GhostCatalog.Scan(store, TrackHash.Compute(renamed)).Count);
        renamed.Grid[40, 40] = renamed.Grid[40, 40] == CellType.Wall ? CellType.Road : CellType.Wall; // one cell changed: another track
        Assert.Empty(GhostCatalog.Scan(store, TrackHash.Compute(renamed)));
    }

    [Fact]
    public void EachGenerationKeepsThePhysicsOfItsSegment()
    {
        var store = Store();
        Train(store, "tuned", "sprint.track", 2, DateTime.UtcNow, d =>
        {
            d.ChangeSettings(new RacingSettings { MaxSpeed = 24f });
            d.Steps(2);
        });
        var ghosts = GhostCatalog.Scan(store, HashOf("sprint.track"));
        Assert.Equal([3, 2, 1, 0], ghosts.Select(g => g.Generation));
        Assert.Equal([24f, 24f, 18f, 18f], ghosts.Select(g => g.Physics.MaxSpeed));
        Assert.Equal([1, 1, 0, 0], ghosts.Select(g => g.Segment));
        Assert.True(GhostPhysics.Choose([ghosts[0], ghosts[3]]).Mismatch);
        Assert.False(GhostPhysics.Choose([ghosts[0], ghosts[1]]).Mismatch);
    }

    [Fact]
    public void ASegmentOnAnotherTrackIsLeftOut()
    {
        var store = Store();
        Train(store, "moved", "sprint.track", 2, DateTime.UtcNow, d =>
        {
            var current = d.Session.Current;
            var segment = d.Session.SegmentAt(current.Branch, current.Generation);
            using var other = new RacingTraining(TestTracks.Load("s_curve.track"), new RacingSettings(), 7, d.Session.Evolution, current.Population.Size,
                SessionDriver.Threads, segment.Config.Brain.Hidden1, segment.Config.Brain.Hidden2, current.Population, current.Generation, current.Params);
            d.Session.StartSegment(other.Config, other.Track.Track, current.Population.Size);
            var record = other.Runner.RunGeneration();
            d.Session.AddGeneration(record, other.Runner.Population, other.Runner.PopulationParams);
        });
        Assert.Equal([1, 0], GhostCatalog.Scan(store, HashOf("sprint.track")).Select(g => g.Generation));
        Assert.Equal([2], GhostCatalog.Scan(store, HashOf("s_curve.track")).Select(g => g.Generation));
    }

    [Fact]
    public void ForkedBranchesAreListedAfterMainWithoutRepeatingTheirParentsGenerations()
    {
        var store = Store();
        Train(store, "forked", "sprint.track", 4, DateTime.UtcNow, d =>
        {
            d.ForkBest("main", 2);
            d.Steps(2);
        });
        var ghosts = GhostCatalog.Scan(store, HashOf("sprint.track"));
        Assert.Equal(["main", "main", "main", "main", "fork-1", "fork-1"], ghosts.Select(g => g.Branch));
        Assert.Equal([3, 2, 1, 0, 3, 2], ghosts.Select(g => g.Generation));
        Assert.Equal(ghosts.Count, ghosts.Select(g => (g.Branch, g.Generation)).Distinct().Count());
        Assert.Equal("gen 3 [fork-1] · forked", ghosts[4].Label);
        Assert.Equal("gen 3 · forked", ghosts[0].Label);
    }

    [Fact]
    public void AFinishedGenerationKnowsItsTimeAndAnUnfinishedOneDoesNot()
    {
        var store = Store();
        Train(store, "learner", "sprint.track", 6, DateTime.UtcNow);
        var ghosts = GhostCatalog.Scan(store, HashOf("sprint.track"));
        Assert.Contains(ghosts, g => g.Finished);
        Assert.Contains(ghosts, g => !g.Finished);
        Assert.All(ghosts, g =>
        {
            Assert.Equal(g.Finished, float.IsFinite(g.FinishTicks));
            if (g.Finished) Assert.True(g.FinishSeconds > 5f);
        });
        // The stats and the entry agree (finished = at least one car finished).
        var folderSession = Session.Load(new SessionFolder(Path.Combine(_root, "learner")));
        foreach (var g in ghosts)
        {
            var e = folderSession.EntryAt(g.Branch, g.Generation)!;
            Assert.Equal(e.Stats.FinishedCount > 0, g.Finished);
            if (g.Finished) Assert.Equal(e.Stats.BestFinishTicks, g.FinishTicks);
        }
    }

    [Fact]
    public void UnreadableSessionsAreSkippedAndTheRestIsListed()
    {
        var store = Store();
        Train(store, "good", "sprint.track", 2, DateTime.UtcNow);
        Directory.CreateDirectory(Path.Combine(_root, "broken"));
        File.WriteAllText(Path.Combine(_root, "broken", SessionFolder.HeaderFileName), "{ nope");
        Assert.Equal(2, GhostCatalog.Scan(store, HashOf("sprint.track")).Count);
        Assert.Empty(GhostCatalog.Scan(new SessionStore(Path.Combine(_root, "nothing here")), HashOf("sprint.track")));
    }

    [Fact]
    public void ScannedGhostsRaceOnTheirTrackWithTheirOwnTimes()
    {
        var store = Store();
        Train(store, "learner", "sprint.track", 6, DateTime.UtcNow);
        var ghosts = GhostCatalog.Scan(store, HashOf("sprint.track"));
        var finisher = ghosts.First(g => g.Finished);
        var other = ghosts.First(g => !g.Finished);
        var race = Race.Create(TestTracks.Load("sprint.track"), [finisher, other]);
        Assert.False(race.Physics.Mismatch);
        Assert.Equal(1f, race.Ghosts[0].Progress);
        Assert.True(race.Ghosts[0].SplitTick(2) > 0 && race.Ghosts[0].SplitTick(2) <= race.Ghosts[0].LastTick);
        Assert.InRange(race.Ghosts[1].Progress, 0f, 0.999f);
        race.Restart(0);
        for (int t = 0; t < 300; t++) race.Tick(1, 0, 0f, 0f, 0f);
        for (int g = 0; g < 2; g++)
        {
            race.GhostPose(g, 0.5f, out float x, out float y, out float a);
            Assert.True(float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(a));
        }
        var results = race.Standings();
        Assert.Equal(3, results.Count);
        Assert.Equal(finisher.FinishSeconds, results.Single(s => s.Ghost == 0).Seconds);
    }
}
