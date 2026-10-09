using Nitrogenesis.Sim.Core;
using Nitrogenesis.Sim.Racing;

public class TrainingHostTests
{
    private static TrainingHost Host(double speed, ulong seed = 12345, int threads = 2, int population = 200) =>
        new(TestTracks.Load("sprint.track"), new TrainingHostOptions { Speed = speed, Seed = seed, Threads = threads, PopulationSize = population });

    private static void WaitReady(TrainingHost host)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        TrainingStats s;
        do
        {
            Thread.Sleep(1);
            host.ReadStats(out s);
            Assert.Null(s.Error);
        } while (!s.Ready && DateTime.UtcNow < deadline);
        Assert.True(s.Ready);
    }

    private static void Frames(TrainingHost host, int count, double frameSeconds = 1.0 / 60)
    {
        for (int f = 0; f < count; f++)
        {
            host.Frame(frameSeconds);
            Assert.True(host.WaitForFrame(TimeSpan.FromSeconds(10)));
        }
    }

    [Fact]
    public void RunsTheSameGenerationsAsTheRunnerDoes()
    {
        // The pinned population after 3 generations (GenerationRunnerTests), reached through frames at MAX.
        using var host = Host(double.PositiveInfinity, threads: 3);
        ulong? hash = null;
        host.GenerationEnded += r =>
        {
            if (r.Generation == 2) hash = host.Training!.Runner.Population.ContentHash();
        };
        WaitReady(host);
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (hash is null && DateTime.UtcNow < deadline) Frames(host, 1);
        Assert.Equal($"0x{GenerationRunnerTests.ExpectedPopulationHashAfter3Generations:X16}", $"0x{hash:X16}");
        host.ReadStats(out var s);
        Assert.True(s.Generation >= 3);
        Assert.True(s.BestTimeTicks < float.PositiveInfinity); // sprint finishes in generation 0 (NOTES M1)
    }

    [Fact]
    public void OneXRunsOneTickPerFrameAndPublishesEachTick()
    {
        using var host = Host(1);
        WaitReady(host);
        Frames(host, 30);
        host.ReadStats(out var s);
        Assert.Equal(30, s.ElapsedTicks);
        Assert.True(s.Interpolates);
        var prev = new AgentSnapshot(200);
        var curr = new AgentSnapshot(200);
        host.Snapshots!.CopyTo(prev, curr);
        Assert.Equal(30, curr.Tick);
        Assert.Equal(29, prev.Tick);
    }

    [Fact]
    public void TenXRunsTenTicksPerFrame()
    {
        using var host = Host(10);
        WaitReady(host);
        Frames(host, 6);
        host.ReadStats(out var s);
        Assert.False(s.Interpolates);
        Assert.InRange(s.TicksLastFrame, 1, 10);
        // A frame whose work overran the 14 ms budget (a busy test machine) leaves its rest due; frames without new
        // game time run it. Exactly 6 × 10 ticks were due, no more.
        for (int i = 0; i < 20 && s.ElapsedTicks < 60; i++)
        {
            Frames(host, 1, frameSeconds: 0);
            host.ReadStats(out s);
        }
        Assert.Equal(60, s.ElapsedTicks);
        Frames(host, 1, frameSeconds: 0);
        host.ReadStats(out s);
        Assert.Equal(60, s.ElapsedTicks);
    }

    [Fact]
    public void PauseStopsTheSimAndStepRunsOneTick()
    {
        using var host = Host(0.5);
        WaitReady(host);
        Frames(host, 10); // 5 ticks
        host.TogglePause();
        Frames(host, 10);
        host.ReadStats(out var s);
        Assert.Equal(5, s.ElapsedTicks);
        Assert.True(s.Paused);
        float alpha = s.Alpha;
        host.StepTick();
        Frames(host, 3);
        host.ReadStats(out s);
        Assert.Equal(6, s.ElapsedTicks);
        Assert.Equal(alpha, s.Alpha);
        Assert.Equal(0, s.ActualSpeed);
    }

    [Fact]
    public void PauseAtAFallingBehindSpeedRunsNoBacklog()
    {
        // 100× with 300 cars and a 0.2 s frame: far more ticks are due than one frame budget runs.
        using var host = Host(100, population: 300);
        WaitReady(host);
        Frames(host, 1, frameSeconds: 0.2);
        host.ReadStats(out var s);
        Assert.InRange(s.TotalTicks, 1, 1199); // behind: the rest is still owed
        host.TogglePause();
        Frames(host, 1);
        host.ReadStats(out s);
        long ticks = s.TotalTicks;
        Frames(host, 30);
        host.ReadStats(out s);
        Assert.Equal(ticks, s.TotalTicks);
        host.StepTick();
        Frames(host, 3);
        host.ReadStats(out s);
        Assert.Equal(ticks + 1, s.TotalTicks);
    }

    [Theory]
    [InlineData(-1, false, 0f, 4, 0.5f, 4)]   // no leader yet
    [InlineData(2, true, 0.495f, 4, 0.5f, 2)] // level: keep it
    [InlineData(2, true, 0.48f, 4, 0.5f, 4)]  // clearly behind: hand over
    [InlineData(2, false, 0.5f, 4, 0.5f, 4)]  // crashed: hand over
    [InlineData(2, false, 0f, -1, 0f, -1)]    // nobody on the track
    public void LeaderChangesOnlyOnAClearLead(int previous, bool onTrack, float previousScore, int best, float bestScore, int expected) =>
        Assert.Equal(expected, TrainingHost.ChooseLeader(previous, onTrack, previousScore, best, bestScore));

    [Fact]
    public void StatsDescribeTheRunningGeneration()
    {
        using var host = Host(10, population: 50);
        WaitReady(host);
        Frames(host, 30); // 300 ticks = 5 s
        host.ReadStats(out var s);
        Assert.Equal(50, s.PopulationSize);
        Assert.Equal(2, s.Threads);
        Assert.InRange(s.Alive, 0, 50);
        Assert.True(s.TimeLimitTicks > 0);
        Assert.InRange(s.BestProgress, 0.01f, 1f);
        Assert.InRange(s.Leader, -1, 49);
        Assert.True(s.TrainingSeconds > 0.4);
    }

    [Fact]
    public void WatchedCarRouteIsCopiedAlsoWhilePaused()
    {
        using var host = Host(1, population: 20);
        WaitReady(host);
        var x = new float[RouteTrails.MaxSamplesPerCar];
        var y = new float[RouteTrails.MaxSamplesPerCar];
        Assert.Equal(0, host.CopyTrail(x, y, out int agent, out _));
        Assert.Equal(-1, agent);
        host.Watch(3);
        Frames(host, 40);
        int count = host.CopyTrail(x, y, out agent, out int generation);
        Assert.Equal(3, agent);
        Assert.Equal(0, generation);
        var mode = host.Training!.Mode;
        Assert.Equal(mode.Trails!.Count(40), count);
        Assert.Equal((mode.Track.Track.Start.X, mode.Track.Track.Start.Y), (x[0], y[0]));
        var prev = new AgentSnapshot(20);
        var curr = new AgentSnapshot(20);
        host.Snapshots!.CopyTo(prev, curr);
        Assert.Equal((curr.X[3], curr.Y[3]), (x[count - 1], y[count - 1])); // 40 ticks: the last sample is the current pose

        // Paused: picking another car still brings its route at the next frame (no tick runs).
        host.TogglePause();
        host.Watch(7);
        host.Frame(1.0 / 60);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        do
        {
            Thread.Sleep(1);
            count = host.CopyTrail(x, y, out agent, out _);
        } while (agent != 7 && DateTime.UtcNow < deadline);
        Assert.Equal(7, agent);
        Assert.Equal(mode.Trails.Count(40), count);
        host.ReadStats(out var s);
        Assert.Equal(40, s.ElapsedTicks);

        host.Watch(-1);
        host.Frame(1.0 / 60);
        deadline = DateTime.UtcNow.AddSeconds(10);
        do
        {
            Thread.Sleep(1);
            count = host.CopyTrail(x, y, out agent, out _);
        } while (agent != -1 && DateTime.UtcNow < deadline);
        Assert.Equal((-1, 0), (agent, count));
    }

    [Fact]
    public void DisposeStopsTheSimulationPromptlyAtMax()
    {
        var host = Host(double.PositiveInfinity, threads: 3);
        WaitReady(host);
        for (int f = 0; f < 5; f++) host.Frame(1.0 / 60); // keeps the sim busy for many frames' budgets
        var watch = System.Diagnostics.Stopwatch.StartNew();
        host.Dispose();
        Assert.True(watch.ElapsedMilliseconds < 1000, $"Dispose took {watch.ElapsedMilliseconds} ms");
        host.ReadStats(out var s);
        Assert.Null(s.Error);
    }
}
