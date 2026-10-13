using Nitrogenesis.Sim.Evolution;
using Nitrogenesis.Sim.History;
using Nitrogenesis.Sim.Io;
using Nitrogenesis.Sim.Racing;

/// <summary>The training host with sessions (M3): saving, resuming, train-to, Step mode, next generation, segments, forks.</summary>
public sealed class TrainingHostSessionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ng-host-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private SessionFolder Folder(string name = "s") => new(Path.Combine(_root, name));

    private static TrainingHostOptions Options(SessionFolder? folder = null, double speed = double.PositiveInfinity, int population = 24) => new()
    {
        Threads = 2,
        HeadlessThreads = 3,
        PopulationSize = population,
        Seed = 99,
        Speed = speed,
        Folder = folder,
        Evolution = new EvolutionSettings { StagnationWindow = 5 },
    };

    private static TrainingStats Wait(TrainingHost host, Func<TrainingStats, bool> until, int frames = 0, double seconds = 120)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        TrainingStats s;
        while (true)
        {
            if (frames > 0)
            {
                host.Frame(1.0 / 60);
                host.WaitForFrame(TimeSpan.FromSeconds(5));
            }
            else
            {
                Thread.Sleep(2);
            }
            host.ReadStats(out s);
            Assert.Null(s.Error);
            if (s.Ready && until(s)) return s;
            Assert.True(DateTime.UtcNow < deadline, "timed out");
        }
    }

    private static TrainingStats TrainTo(TrainingHost host, TrainToCondition condition)
    {
        int runs = Wait(host, _ => true).TrainToRuns;
        host.TrainTo(condition);
        return Wait(host, s => s.TrainToRuns > runs && !s.Headless);
    }

    private static string Fingerprint(TrainingHost host) => SessionCompare.Fingerprint(host.Session!);

    [Fact]
    public void ANewSessionIsSavedAtOnceThenOnExitAndResumesExactly()
    {
        // Uninterrupted reference: 12 generations without a folder.
        string reference;
        using (var host = new TrainingHost(TestTracks.Load("sprint.track"), Options()))
        {
            var s = TrainTo(host, TrainToCondition.ToGeneration(12));
            Assert.Equal(12, s.Generation);
            reference = Fingerprint(host);
        }

        // Saved after 5, closed, opened again, trained to 12.
        var folder = Folder();
        using (var host = new TrainingHost(TestTracks.Load("sprint.track"), Options(folder)))
        {
            Wait(host, _ => true);
            Assert.True(new SessionFolder(folder.Path).Exists); // the new session is on disk from the start
            TrainTo(host, TrainToCondition.ToGeneration(5));
        }
        var loaded = Session.Load(folder);
        Assert.Equal(5, loaded.Current.Generation);
        Assert.True(loaded.TrainingSeconds >= 0);
        Assert.Equal(folder.LoadHeader().SavedAtUtc, loaded.LoadedSavedAtUtc);
        using (var host = new TrainingHost(loaded, Options(folder)))
        {
            // The "saved" indicator shows the save it was loaded from until this run saves (autosave every 2 min).
            Assert.Equal(loaded.LoadedSavedAtUtc, Wait(host, _ => true).SavedAtUtc);
            TrainTo(host, TrainToCondition.ToGeneration(12));
            Assert.Equal(reference, Fingerprint(host));
        }
        Assert.Equal(reference, SessionCompare.Fingerprint(Session.Load(folder)));
    }

    [Fact]
    public void TrainToStopsAtEachConditionWithTheHeadlessThreads()
    {
        using var host = new TrainingHost(TestTracks.Load("sprint.track"), Options(speed: 1));
        Wait(host, _ => true);
        int threadsSeen = 0;
        host.GenerationEnded += _ => threadsSeen = Math.Max(threadsSeen, host.Training!.Runner.Threads);

        var s = TrainTo(host, TrainToCondition.ToGeneration(4));
        Assert.Equal(4, s.Generation);
        Assert.Equal(3, threadsSeen);
        Assert.Equal(2, s.Threads); // back to the visual count
        Assert.True(s.GenerationsPerSecond >= 0);

        // "Until a car finishes" stops after the first generation with a finisher.
        s = TrainTo(host, TrainToCondition.UntilFinish());
        var main = host.Session!.History.Find("main")!;
        Assert.True(s.LastStats.FinishedCount > 0);
        Assert.Equal(s.Generation - 1, s.LastGeneration);
        Assert.All(main.Entries.Where(e => e.Generation >= 4 && e.Generation < s.LastGeneration), e => Assert.Equal(0, e.Stats.FinishedCount));
        int before = s.Generation;

        float best = s.LastStats.BestFinishTicks / 60f;
        s = TrainTo(host, TrainToCondition.UntilBestTimeBelow(best + 0.001f));
        Assert.Equal(before + 1, s.Generation); // the elites finish as fast again
        Assert.True(s.LastStats.BestFinishTicks / 60f < best + 0.001f);

        s = TrainTo(host, TrainToCondition.ToGeneration(3)); // already past: nothing to do
        Assert.NotNull(s.Notice);
        Assert.Equal(before + 1, s.Generation);
    }

    [Fact]
    public void TrainToCanBeCancelled()
    {
        using var host = new TrainingHost(TestTracks.Load("hairpins.track"), Options(population: 60));
        Wait(host, _ => true);
        host.TrainTo(TrainToCondition.ToGeneration(100_000));
        Wait(host, s => s.Headless && s.Generation >= 1);
        host.CancelTrainTo();
        var s = Wait(host, x => !x.Headless && x.TrainToRuns == 1);
        Assert.InRange(s.Generation, 1, 1000);
    }

    [Fact]
    public void ACancelBeforeTheQueuedTrainToStartsDropsIt()
    {
        // The scheduler thread is held inside a generation end while the request and its cancel are queued, so the
        // cancel lands before the train-to command runs (e.g. "keep training while watching" ticked and unticked at once).
        using var gate = new ManualResetEventSlim(false);
        using var held = new ManualResetEventSlim(false);
        int hold = 0;
        using var host = new TrainingHost(TestTracks.Load("sprint.track"), Options());
        host.GenerationEnded += _ =>
        {
            if (Interlocked.Exchange(ref hold, 0) == 1)
            {
                held.Set();
                gate.Wait();
            }
        };
        int runs = Wait(host, _ => true).TrainToRuns;
        Volatile.Write(ref hold, 1);
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!held.IsSet)
        {
            host.Frame(1.0 / 60);
            Thread.Sleep(1);
            Assert.True(DateTime.UtcNow < deadline, "timed out");
        }
        host.TrainTo(TrainToCondition.ToGeneration(100_000));
        host.CancelTrainTo();
        gate.Set();
        Wait(host, s => s.TrainToRuns == runs + 1, seconds: 20);
        Thread.Sleep(200);
        host.ReadStats(out var after);
        Assert.False(after.Headless);
        Assert.Equal(runs + 1, after.TrainToRuns);

        // A train-to queued after the cancel runs as usual.
        var s2 = TrainTo(host, TrainToCondition.ToGeneration(after.Generation + 2));
        Assert.Equal(runs + 2, s2.TrainToRuns);
    }

    [Fact]
    public void StepModePausesAtEachGenerationEndUntilNext()
    {
        using var host = new TrainingHost(TestTracks.Load("sprint.track"), Options());
        host.StepMode = true;
        var s = Wait(host, x => x.WaitingForNext, frames: 1);
        Assert.Equal((1, 0, true), (s.Generation, s.LastGeneration, s.Paused));
        Assert.Equal(0, s.ElapsedTicks); // the next generation waits at the start
        for (int i = 0; i < 10; i++) host.Frame(1.0 / 60);
        host.ReadStats(out s);
        Assert.Equal(1, s.Generation);
        host.Next();
        s = Wait(host, x => x.WaitingForNext && x.Generation == 2, frames: 1);
        host.StepMode = false;
        host.Next();
        Wait(host, x => x.Generation >= 4, frames: 1);
    }

    [Fact]
    public void NextGenerationEndsItNowAndTheRecordKeepsItsTicks()
    {
        using var host = new TrainingHost(TestTracks.Load("sprint.track"), Options(speed: 1));
        Wait(host, x => x.ElapsedTicks >= 30, frames: 1);
        host.TogglePause();
        host.ReadStats(out var s);
        long ticks = s.ElapsedTicks;
        host.EndGenerationNow();
        s = Wait(host, x => x.Generation == 1);
        Assert.Equal(0, s.LastGeneration);
        var entry = host.Session!.History.Get("main", 0)!;
        Assert.Equal(ticks, entry.Ticks);
        Assert.True(s.Paused); // it stays paused
    }

    [Fact]
    public void EvolutionChangesApplyAtTheNextBoundaryAndSettingsChangesStartASegment()
    {
        using var host = new TrainingHost(TestTracks.Load("sprint.track"), Options());
        Wait(host, x => x.Generation >= 1, frames: 1);
        host.TogglePause();
        var changed = new EvolutionSettings { MutationSigma = 0.45f, StagnationBoost = false };
        host.SetEvolution(changed);
        host.ChangeSettings(new RacingSettings { MaxSpeed = 23f });
        host.ReadStats(out var s);
        int at = s.Generation;
        host.TogglePause();
        s = Wait(host, x => x.Generation >= at + 2, frames: 1);
        var session = host.Session!;
        Assert.Equal(changed, session.Evolution);
        Assert.Equal(0.45f, session.History.Get("main", at + 1)!.Params.MutationSigma);
        Assert.Equal(2, session.Segments.Count);
        Assert.Equal(at + 1, session.Segments[1].FirstGeneration);
        Assert.Equal(1, s.Segment);
        Assert.NotEqual(session.Segments[0].Config.Hash, session.Segments[1].Config.Hash);
        Assert.Equal(23f, ((RacingSettings)host.Training!.Config.Mode).MaxSpeed);

        // The same settings again change nothing.
        host.ChangeSettings(new RacingSettings { MaxSpeed = 23f });
        Wait(host, x => x.Generation >= at + 4, frames: 1);
        Assert.Equal(2, session.Segments.Count);
    }

    [Fact]
    public void ForkFromAThinnedGenerationAndSwitchBackThroughTheHost()
    {
        var folder = Folder();
        using (var host = new TrainingHost(TestTracks.Load("sprint.track"), Options(folder) with { HistoryMemoryLimit = 0 }))
        {
            TrainTo(host, TrainToCondition.ToGeneration(12));
            var session = host.Session!;
            Assert.Null(session.History.Get("main", 7)!.Snapshot);
            host.Fork("main", 7, ForkKind.Full);
            var s = Wait(host, x => x.Branch == "fork-1");
            Assert.Equal(7, s.Generation);
            Assert.Equal(-1, s.LastGeneration); // the summary of the other branch's last generation is not shown on the fork
            Assert.Equal(-1, s.BoostStartedAt);
            TrainTo(host, TrainToCondition.ToGeneration(10));
            host.SwitchBranch("main");
            s = Wait(host, x => x.Branch == "main");
            Assert.Equal(11, s.Generation); // main's last stored generation, run again
            host.Fork("fork-1", 9, ForkKind.BestOnly);
            s = Wait(host, x => x.Branch == "fork-2");
            Assert.Equal(9, s.Generation);
            var evolution = session.Evolution;
            host.Fork("main", 40, ForkKind.Full, evolution with { MutationSigma = 0.9f }); // not evaluated: refused with a notice, training goes on
            s = Wait(host, x => x.Notice is not null);
            Assert.Contains("not been evaluated", s.Notice);
            Assert.Equal("fork-2", s.Branch);
            Assert.Equal(evolution, session.Evolution); // a refused fork changes nothing, also not the settings it came with
        }
        var loaded = Session.Load(folder);
        Assert.Equal(["main", "fork-1", "fork-2"], loaded.Branches.Select(b => b.Name));
        Assert.Equal("fork-2", loaded.ActiveBranch);
    }

    [Theory]
    [InlineData(ForkKind.Full)]
    [InlineData(ForkKind.BestOnly)]
    public void AForkLeftBeforeItsFirstGenerationEndedCanBeSwitchedBackTo(ForkKind kind)
    {
        using var host = new TrainingHost(TestTracks.Load("sprint.track"), Options(Folder(), population: 20) with { HistoryMemoryLimit = 0 });
        TrainTo(host, TrainToCondition.ToGeneration(9));
        var session = host.Session!;
        host.SetPaused(true);
        Wait(host, _ => true);
        host.Fork("main", 4, kind);
        Wait(host, x => x.Branch == "fork-1");
        ulong first;
        lock (session.SyncRoot) first = session.Current.Population.ContentHash();
        Assert.Equal(4, session.HistoryEnd("fork-1"));

        host.SwitchBranch("main");
        Wait(host, x => x.Branch == "main");
        host.SwitchBranch("fork-1");
        var s = Wait(host, x => x.Branch == "fork-1");
        Assert.Equal(4, s.Generation);
        lock (session.SyncRoot)
        {
            Assert.Equal(first, session.Current.Population.ContentHash());
            Assert.Equal(4, session.Current.Generation);
        }
        host.SetPaused(false);
        Wait(host, x => x.Generation >= 6, frames: 1);
    }

    [Fact]
    public void ForkAndSwitchAreRefusedWithANoticeWhileTrainingWithoutVisualsAndOnTheActiveBranch()
    {
        using var host = new TrainingHost(TestTracks.Load("sprint.track"), Options(Folder()));
        TrainTo(host, TrainToCondition.ToGeneration(5));
        var session = host.Session!;

        host.SwitchBranch("main"); // already active
        var s = Wait(host, x => x.Notice is not null);
        Assert.Contains("already the active branch", s.Notice);
        host.SwitchBranch("nope"); // unknown
        int count = s.NoticeCount;
        s = Wait(host, x => x.NoticeCount > count);
        Assert.False(string.IsNullOrEmpty(s.Notice));
        Assert.Single(session.Branches);

        host.TrainTo(TrainToCondition.ToGeneration(int.MaxValue));
        Wait(host, x => x.Headless && x.Generation >= 7);
        count = Wait(host, _ => true).NoticeCount;
        host.Fork("main", 2, ForkKind.Full);
        s = Wait(host, x => x.NoticeCount > count);
        Assert.Contains("without visuals", s.Notice);
        count = s.NoticeCount;
        host.SwitchBranch("main");
        s = Wait(host, x => x.NoticeCount > count);
        Assert.Contains("without visuals", s.Notice);
        Assert.Single(session.Branches);
        Assert.Equal("main", session.ActiveBranch);
        host.CancelTrainTo();
        Wait(host, x => !x.Headless);
    }

    [Fact]
    public void AutosaveWritesWhileTrainingGoesOn()
    {
        var folder = Folder();
        using var host = new TrainingHost(TestTracks.Load("sprint.track"), Options(folder) with { AutosaveInterval = TimeSpan.Zero });
        Wait(host, x => x.Generation >= 6 && x.SavedAtUtc != default, frames: 1);
        var s = Wait(host, x => x.SavedAtUtc != default);
        Assert.Null(s.SaveError);
        Assert.True(Session.Load(new SessionFolder(folder.Path)).Current.Generation >= 1);
    }

    [Fact]
    public void ASessionOpenInAHostCannotBeOpenedAgain()
    {
        var folder = Folder();
        using var host = new TrainingHost(TestTracks.Load("sprint.track"), Options(folder));
        Wait(host, _ => true);
        Assert.Throws<SessionLockedException>(() => new TrainingHost(TestTracks.Load("sprint.track"), Options(Folder())));
    }

    [Fact]
    public void ModeAndSpeedComeBackWhereTheUserLeftThem()
    {
        var folder = Folder();
        using (var host = new TrainingHost(TestTracks.Load("sprint.track"), Options(folder)))
        {
            Wait(host, _ => true);
            host.SetSpeed(10);
            host.StepMode = true;
        }
        var session = Session.Load(folder);
        Assert.True(session.StepMode);
        Assert.Equal(10, session.Speed);
        using (var host = new TrainingHost(session, Options(folder, speed: 1)))
        {
            var s = Wait(host, _ => true);
            Assert.True(s.StepMode);
            Assert.Equal(10, s.TargetSpeed);
            host.SetSpeed(double.PositiveInfinity);
            host.StepMode = false;
        }
        var again = Session.Load(folder);
        Assert.False(again.StepMode);
        Assert.True(double.IsPositiveInfinity(again.Speed!.Value));
    }

    [Fact]
    public void ASessionWithoutModeAndSpeedUsesTheOptions()
    {
        var folder = Folder();
        using (var host = new TrainingHost(TestTracks.Load("sprint.track"), Options(folder))) Wait(host, _ => true);
        var header = SessionHeader.Deserialize(File.ReadAllText(Path.Combine(folder.Path, "header.json")));
        Assert.NotNull(header.Speed); // written from the start
        string json = File.ReadAllText(Path.Combine(folder.Path, "header.json"));
        var root = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        root.Remove("speed");
        root.Remove("stepMode");
        File.WriteAllText(Path.Combine(folder.Path, "header.json"), root.ToJsonString());
        var session = Session.Load(folder);
        Assert.Null(session.Speed);
        using var host2 = new TrainingHost(session, Options(folder, speed: 25));
        var s = Wait(host2, _ => true);
        Assert.Equal(25, s.TargetSpeed);
        Assert.False(s.StepMode);
    }

    [Fact]
    public void AFailedExitSaveIsReportedAndCanBeRetried()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root") return; // needs a folder the process cannot write
        var folder = Folder();
        var host = new TrainingHost(TestTracks.Load("sprint.track"), Options(folder));
        try
        {
            TrainTo(host, TrainToCondition.ToGeneration(4));
            File.SetUnixFileMode(folder.Path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            host.Stop();
            Assert.NotNull(host.ExitSaveError);
            Assert.False(host.RetryExitSave()); // still read-only
            File.SetUnixFileMode(folder.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Assert.True(host.RetryExitSave());
            Assert.Null(host.ExitSaveError);
        }
        finally
        {
            File.SetUnixFileMode(folder.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            host.Dispose();
        }
        Assert.Equal(4, Session.Load(folder).Current.Generation);
    }

    // ---- ticket 10, review fixes (R1-R3)

    [Fact]
    public void OpeningAStaleSessionAfterAnotherWindowSavedIsRefusedAndKeepsTheOtherWindowsWork()
    {
        var folder = Folder();
        using (var host = new TrainingHost(TestTracks.Load("sprint.track"), Options(folder)))
            TrainTo(host, TrainToCondition.ToGeneration(5));
        var stale = Session.Load(folder);
        using (var host = new TrainingHost(Session.Load(folder), Options(folder)))
            TrainTo(host, TrainToCondition.ToGeneration(15));
        string before = SessionCompare.Fingerprint(Session.Load(folder));

        var e = Assert.Throws<SessionChangedException>(() => new TrainingHost(stale, Options(folder)));
        Assert.Contains("changed on disk", e.Message);
        Assert.IsAssignableFrom<IOException>(e);
        var after = Session.Load(folder);
        Assert.Equal(15, after.Current.Generation);
        Assert.Equal(before, SessionCompare.Fingerprint(after));
        // The lock was released: a fresh load opens fine.
        using var again = new TrainingHost(after, Options(folder));
        Wait(again, _ => true);
    }

    [Fact]
    public void OpeningASessionDeletedMeanwhileIsRefusedAndLeavesNothingBehind()
    {
        var folder = Folder();
        using (var host = new TrainingHost(TestTracks.Load("sprint.track"), Options(folder)))
            TrainTo(host, TrainToCondition.ToGeneration(30));
        var stale = Session.Load(folder);
        Directory.Delete(folder.Path, true);

        Assert.Throws<SessionChangedException>(() => new TrainingHost(stale, Options(folder)));
        Assert.False(Directory.Exists(folder.Path));
    }

    [Fact]
    public void ADeletedFolderThatIsRecreatedByTheLockLeavesNothingBehind()
    {
        // The folder is deleted between the existence check and the lock: simulate with an empty folder (lock file only).
        var folder = Folder();
        using (var host = new TrainingHost(TestTracks.Load("sprint.track"), Options(folder))) Wait(host, _ => true);
        var stale = Session.Load(folder);
        Directory.Delete(folder.Path, true);
        Directory.CreateDirectory(folder.Path);

        Assert.Throws<SessionChangedException>(() => new TrainingHost(stale, Options(folder)));
        Assert.False(Directory.Exists(folder.Path));
    }

    [Fact]
    public void OpeningASessionLoadedFromTheBackupHeaderStillWorksAndSpotsAnotherSave()
    {
        var folder = Folder();
        using (var host = new TrainingHost(TestTracks.Load("sprint.track"), Options(folder)))
            TrainTo(host, TrainToCondition.ToGeneration(5));
        using (var host = new TrainingHost(Session.Load(folder), Options(folder)))
            TrainTo(host, TrainToCondition.ToGeneration(8)); // header.json.bak now exists
        File.WriteAllText(folder.HeaderPath, "{ damaged");
        var fromBackup = Session.Load(folder);
        Assert.True(fromBackup.LoadedFromBackup);
        var stale = Session.Load(folder);
        using (var host = new TrainingHost(fromBackup, Options(folder)))
            TrainTo(host, TrainToCondition.ToGeneration(12));
        Assert.Throws<SessionChangedException>(() => new TrainingHost(stale, Options(folder)));
        Assert.Equal(12, Session.Load(folder).Current.Generation);
    }

    private static TrainingHostOptions FastAutosave(SessionFolder folder, bool stepMode = false) =>
        new()
        {
            Threads = 2, HeadlessThreads = 3, PopulationSize = 24, Seed = 99, Speed = double.PositiveInfinity, Folder = folder,
            AutosaveInterval = TimeSpan.FromSeconds(1),
            Evolution = new EvolutionSettings { StagnationWindow = 5 },
        };

    private static void WaitForSavedGeneration(SessionFolder folder, int generation, double seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            Thread.Sleep(100);
            try
            {
                if (folder.LoadHeader().Population?.Generation >= generation) return;
            }
            catch (InvalidDataException) { }
            Assert.True(DateTime.UtcNow < deadline, "the session was not saved while idle");
        }
    }

    [Fact]
    public void AutosaveStillRunsWhileTheTrainingIsPaused()
    {
        var folder = Folder();
        using var host = new TrainingHost(TestTracks.Load("sprint.track"), FastAutosave(folder));
        TrainTo(host, TrainToCondition.ToGeneration(6));
        Thread.Sleep(1100); // let the autosave after the train-to settle, then make the generations unsaved
        host.SetPaused(true);
        TrainTo(host, TrainToCondition.ToGeneration(24));
        Thread.Sleep(300);
        // No Frame call from here on: the host is idle.
        WaitForSavedGeneration(folder, 24);
    }

    [Fact]
    public void AutosaveStillRunsWhileStepModeWaitsForNext()
    {
        var folder = Folder();
        using var host = new TrainingHost(TestTracks.Load("sprint.track"), FastAutosave(folder));
        Wait(host, _ => true);
        host.StepMode = true;
        TrainTo(host, TrainToCondition.ToGeneration(20));
        Wait(host, x => x.WaitingForNext || x.Headless == false);
        host.Next();
        // Back to Auto-less waiting: one generation by frames, then it waits for Next.
        Wait(host, x => x.WaitingForNext, frames: 1);
        int g = host.Session!.Current.Generation;
        WaitForSavedGeneration(folder, g);
    }

    [Fact]
    public void ResumingInStepModeWaitsForNextInsteadOfRunning()
    {
        var folder = Folder();
        using (var host = new TrainingHost(TestTracks.Load("sprint.track"), Options(folder)))
        {
            TrainTo(host, TrainToCondition.ToGeneration(3));
            host.StepMode = true;
        }
        var session = Session.Load(folder);
        Assert.True(session.StepMode);
        using (var host = new TrainingHost(session, Options(folder)))
        {
            var s = Wait(host, _ => true);
            Assert.True(s.Paused);
            Assert.True(s.WaitingForNext);
            for (int i = 0; i < 20; i++) host.Frame(1.0 / 60);
            host.ReadStats(out s);
            Assert.Equal(0, s.ElapsedTicks); // the generation did not start running
            Assert.Equal(3, s.Generation);
            Assert.Equal(2, s.LastGeneration);
            Assert.True(s.LastStats.BestScore > 0);
            host.Next();
            Wait(host, x => x.ElapsedTicks > 0 && !x.WaitingForNext, frames: 1);
        }
    }
}
