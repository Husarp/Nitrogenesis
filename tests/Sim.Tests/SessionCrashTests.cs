using Nitrogenesis.Sim.Evolution;
using Nitrogenesis.Sim.History;
using Nitrogenesis.Sim.Io;
using Nitrogenesis.Sim.Racing;

/// <summary>
/// PLAN §8 M3 acceptance, "kill the app mid-autosave → it resumes": a save is stopped at every single step (as if the
/// process died there) and the session must still load, as the previous save or the new one, and train on.
/// </summary>
public sealed class SessionCrashTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ng-crash-" + Guid.NewGuid().ToString("N"));

    public SessionCrashTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string Dir(string name) => Path.Combine(_root, name);

    private static void Merge(Dictionary<(string, int), float[]> into, Session s)
    {
        foreach (var (key, value) in SessionCompare.Snapshots(s)) into[key] = value;
    }

    [Fact]
    public void ACrashAtAnyStepOfASaveLeavesTheLastSaveOrTheNewOne()
    {
        var originals = new Dictionary<(string, int), float[]>();
        using var d = SessionDriver.New(population: 16, evolution: new EvolutionSettings { StagnationWindow = 5 });

        // The last good save: main 0…29, fork-1 from 12, thinned.
        d.Steps(30);
        d.ForkFull("main", 12);
        d.Steps(8);
        Merge(originals, d.Session);
        long small = d.Session.History.Bytes - d.Session.History.SnapshotBytes + 12 * originals[("main", 0)].Length * 4L;
        d.Session.SetMemoryLimit(small);
        d.Save(new SessionFolder(Dir("committed")));
        string before = SessionCompare.Fingerprint(d.Session);
        Assert.Equal(before, SessionCompare.Fingerprint(Session.Load(new SessionFolder(Dir("committed")))));

        // The next save: more generations across chunk windows, a branch switch, a new segment, a new fork and
        // more thinning (saved windows are rewritten).
        d.Session.SetMemoryLimit(HistoryStore.DefaultMemoryLimit);
        d.Steps(10); // fork-1 to 29
        d.Activate("main");
        d.Steps(3);
        d.ChangeSettings(new RacingSettings { MaxSpeed = 21f });
        d.Steps(2);
        d.ForkBest("main", 20);
        d.Steps(2);
        Merge(originals, d.Session);
        d.Session.SetMemoryLimit(small);
        var plan = d.Session.PrepareSave(DateTime.UtcNow);
        string after = SessionCompare.Fingerprint(d.Session);
        Assert.NotEqual(before, after);
        Assert.Contains(plan.Windows, w => w.Branch == "main" && w.Window == 0); // a thinned window, saved before

        // Count the steps of the save.
        SessionCompare.CopyDirectory(Dir("committed"), Dir("dry"));
        var steps = new List<string>();
        new SessionFolder(Dir("dry")) { FaultInjection = s => { steps.Add(s); return false; } }.Save(plan);
        Assert.Equal(after, SessionCompare.Fingerprint(Session.Load(new SessionFolder(Dir("dry")))));
        int commit = steps.FindIndex(s => s.StartsWith("moved ") && s.EndsWith(SessionFolder.HeaderFileName));
        Assert.Equal(steps.Count - 1, commit); // the header is the last step
        // Every kind of step is there: new chunk files, thinned windows rewritten over old files, superseded partial
        // chunks deleted, a new branch, the header with its backup.
        string all = string.Join("\n", steps.Select(s => s.Replace(Dir("dry"), "")));
        Assert.True(steps.Count > 20, all);
        Assert.Contains("/history/main/gen-0-24.bin.tmp", all);  // rewritten without the dropped snapshots
        Assert.Contains("delete /history/main/gen-25-29.bin", all); // superseded by the longer chunk
        Assert.Contains("/history/fork-2/", all);

        for (int k = 0; k < steps.Count; k++)
        {
            string dir = Dir($"crash-{k}");
            SessionCompare.CopyDirectory(Dir("committed"), dir);
            int n = 0;
            var folder = new SessionFolder(dir) { FaultInjection = _ => n++ == k };
            var crash = Assert.Throws<SimulatedCrashException>(() => folder.Save(plan));
            Assert.Contains(steps[k].Replace(Dir("dry"), dir), crash.Message);

            var loaded = Session.Load(new SessionFolder(dir));
            Assert.Empty(loaded.Warnings);
            Assert.Equal(k < commit ? before : after, SessionCompare.Fingerprint(loaded));
            SessionCompare.AssertSnapshotsMatch(loaded, originals);

            // It resumes, trains and saves again.
            using var resumed = SessionDriver.Resume(loaded);
            resumed.Step();
            resumed.Save(new SessionFolder(dir));
            Assert.Equal(SessionCompare.Fingerprint(resumed.Session), SessionCompare.Fingerprint(Session.Load(new SessionFolder(dir))));
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ADamagedHeaderFallsBackToThePreviousSave()
    {
        var folder = new SessionFolder(Dir("s"));
        using var d = SessionDriver.New(population: 16);
        d.Steps(6);
        d.Save(folder);
        string first = SessionCompare.Fingerprint(d.Session);
        d.Steps(4);
        d.Save(folder);
        string second = SessionCompare.Fingerprint(d.Session);

        string header = File.ReadAllText(folder.HeaderPath);
        File.WriteAllText(folder.HeaderPath, header[..(header.Length / 2)]); // torn by a disk fault
        var loaded = Session.Load(new SessionFolder(Dir("s")));
        Assert.Equal(first, SessionCompare.Fingerprint(loaded));
        Assert.Contains("damaged", Assert.Single(loaded.Warnings));

        // Training goes on from the older save; the damaged header never becomes the backup.
        var reopened = new SessionFolder(Dir("s"));
        var again = Session.Load(reopened);
        using var r = SessionDriver.Resume(again);
        r.Steps(4);
        Assert.Equal(second, SessionCompare.Fingerprint(r.Session)); // deterministic: the same 4 generations again
        r.Save(reopened);
        Assert.Equal(second, SessionCompare.Fingerprint(Session.Load(new SessionFolder(Dir("s")))));
        Assert.Equal(first, SessionCompare.Fingerprint(Session.FromSaved(new SessionFolder(Dir("s")).Load(backupOnly: true))));
    }

    [Fact]
    public void AfterLoadingTheBackupTheDamagedHeaderNeverBecomesTheBackupWhicheverFolderObjectSaves()
    {
        var folder = new SessionFolder(Dir("s"));
        using var d = SessionDriver.New(population: 16);
        d.Steps(5);
        d.Save(folder);
        string first = SessionCompare.Fingerprint(d.Session);
        d.Steps(2);
        d.Save(folder);
        File.WriteAllText(folder.HeaderPath, "{ torn");

        var loaded = Session.Load(new SessionFolder(Dir("s")));
        Assert.Equal(first, SessionCompare.Fingerprint(loaded));
        using var r = SessionDriver.Resume(loaded);
        r.Step();
        r.Save(new SessionFolder(Dir("s"))); // another SessionFolder object than the one that loaded it (as an app may do)
        Assert.Empty(Session.Load(new SessionFolder(Dir("s"))).Warnings);
        Assert.Equal(first, SessionCompare.Fingerprint(Session.FromSaved(new SessionFolder(Dir("s")).Load(backupOnly: true))));
        r.Step();
        r.Save(new SessionFolder(Dir("s"))); // from now on the previous save becomes the backup as usual
        Assert.NotEqual(first, SessionCompare.Fingerprint(Session.FromSaved(new SessionFolder(Dir("s")).Load(backupOnly: true))));
    }

    [Fact]
    public void AHeaderThatContradictsItselfFallsBackToThePreviousSave()
    {
        var folder = new SessionFolder(Dir("s"));
        using var d = SessionDriver.New(population: 16);
        d.Steps(3);
        d.Save(folder);
        string first = SessionCompare.Fingerprint(d.Session);
        d.Steps(3);
        d.Save(folder);
        File.WriteAllText(folder.HeaderPath, File.ReadAllText(folder.HeaderPath).Replace("\"currentBranch\": \"main\"", "\"currentBranch\": \"nope\""));
        var loaded = Session.Load(folder);
        Assert.Equal(first, SessionCompare.Fingerprint(loaded));
        Assert.Single(loaded.Warnings);
    }

    [Fact]
    public void WithoutAnyReadableHeaderLoadingFailsClearly()
    {
        var folder = new SessionFolder(Dir("s"));
        using var d = SessionDriver.New(population: 16);
        d.Steps(2);
        d.Save(folder);
        File.WriteAllText(folder.HeaderPath, "{");
        Assert.Throws<InvalidDataException>(() => Session.Load(folder)); // the first save has no backup yet
    }

    [Fact]
    public void ADamagedChunkLoadsWithAGapAndTrainingGoesOn()
    {
        var folder = new SessionFolder(Dir("s"));
        using var d = SessionDriver.New(population: 16);
        d.Steps(60);
        d.Save(folder);
        string chunk = Path.Combine(folder.BranchFolder("main"), "gen-25-49.bin");
        byte[] bytes = File.ReadAllBytes(chunk);
        bytes[100] ^= 1;
        File.WriteAllBytes(chunk, bytes);

        var loaded = Session.Load(folder);
        Assert.Contains("generations 25–49", Assert.Single(loaded.Warnings));
        Assert.Equal(35, loaded.History.Find("main")!.Count);
        Assert.Null(loaded.History.Get("main", 30));
        // Generations after the gap still rebuild from their own snapshots; the gap itself cannot be rebuilt.
        Assert.NotNull(Rebuilder.Rebuild(loaded, "main", 55));
        var e = Assert.Throws<InvalidOperationException>(() => Rebuilder.Rebuild(loaded, "main", 30));
        Assert.Contains("missing", e.Message);

        using var r = SessionDriver.Resume(loaded);
        r.Steps(3);
        Assert.Equal(d.Runner.Generation + 3, r.Runner.Generation);
        r.Save(folder);
        var again = Session.Load(folder);
        Assert.Equal(38, again.History.Find("main")!.Count);
        Assert.Contains("generations 25–49", Assert.Single(again.Warnings)); // the damaged file is kept, not overwritten
    }

    [Fact]
    public void AFolderLeftByAForkThatNeverCommittedIsClearedOnItsFirstSave()
    {
        var folder = new SessionFolder(Dir("s"));
        using var d = SessionDriver.New(population: 16);
        d.Steps(40);
        d.Save(folder);
        // A previous run forked at 3, wrote fork-1's chunks, and died before its header.
        Directory.CreateDirectory(folder.BranchFolder("fork-1"));
        File.WriteAllBytes(Path.Combine(folder.BranchFolder("fork-1"), "gen-3-24.bin"), File.ReadAllBytes(Path.Combine(folder.BranchFolder("main"), "gen-0-24.bin")));
        File.WriteAllText(Path.Combine(folder.BranchFolder("fork-1"), "gen-25-30.bin"), "junk");
        Assert.Equal(SessionCompare.Fingerprint(d.Session), SessionCompare.Fingerprint(Session.Load(folder)));

        d.ForkFull("main", 30);
        d.Steps(2);
        d.Save(folder);
        Assert.Equal(["gen-30-31.bin"], Directory.GetFiles(folder.BranchFolder("fork-1")).Select(f => Path.GetFileName(f)!));
        Assert.Equal(SessionCompare.Fingerprint(d.Session), SessionCompare.Fingerprint(Session.Load(folder)));
    }

    [Fact]
    public void ASessionCannotBeOpenedTwice()
    {
        var folder = new SessionFolder(Dir("s"));
        using (folder.AcquireLock())
        {
            var e = Assert.Throws<SessionLockedException>(() => new SessionFolder(Dir("s")).AcquireLock());
            Assert.Contains("another", e.Message);
        }
        using (new SessionFolder(Dir("s")).AcquireLock()) { } // free again
    }

    [Fact]
    public void TheSaverWritesInTheBackgroundAndReportsFailures()
    {
        using var d = SessionDriver.New(population: 16);
        d.Steps(3);
        var folder = new SessionFolder(Dir("s"));
        using (var saver = new SessionSaver(folder, d.Session))
        {
            saver.Start(d.Session.PrepareSave(DateTime.UtcNow));
            Assert.True(saver.WaitIdle(TimeSpan.FromSeconds(30)));
            Assert.Null(saver.LastError);
            Assert.Equal(1, saver.Saves);
            Assert.False(d.Session.HasUnsavedChanges);
        }

        File.WriteAllText(Dir("file"), "not a folder");
        d.Step();
        using (var saver = new SessionSaver(new SessionFolder(Path.Combine(Dir("file"), "s")), d.Session))
        {
            saver.Start(d.Session.PrepareSave(DateTime.UtcNow));
            Assert.True(saver.WaitIdle(TimeSpan.FromSeconds(30)));
            Assert.NotNull(saver.LastError);
            Assert.Equal(0, saver.Saves);
            Assert.True(d.Session.HasUnsavedChanges); // not marked saved: the next save writes it all again
        }
        Assert.Equal(3, Session.Load(folder).Current.Generation);
    }
}
