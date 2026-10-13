using Nitrogenesis.Sim.Evolution;
using Nitrogenesis.Sim.History;
using Nitrogenesis.Sim.Io;
using Nitrogenesis.Sim.Racing;

/// <summary>
/// Trains a <see cref="Session"/> in the test thread the way the training host does (no threads of its own, no
/// frames): run a generation, store it, continue; forks, branch switches and segment changes as the host does them.
/// </summary>
internal sealed class SessionDriver : IDisposable
{
    public const int Threads = 2;

    private SessionDriver(Session session, RacingTraining training)
    {
        Session = session;
        Training = training;
    }

    public Session Session { get; }
    public RacingTraining Training { get; private set; }
    public GenerationRunnerView Runner => new(Training);

    public static SessionDriver New(string track = "sprint.track", int population = 24, ulong seed = 7, EvolutionSettings? evolution = null,
        long memoryLimit = HistoryStore.DefaultMemoryLimit, RacingSettings? settings = null)
    {
        var t = new RacingTraining(TestTracks.Load(track), settings ?? new RacingSettings(), seed, evolution, population, Threads);
        var s = Session.Create(t.Config, t.Track.Track, seed, t.Runner.Settings, t.Runner.Population, t.Runner.PopulationParams, memoryLimit);
        return new SessionDriver(s, t);
    }

    /// <summary>Continues a session (e.g. a loaded one) on this thread.</summary>
    public static SessionDriver Resume(Session session) => new(session, RacingTraining.ResumeFrom(session, Threads));

    /// <summary>Runs and stores one generation.</summary>
    public GenerationRecord Step()
    {
        var runner = Training.Runner;
        var record = runner.RunGeneration();
        Session.AddGeneration(record, runner.Population, runner.PopulationParams);
        return record;
    }

    public void Steps(int count)
    {
        for (int i = 0; i < count; i++) Step();
    }

    /// <summary>Runs one generation for only <paramref name="ticks"/> ticks ("Next generation" pressed) and stores it.</summary>
    public GenerationRecord StepEndedEarly(int ticks)
    {
        var runner = Training.Runner;
        runner.BeginGeneration();
        runner.RunTicks(ticks);
        var record = runner.EndGeneration();
        Session.AddGeneration(record, runner.Population, runner.PopulationParams);
        return record;
    }

    /// <summary>
    /// The population continues under other racing settings, and optionally another number of cars, as a new segment (what the
    /// host does at a boundary).
    /// </summary>
    public void ChangeSettings(RacingSettings settings, int? populationSize = null)
    {
        var current = Session.Current;
        var segment = Session.SegmentAt(current.Branch, current.Generation);
        ulong seed = Session.Branch(current.Branch).Seed;
        var population = current.Population;
        int size = populationSize ?? population.Size;
        if (size != population.Size)
            population = PopulationResize.To(population, size, current.Params, PopulationResize.SeedFor(Nitrogenesis.Sim.Rng.SeedHash.Derive(seed, (ulong)current.Generation)));
        var t = new RacingTraining(segment.Track, settings, seed, Session.Evolution, size, Threads,
            segment.Config.Brain.Hidden1, segment.Config.Brain.Hidden2, population, current.Generation, current.Params);
        Session.StartSegment(t.Config, segment.Track, size, size != current.Population.Size ? population : null);
        Training.Dispose();
        Training = t;
    }

    /// <summary>New evolution settings, as the host applies them: saved with the session and used from the next breeding.</summary>
    public void SetEvolution(EvolutionSettings settings)
    {
        Session.Evolution = settings;
        Training.Runner.Settings = settings;
    }

    public BranchEntry ForkFull(string branch, int generation)
    {
        var population = Rebuilder.Rebuild(Session, branch, generation, Threads);
        var b = Session.CreateFork(branch, generation, ForkKind.Full, population);
        Reload();
        return b;
    }

    public BranchEntry ForkBest(string branch, int generation)
    {
        var b = Session.CreateFork(branch, generation, ForkKind.BestOnly);
        Reload();
        return b;
    }

    public void Activate(string branch)
    {
        var (generation, _) = Session.ResumePoint(branch);
        Session.Activate(branch, Rebuilder.Rebuild(Session, branch, generation, Threads));
        Reload();
    }

    /// <summary>Saves the whole session to a folder (synchronously) and records it.</summary>
    public void Save(SessionFolder folder)
    {
        var plan = Session.PrepareSave(DateTime.UtcNow);
        folder.Save(plan);
        Session.MarkSaved(plan);
    }

    private void Reload()
    {
        Training.Dispose();
        Training = RacingTraining.ResumeFrom(Session, Threads);
    }

    public void Dispose() => Training.Dispose();

    /// <summary>Shortcuts into the runner.</summary>
    public readonly struct GenerationRunnerView(RacingTraining t)
    {
        public int Generation => t.Runner.Generation;
        public ulong PopulationHash => t.Runner.Population.ContentHash();
    }
}

/// <summary>Helpers to compare sessions.</summary>
internal static class SessionCompare
{
    /// <summary>Everything about a stored generation except its snapshot (which thinning may drop).</summary>
    public static string Describe(HistoryEntry e) =>
        $"{e.Generation} {e.Seed:X} {e.Params} {e.Stats} {e.Ticks} " +
        $"{Convert.ToHexString(System.Runtime.InteropServices.MemoryMarshal.AsBytes(e.BestGenome.Weights.AsSpan()))[..64]}… " +
        $"{Crc32.Compute(System.Runtime.InteropServices.MemoryMarshal.AsBytes(e.BestGenome.Weights.AsSpan())):X8} {Crc32.Compute(e.RecordingBytes):X8}";

    /// <summary>The session's state as text: branches, segments, current population and every stored generation.</summary>
    public static string Fingerprint(Session s)
    {
        lock (s.SyncRoot)
        {
            var lines = new List<string>
            {
                $"active {s.ActiveBranch} current {s.Current.Branch}/{s.Current.Generation}/seg{s.Current.Segment} {s.Current.Params} {s.Current.Population.ContentHash():X16}",
                $"evolution {s.Evolution}",
            };
            lines.AddRange(s.Branches.Select(b => $"branch {b}"));
            lines.AddRange(s.Segments.Select(g => $"segment {g.Id} {g.Branch} {g.FirstGeneration} {g.Config.Hash} {g.PopulationSize}"));
            foreach (var b in s.History.Branches)
                lines.AddRange(b.Entries.Select(e => $"{b.Name} {Describe(e)}"));
            return string.Join("\n", lines);
        }
    }

    /// <summary>Every snapshot still held equals the original one of that generation (thinning only drops them).</summary>
    public static void AssertSnapshotsMatch(Session s, IReadOnlyDictionary<(string, int), float[]> originals)
    {
        foreach (var b in s.History.Branches)
            foreach (var e in b.Entries)
                if (e.Snapshot is { } snap)
                    Assert.True(Nitrogenesis.Sim.Evolution.Population.BitEquals(originals[(b.Name, e.Generation)], snap), $"snapshot {b.Name}/{e.Generation} differs");
    }

    /// <summary>All snapshots the session holds now, by (branch, generation).</summary>
    public static Dictionary<(string, int), float[]> Snapshots(Session s)
    {
        var result = new Dictionary<(string, int), float[]>();
        foreach (var b in s.History.Branches)
            foreach (var e in b.Entries)
                if (e.Snapshot is { } snap) result[(b.Name, e.Generation)] = snap;
        return result;
    }

    public static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (string dir in Directory.GetDirectories(from)) CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)));
    }
}
