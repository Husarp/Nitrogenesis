using Nitrogenesis.Sim;
using Nitrogenesis.Sim.History;
using Nitrogenesis.Sim.Io;
using Nitrogenesis.Sim.Racing;

/// <summary>"Watch whole generation" (PLAN §5): re-simulating a stored generation reproduces its record bit for bit.</summary>
public sealed class GenerationReplayTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ng-replay-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    /// <summary>Plays the replay at MAX until it ends or fails (frames of the render thread, simulated here).</summary>
    private static ReplayStats Watch(GenerationReplay replay, double seconds = 60)
    {
        replay.SetSpeed(double.PositiveInfinity);
        var end = DateTime.UtcNow.AddSeconds(seconds);
        ReplayStats s;
        do
        {
            replay.Frame(0.016);
            replay.WaitForFrame(TimeSpan.FromSeconds(5));
            replay.ReadStats(out s);
            if (!s.Ready && s.Error is null) Thread.Sleep(5);
        } while (!s.Ended && s.Error is null && DateTime.UtcNow < end);
        return s;
    }

    [Fact]
    public void ResimulatingAStoredGenerationReproducesItsRecord()
    {
        using var d = SessionDriver.New(population: 24);
        d.Steps(6);
        foreach (int g in new[] { 0, 3, 5 })
        {
            using var replay = new GenerationReplay(d.Session, "main", g, threads: 2);
            var s = Watch(replay);
            var entry = d.Session.EntryAt("main", g)!;
            Assert.Null(s.Error);
            Assert.True(s.Ended);
            Assert.True(s.MatchesRecord, $"generation {g}");
            Assert.Equal(entry.Ticks, (int)s.ElapsedTicks);
            Assert.Equal(entry.Stats.FinishedCount, s.Finished);
            Assert.Equal(entry.Stats, s.Record);
            Assert.Equal(24, s.PopulationSize);
        }
    }

    [Fact]
    public void AThinnedGenerationIsRebuiltFirstAndStillMatches()
    {
        using var d = SessionDriver.New(population: 20);
        d.Steps(12);
        d.Session.SetMemoryLimit(0);
        Assert.Null(d.Session.History.Get("main", 7)!.Snapshot);
        using var replay = new GenerationReplay(d.Session, "main", 7, threads: 2);
        var s = Watch(replay);
        Assert.Null(s.Error);
        Assert.True(s.MatchesRecord);
    }

    [Fact]
    public void ARestartPlaysTheGenerationAgainWithTheSameResult()
    {
        using var d = SessionDriver.New(population: 16);
        d.Steps(3);
        using var replay = new GenerationReplay(d.Session, "main", 2, threads: 2);
        Assert.True(Watch(replay).MatchesRecord);
        replay.TogglePause();
        replay.Restart();
        ReplayStats mid;
        var end = DateTime.UtcNow.AddSeconds(10);
        do
        {
            Thread.Sleep(5);
            replay.ReadStats(out mid);
        } while (mid.Ended && DateTime.UtcNow < end);
        Assert.False(mid.Ended);
        Assert.Equal(0, mid.ElapsedTicks);
        replay.TogglePause();
        var again = Watch(replay);
        Assert.True(again.MatchesRecord);
    }

    [Fact]
    public void AGenerationThatWasNotEvaluatedIsAnError()
    {
        using var d = SessionDriver.New(population: 16);
        d.Steps(2);
        using var replay = new GenerationReplay(d.Session, "main", 2, threads: 2); // generation 2 is still running
        var s = Watch(replay, 10);
        Assert.NotNull(s.Error);
        Assert.False(s.Ready);
    }

    [Fact]
    public void RefusesASessionOfAnotherSimVersionButItsRecordingsStillPlay()
    {
        using (var d = SessionDriver.New(population: 16))
        {
            d.Steps(3);
            d.Save(new SessionFolder(_dir));
        }
        var folder = new SessionFolder(_dir);
        string json = File.ReadAllText(folder.HeaderPath);
        string old = $"\"simVersion\": {SimInfo.SimVersion}";
        Assert.Contains(old, json);
        File.WriteAllText(folder.HeaderPath, json.Replace(old, "\"simVersion\": 1"));
        File.Delete(folder.BackupHeaderPath);

        var loaded = Session.Load(folder);
        Assert.True(loaded.ReadOnly);
        Assert.False(loaded.Compatibility.CanResimulate);
        var ex = Assert.Throws<InvalidOperationException>(() => new GenerationReplay(loaded, "main", 1, threads: 1));
        Assert.Equal(loaded.Compatibility.Message, ex.Message);

        // "Watch best" still works: the stored recording plays to its end.
        var entry = loaded.EntryAt("main", 1)!;
        var play = new RecordingPlayback(entry.DecodeRecording()!) { SpeedIndex = RecordingPlayback.MaxSpeedIndex };
        for (int i = 0; i < 100000 && !play.Ended; i++) play.Advance(0.05);
        Assert.True(play.Ended);
    }
}
