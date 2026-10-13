using Nitrogenesis.Sim.Core;
using Nitrogenesis.Sim.Evolution;
using Nitrogenesis.Sim.History;
using Nitrogenesis.Sim.Racing;

/// <summary>M3 Stage B helpers: the graph data, segment dividers, recording playback and the whole-generation replay.</summary>
public sealed class TrainingControlTests
{
    // ---------------------------------------------------------------- ScoreGraph

    [Fact]
    public void GraphKeepsOneSlotPerGenerationWithGapsAsNaN()
    {
        var g = new ScoreGraph();
        int v = g.Version;
        g.Set(0, 0.5f, 0.1f);
        g.Set(3, 2.4f, 0.9f); // 1 and 2 missing (a damaged history file)
        Assert.Equal(4, g.Count);
        Assert.True(float.IsNaN(g.Best(1)) && float.IsNaN(g.Average(2)));
        Assert.True(g.Version > v);
        g.Set(1000, 1f, 1f); // grows
        Assert.Equal(1001, g.Count);
        Assert.Equal(2.4f, g.Best(3));
    }

    [Fact]
    public void GraphSampleIsOnePointPerGenerationOrBucketsWithMaxBestAndMeanAverage()
    {
        var g = new ScoreGraph();
        for (int i = 0; i < 10; i++) g.Set(i, i, -i);
        var best = new float[16];
        var avg = new float[16];
        int n = g.Sample(best, avg, out float low, out float high);
        Assert.Equal(10, n);
        Assert.Equal(new float[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }, best[..10]);
        Assert.Equal((-9f, 9f), (low, high));

        n = g.Sample(best.AsSpan(0, 4), avg.AsSpan(0, 4), out low, out high);
        Assert.Equal(4, n);
        // Buckets [0,2) [2,5) [5,7) [7,10).
        Assert.Equal(new float[] { 1, 4, 6, 9 }, best[..4]);
        Assert.Equal(new float[] { -0.5f, -3f, -5.5f, -8f }, avg[..4]);

        g.Set(12, float.NaN, float.NaN);
        n = g.Sample(best, avg, out _, out _);
        Assert.Equal(13, n);
        Assert.True(float.IsNaN(best[11]) && float.IsNaN(avg[10]));

        var empty = new ScoreGraph();
        Assert.Equal(0, empty.Sample(best, avg, out low, out high));
        Assert.True(float.IsNaN(low) && float.IsNaN(high));
    }

    [Fact]
    public void GraphSampleDoesNotAllocate()
    {
        var g = new ScoreGraph();
        for (int i = 0; i < 5000; i++) g.Set(i, i * 0.001f, i * 0.0005f);
        g.SetDividers([100, 2000]);
        var best = new float[300];
        var avg = new float[300];
        var dividers = new int[8];
        Allocations.AssertSteadyStateFree(() =>
        {
            g.Sample(best, avg, out _, out _);
            g.CopyDividers(dividers);
        });
        Assert.Equal(2, g.CopyDividers(dividers));
        Assert.Equal(2000, dividers[1]);
    }

    // ---------------------------------------------------------------- segment dividers

    [Fact]
    public void SegmentRunStartsAreWhereTheConfigHashChangesAlongTheLineage()
    {
        using var d = SessionDriver.New(population: 16);
        d.Steps(4);
        Assert.Empty(d.Session.SegmentRunStarts("main"));
        d.ChangeSettings(new RacingSettings { MaxSpeed = 20f });
        Assert.Equal([4], d.Session.SegmentRunStarts("main")); // the current generation starts the new run
        d.Steps(3);
        d.ChangeSettings(new RacingSettings()); // back to the first config: a third run
        d.Steps(2);
        Assert.Equal([4, 7], d.Session.SegmentRunStarts("main"));

        // A fork inherits its parent's dividers up to the fork point; same config at the fork: no divider there.
        d.ForkFull("main", 5);
        Assert.Equal([4], d.Session.SegmentRunStarts("fork-1"));
        d.Steps(1);
        d.ChangeSettings(new RacingSettings { Walls = WallMode.Bounce });
        d.Steps(1);
        Assert.Equal([4, 6], d.Session.SegmentRunStarts("fork-1"));
        Assert.Equal([4, 7], d.Session.SegmentRunStarts("main"));
    }

    // ---------------------------------------------------------------- RecordingPlayback

    private static Recording Line(int samples)
    {
        var r = new Recording();
        for (int i = 0; i < samples; i++) r.Add(10f + i * 0.25f, 5f, 0);
        return r;
    }

    [Fact]
    public void PlaybackRunsOneSamplePerTickAndStopsAtTheLast()
    {
        var p = new RecordingPlayback(Line(200));
        Assert.Equal(0, p.Tick);
        for (int i = 0; i < 60; i++) p.Advance(1.0 / 60);
        Assert.InRange(p.Tick, 59, 60);
        p.SpeedIndex = SpeedController.IndexFor(100);
        p.Advance(1);
        Assert.True(p.Ended);
        Assert.Equal(199, p.Tick);
        p.Pose(out float x, out _, out _);
        Assert.Equal(10f + 199 * 0.25f, x);
        p.Restart();
        Assert.Equal(0, p.Tick);
        Assert.False(p.Ended);
    }

    [Fact]
    public void PlaybackInterpolatesAtSlowSpeedsAndHasNoMax()
    {
        var p = new RecordingPlayback(Line(100)) { SpeedIndex = SpeedController.IndexFor(0.25) };
        p.Advance(1.0 / 30); // 0.5 tick
        Assert.Equal(0, p.Tick);
        p.Pose(out float x, out float y, out _);
        Assert.Equal(10f + 0.125f, x, 4);
        Assert.Equal(5f, y);

        p.SpeedIndex = SpeedController.MaxIndex;
        Assert.Equal(RecordingPlayback.MaxSpeedIndex, p.SpeedIndex);
        Assert.Equal(100, p.TargetSpeed);
    }

    [Fact]
    public void PlaybackPauseHoldsAndStepAdvancesOneTick()
    {
        var p = new RecordingPlayback(Line(100));
        p.Advance(0.5);
        int at = p.Tick;
        p.Paused = true;
        p.Advance(1);
        Assert.Equal(at, p.Tick);
        p.StepOnce();
        p.Advance(1.0 / 60);
        Assert.Equal(at + 1, p.Tick);
    }

    // ---------------------------------------------------------------- GenerationReplay

    private static ReplayStats WaitReplay(GenerationReplay r, Func<ReplayStats, bool> until, bool frames = true, double seconds = 60)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            if (frames)
            {
                r.Frame(1.0 / 60);
                r.WaitForFrame(TimeSpan.FromSeconds(5));
            }
            else
            {
                Thread.Sleep(2);
            }
            r.ReadStats(out var s);
            if (until(s)) return s;
            Assert.Null(s.Error);
            Assert.True(DateTime.UtcNow < deadline, "timed out");
        }
    }

    [Fact]
    public void WatchingAWholeThinnedGenerationReproducesItsRecord()
    {
        using var d = SessionDriver.New(population: 24);
        d.Steps(12);
        d.Session.SetMemoryLimit(0);
        Assert.Null(d.Session.History.Get("main", 7)!.Snapshot);
        var record = d.Session.History.Get("main", 7)!;

        using var r = new GenerationReplay(d.Session, "main", 7, threads: 2, rebuildThreads: 3, speed: double.PositiveInfinity);
        var s = WaitReplay(r, x => x.Ended);
        Assert.True(s.MatchesRecord);
        Assert.Null(s.Error);
        Assert.Equal(record.Ticks, s.ElapsedTicks);
        Assert.Equal(record.Stats.FinishedCount, s.Finished);
        Assert.Equal(24, s.PopulationSize);

        // Restart: from tick 0 again, and the same end.
        r.Restart();
        s = WaitReplay(r, x => !x.Ended && x.ElapsedTicks == 0, frames: false);
        s = WaitReplay(r, x => x.Ended);
        Assert.True(s.MatchesRecord);
    }

    [Fact]
    public void AGenerationEndedEarlyIsWatchedToTheSameTick()
    {
        using var d = SessionDriver.New(population: 20);
        d.Steps(2);
        d.StepEndedEarly(140);
        d.Steps(1);
        using var r = new GenerationReplay(d.Session, "main", 2, threads: 2, speed: 100);
        var s = WaitReplay(r, x => x.Ended);
        Assert.Equal(140, s.ElapsedTicks);
        Assert.True(s.MatchesRecord);
        Assert.True(s.Alive > 0); // cars were still running when it was scored
    }

    [Fact]
    public void ReplayAtOneXRunsOneTickPerFrameAndPausesAndSteps()
    {
        using var d = SessionDriver.New(population: 16);
        d.Steps(2);
        using var r = new GenerationReplay(d.Session, "main", 1, threads: 1);
        WaitReplay(r, x => x.Ready, frames: false);
        var s = WaitReplay(r, x => x.ElapsedTicks >= 30);
        Assert.InRange(s.ElapsedTicks, 30, 31);
        Assert.True(s.Interpolates);
        r.TogglePause();
        for (int i = 0; i < 5; i++)
        {
            r.Frame(1.0 / 60);
            r.WaitForFrame(TimeSpan.FromSeconds(1));
        }
        r.ReadStats(out var paused);
        r.StepTick();
        s = WaitReplay(r, x => x.ElapsedTicks == paused.ElapsedTicks + 1);
        Assert.True(s.Paused);
    }

    [Fact]
    public void ReplayOfAGenerationNotEvaluatedFailsWithAMessage()
    {
        using var d = SessionDriver.New(population: 16);
        d.Steps(2);
        using var r = new GenerationReplay(d.Session, "main", 5, threads: 1);
        var s = WaitReplay(r, x => x.Error is not null, frames: false);
        Assert.Contains("not been evaluated", s.Error);
        Assert.False(s.Ready);
    }

    // ---------------------------------------------------------------- host: graph, pause, train-to threads

    private static TrainingStats WaitHost(TrainingHost host, Func<TrainingStats, bool> until, bool frames = false)
    {
        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (true)
        {
            if (frames)
            {
                host.Frame(1.0 / 60);
                host.WaitForFrame(TimeSpan.FromSeconds(5));
            }
            else
            {
                Thread.Sleep(2);
            }
            host.ReadStats(out var s);
            Assert.Null(s.Error);
            if (s.Ready && until(s)) return s;
            Assert.True(DateTime.UtcNow < deadline, "timed out");
        }
    }

    [Fact]
    public void TheHostGraphFollowsTheActiveLineageWithDividers()
    {
        var options = new TrainingHostOptions { Threads = 2, HeadlessThreads = 3, PopulationSize = 20, Seed = 5, Speed = double.PositiveInfinity };
        using var host = new TrainingHost(TestTracks.Load("sprint.track"), options);
        var s = WaitHost(host, _ => true);
        int runs = s.TrainToRuns;
        host.TrainTo(TrainToCondition.ToGeneration(8));
        s = WaitHost(host, x => x.TrainToRuns > runs && !x.Headless);

        var best = new float[64];
        var avg = new float[64];
        var dividers = new int[8];
        int n = host.CopyGraph(best, avg, dividers, out int dividerCount, out int generations, out _, out float high);
        Assert.Equal(8, generations);
        Assert.Equal(8, n);
        Assert.Equal(0, dividerCount);
        var main = host.Session!.History.Find("main")!;
        for (int g = 0; g < 8; g++) Assert.Equal(main.Get(g)!.Stats.BestScore, best[g]);
        Assert.Equal(main.Entries.Max(e => e.Stats.BestScore), high);

        // A settings change: a divider at the generation that starts the new segment.
        int version = s.GraphVersion;
        host.ChangeSettings(new RacingSettings { MaxSpeed = 21f });
        s = WaitHost(host, x => x.Segment == 1, frames: true);
        host.CopyGraph(best, avg, dividers, out dividerCount, out _, out _, out _);
        Assert.Equal(1, dividerCount);
        Assert.Equal(host.Session.Segments[1].FirstGeneration, dividers[0]);
        Assert.True(s.GraphVersion > version);

        // A fork from generation 3: the graph shows the fork's lineage (generations 0…2 of main, then the fork).
        host.SetPaused(true);
        host.Fork("main", 3, ForkKind.Full);
        s = WaitHost(host, x => x.Branch == "fork-1");
        host.CopyGraph(best, avg, dividers, out dividerCount, out generations, out _, out _);
        Assert.Equal(3, generations);
        Assert.Equal(0, dividerCount);
        Assert.True(s.Paused);
    }

    [Fact]
    public void TrainToWithAThreadCountUsesItAndNoticesAreCounted()
    {
        var options = new TrainingHostOptions { Threads = 1, HeadlessThreads = 3, PopulationSize = 20, Seed = 5, Speed = 1 };
        using var host = new TrainingHost(TestTracks.Load("sprint.track"), options);
        var s = WaitHost(host, _ => true);
        int seen = 0;
        host.GenerationEnded += _ => seen = Math.Max(seen, host.Training!.Runner.Threads);
        host.TrainTo(TrainToCondition.ToGeneration(3), threads: 2);
        s = WaitHost(host, x => x.TrainToRuns == 1 && !x.Headless);
        Assert.Equal(2, seen);
        Assert.Equal(1, s.Threads);

        int notices = s.NoticeCount;
        host.TrainTo(TrainToCondition.ToGeneration(1));
        s = WaitHost(host, x => x.NoticeCount > notices);
        host.TrainTo(TrainToCondition.ToGeneration(1)); // the same text again still counts
        s = WaitHost(host, x => x.NoticeCount > notices + 1);
        Assert.Contains("Already", s.Notice);
    }
}
