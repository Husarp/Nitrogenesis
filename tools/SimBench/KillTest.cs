using System.Diagnostics;
using Nitrogenesis.Sim.Evolution;
using Nitrogenesis.Sim.History;
using Nitrogenesis.Sim.Io;
using Nitrogenesis.Sim.Map;
using Nitrogenesis.Sim.Racing;

/// <summary>
/// `kill-test` (PLAN §8 M3 acceptance, "kill the app mid-autosave → it resumes"): a child process trains a session
/// headless and saves continuously (autosave interval 0, a small history limit so chunks get thinned and rewritten,
/// forks at fixed points); the parent kills it at random moments, then checks that the session loads without
/// warnings, its history is complete, and a thinned generation still rebuilds bit for bit. The next child resumes the
/// session. At the end one child runs to the target, and the result must equal an uninterrupted run in this process.
/// </summary>
/// <remarks>`save-stress` is the child. Its output lines starting with "step " are the save steps, so the parent
/// knows whether a kill landed inside a save.</remarks>
internal static class KillTest
{
    private const int Population = 40;
    private const ulong Seed = 5;
    private const long HistoryLimit = 3_000_000;

    private static TrainingHostOptions Options(SessionFolder? folder) => new()
    {
        Threads = 2,
        HeadlessThreads = 2,
        PopulationSize = Population,
        Seed = Seed,
        Folder = folder,
        AutosaveInterval = TimeSpan.Zero,
        HistoryMemoryLimit = Environment.GetEnvironmentVariable("NG_STRESS_LIMIT") is { } l ? long.Parse(l, System.Globalization.CultureInfo.InvariantCulture) : HistoryLimit,
        Evolution = new EvolutionSettings { StagnationWindow = 5 },
    };

    /// <summary>
    /// Forks at fixed points so a killed and resumed run makes the same branches. It depends only on the session's
    /// state (checked when a generation ends and when a child starts, before training): when generation 40k is next
    /// on the active branch, below the target, and the session has fewer than k + 1 branches, fork from 23
    /// generations back (full and best-only in turn).
    /// </summary>
    private static void ForkPolicy(TrainingHost host, int next, int target)
    {
        if (Environment.GetEnvironmentVariable("NG_STRESS_NOFORK") is not null) return;
        var session = host.Session!;
        int branches = session.Branches.Count;
        if (next == 0 || next % 40 != 0 || next >= target || branches >= 1 + next / 40) return;
        host.Fork(session.ActiveBranch, next - 23, branches % 2 == 1 ? ForkKind.Full : ForkKind.BestOnly);
    }

    private static string Fingerprint(Session s)
    {
        lock (s.SyncRoot)
        {
            var lines = new List<string> { $"{s.ActiveBranch} {s.Current.Generation} {s.Current.Params} {s.Current.Population.ContentHash():X16}" };
            lines.AddRange(s.Branches.Select(b => b.ToString()));
            lines.AddRange(s.Segments.Select(g => $"{g.Id} {g.Branch} {g.FirstGeneration} {g.Config.Hash}"));
            foreach (var b in s.History.Branches)
                lines.AddRange(b.Entries.Select(e => $"{b.Name} {e.Generation} {e.Seed:X} {e.Params} {e.Stats} {e.Ticks} {Crc32.Compute(e.RecordingBytes):X8}"));
            return string.Join("\n", lines);
        }
    }

    private static Track LoadTrack() => TrackFile.Load(TrackPaths.Resolve("sprint"));

    /// <summary>The child: open or create the session in <paramref name="dir"/>, train to <paramref name="generations"/> saving all the time.</summary>
    public static int Child(string dir, int generations)
    {
        var folder = new SessionFolder(dir) { FaultInjection = step => { Console.WriteLine("step " + step); return false; } };
        bool noSave = Environment.GetEnvironmentVariable("NG_STRESS_NOSAVE") is not null;
        if (Environment.GetEnvironmentVariable("NG_STRESS_GARBAGE") is not null)
            new Thread(() => { var keep = new List<byte[]>(); while (true) { keep.Add(new byte[20_000]); if (keep.Count > 50) keep.Clear(); } }) { IsBackground = true }.Start();
        TrainingHost host = folder.Exists ? new TrainingHost(Session.Load(folder), Options(folder))
            : new TrainingHost(LoadTrack(), Options(noSave ? null : folder));
        host.GenerationEnded += r => ForkPolicy(host, r.Generation + 1, generations);
        TrainingStats s;
        do
        {
            Thread.Sleep(1);
            host.ReadStats(out s);
        } while (!s.Ready && s.Error is null);
        Console.WriteLine($"ready {s.Generation} {s.Branch}");
        ForkPolicy(host, s.Generation, generations); // the save may have been made between a generation's end and its fork
        host.TrainTo(TrainToCondition.ToGeneration(generations));
        int runs = s.TrainToRuns;
        do
        {
            Thread.Sleep(5);
            host.ReadStats(out s);
        } while (s.Error is null && (s.TrainToRuns == runs || s.Headless || s.Busy is not null));
        if (s.Error is not null)
        {
            Console.WriteLine($"error at generation {s.Generation} on {s.Branch}: {s.Error}");
            return 1;
        }
        host.Dispose(); // the exit save
        Console.WriteLine($"done {s.Generation} {s.Branch}");
        return 0;
    }

    public static int Run(int rounds, int generations)
    {
        string dir = Path.Combine(Path.GetTempPath(), "nitrogenesis-kill-test-" + Guid.NewGuid().ToString("N"));
        Console.WriteLine($"kill-test: {rounds} kills, then train to generation {generations}; session in {dir}");

        // The uninterrupted reference, in this process, not saved.
        string reference;
        var watch = Stopwatch.StartNew();
        using (var host = new TrainingHost(LoadTrack(), Options(null)))
        {
            host.GenerationEnded += r => ForkPolicy(host, r.Generation + 1, generations);
            TrainingStats s;
            do
            {
                Thread.Sleep(1);
                host.ReadStats(out s);
            } while (!s.Ready);
            host.TrainTo(TrainToCondition.ToGeneration(generations));
            do
            {
                Thread.Sleep(5);
                host.ReadStats(out s);
            } while (s.Error is null && (s.TrainToRuns == 0 || s.Headless || s.Busy is not null));
            if (s.Error is not null) throw new InvalidOperationException(s.Error);
            reference = Fingerprint(host.Session!);
            Console.WriteLine($"reference: generation {s.Generation} on {s.Branch}, {host.Session!.Branches.Count} branches, {watch.Elapsed.TotalSeconds:F1} s");
        }

        var random = new Random(12345);
        int midSave = 0, failures = 0, kills = 0, lastGeneration = -1;
        for (int round = 0; round < rounds; round++)
        {
            var child = StartChild(dir, generations);
            var lines = new List<string>();
            var ready = new ManualResetEventSlim();
            child.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                lock (lines) lines.Add(e.Data);
                if (e.Data.StartsWith("ready", StringComparison.Ordinal)) ready.Set();
            };
            child.BeginOutputReadLine();
            if (!ready.Wait(TimeSpan.FromSeconds(60))) throw new InvalidOperationException("The child did not start.");
            Thread.Sleep(random.Next(100, 1500));
            bool exited = child.HasExited;
            if (!exited) child.Kill(entireProcessTree: true);
            child.WaitForExit();
            string last;
            lock (lines) last = lines.Count > 0 ? lines[^1] : "";
            if (exited)
            {
                Console.WriteLine($"round {round}: the child finished before the kill ({last})");
                break;
            }
            kills++;
            bool inSave = last.StartsWith("step ", StringComparison.Ordinal) && !(last.StartsWith("step moved", StringComparison.Ordinal) && last.EndsWith(SessionFolder.HeaderFileName));
            if (inSave) midSave++;

            // The session must load cleanly, completely and rebuild.
            try
            {
                var session = Session.Load(new SessionFolder(dir));
                if (session.Warnings.Count > 0) throw new InvalidDataException(string.Join("; ", session.Warnings));
                foreach (var b in session.Branches)
                {
                    var entries = session.History.Find(b.Name)!.Entries;
                    for (int i = 0; i < entries.Count; i++)
                        if (entries[i].Generation != b.FirstGeneration + i) throw new InvalidDataException($"'{b.Name}' has a gap at {b.FirstGeneration + i}.");
                    if (entries.Count > 0) entries[^1].DecodeRecording();
                }
                var main = session.History.Find(Session.RootBranch)!.Entries;
                var thinned = main.Where(e => e.Snapshot is null).ToList();
                string rebuilt = "";
                if (thinned.Count > 0)
                {
                    var target = thinned[random.Next(thinned.Count)];
                    Rebuilder.Rebuild(session, Session.RootBranch, target.Generation, 2); // checks every re-run generation against its record
                    rebuilt = $", rebuilt {target.Generation} ok";
                }
                int generation = session.Current.Generation;
                Console.WriteLine($"round {round}: killed {(inSave ? "mid-save " : "")}at '{Shorten(last, dir)}' → loads: {session.ActiveBranch} generation {generation}, " +
                                  $"{session.Branches.Count} branches, {session.History.SnapshotCount} snapshots{rebuilt}");
                lastGeneration = generation;
            }
            catch (Exception e)
            {
                failures++;
                Console.WriteLine($"round {round}: FAILED to load after a kill at '{Shorten(last, dir)}': {e.GetType().Name}: {e.Message}");
            }
        }

        // The last child runs to the end; the result equals the uninterrupted run.
        var final = StartChild(dir, generations);
        final.BeginOutputReadLine();
        final.WaitForExit();
        var finished = Session.Load(new SessionFolder(dir));
        bool same = Fingerprint(finished) == reference;
        Console.WriteLine($"final: generation {finished.Current.Generation} on {finished.ActiveBranch}, {finished.Branches.Count} branches; " +
                          $"identical to the uninterrupted run: {(same ? "yes" : "NO")}");
        Console.WriteLine($"{kills} kills, {midSave} inside a save, {failures} failed loads; last generation seen {lastGeneration}");
        Directory.Delete(dir, recursive: true);
        return failures == 0 && same && final.ExitCode == 0 ? 0 : 1;
    }

    private static string Shorten(string line, string dir) => line.Replace(dir, "…");

    private static Process StartChild(string dir, int generations)
    {
        string self = Environment.ProcessPath!;
        var info = new ProcessStartInfo(self) { RedirectStandardOutput = true, UseShellExecute = false };
        if (Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(Environment.GetCommandLineArgs()[0]); // the SimBench.dll that dotnet runs
        foreach (string a in new[] { "save-stress", "--dir", dir, "--gens", generations.ToString(System.Globalization.CultureInfo.InvariantCulture) })
            info.ArgumentList.Add(a);
        return Process.Start(info)!;
    }
}
