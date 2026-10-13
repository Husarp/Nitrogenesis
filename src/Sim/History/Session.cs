using Nitrogenesis.Sim.Core;
using Nitrogenesis.Sim.Evolution;
using Nitrogenesis.Sim.Io;
using Nitrogenesis.Sim.Map;
using Nitrogenesis.Sim.Rng;

namespace Nitrogenesis.Sim.History;

/// <summary>Generations [<see cref="From"/>, <see cref="To"/>) of a lineage that are stored in <see cref="Branch"/>'s history.</summary>
public readonly record struct LineagePart(string Branch, int From, int To);

/// <summary>
/// One training in memory (PLAN §5): its segments (SimConfig + track copy), branches, the active branch, the
/// current population, the evolution settings and the history of every branch. It is what a session folder
/// holds (<see cref="SessionHeader"/> + history chunks) and what the training host trains.
/// </summary>
/// <remarks>
/// <para><b>Threads.</b> The training thread changes the session; the UI reads it; the save thread records what it
/// wrote. Every public method locks <see cref="SyncRoot"/>; a reader that needs several values to agree locks it
/// too. <see cref="Current"/>'s population is the runner's live one: only the training thread may read its weights
/// (a save copies them in <see cref="PrepareSave"/>).</para>
/// <para><b>Lineage.</b> A fork from generation X of a parent starts its own history at X; generations before X
/// belong to the parent. The lineage of a branch is the parent's lineage up to X, then the branch.</para>
/// <para><b>Segments.</b> Each branch has a segment at its first generation; a SimConfig change starts a new one at
/// the next generation boundary. Stats baselines (best ever, stagnation, graph) run over the contiguous generations
/// of the lineage with the same config hash (<see cref="SegmentRunStart"/>).</para>
/// </remarks>
public sealed class Session
{
    public const string RootBranch = "main";
    private const ulong ForkSeedTag = 0x466F726B_00000000UL; // "Fork"

    private readonly List<SegmentEntry> _segments = [];
    private readonly List<BranchEntry> _branches = [];
    private readonly Dictionary<string, int> _savedEnds = new(StringComparer.Ordinal);
    // History ends of the last planned save, which may still be writing: a window it writes and that is thinned
    // meanwhile must be rewritten by the next save too.
    private readonly Dictionary<string, int> _plannedEnds = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Branch, int Window), long> _dirty = [];
    private readonly List<string> _warnings = [];
    private long _stamp = 1, _savedStamp;
    private bool _loadedFromBackup;
    private EvolutionSettings _evolution = new();

    private Session(int simVersion, long memoryLimit)
    {
        SimVersion = simVersion;
        History = new HistoryStore { MemoryLimit = memoryLimit };
    }

    public object SyncRoot { get; } = new();

    /// <summary>SimVersion the session was trained with.</summary>
    public int SimVersion { get; }
    public SessionCompatibility Compatibility => new(SimVersion);
    /// <summary>Made with another SimVersion: recordings play, but training, forks and re-simulation are off (PLAN §5.1).</summary>
    public bool ReadOnly => !Compatibility.CanResume;

    public HistoryStore History { get; }
    public IReadOnlyList<SegmentEntry> Segments => _segments;
    public IReadOnlyList<BranchEntry> Branches => _branches;
    public string ActiveBranch { get; private set; } = RootBranch;
    /// <summary>The population being (or about to be) evaluated, on the active branch.</summary>
    public PopulationEntry Current { get; private set; } = null!;

    /// <summary>Live evolution settings (PLAN §6.4); the runner applies them at the next generation boundary.</summary>
    public EvolutionSettings Evolution
    {
        get => _evolution;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (SyncRoot)
            {
                var clamped = value.Clamped();
                if (clamped == _evolution) return;
                _evolution = clamped;
                Changed();
            }
        }
    }

    /// <summary>Real time spent training, seconds (kept by the host, saved in the header).</summary>
    public double TrainingSeconds { get; set; }
    /// <summary>Ticks simulated in all generations (kept by the host).</summary>
    public long TotalTicks { get; set; }

    /// <summary>Mode Step (true) / Auto (false) the user left the training in, or null when the file did not say (kept by the host).</summary>
    public bool? StepMode { get; private set; }
    /// <summary>Playback speed the user left the training at (+∞ = MAX), or null when the file did not say (kept by the host).</summary>
    public double? Speed { get; private set; }

    /// <summary>Remembers the mode and speed for the next save; a change counts as an unsaved change.</summary>
    public void SetViewState(bool stepMode, double speed)
    {
        lock (SyncRoot)
        {
            if (StepMode == stepMode && Speed == speed) return;
            StepMode = stepMode;
            Speed = speed;
            Changed();
        }
    }

    /// <summary>When the save this session was loaded from was made (UTC; default for a new session).</summary>
    public DateTime LoadedSavedAtUtc { get; private set; }

    /// <summary>True when the load used the backup header (<see cref="LoadedSavedAtUtc"/> is the backup's).</summary>
    public bool LoadedFromBackup { get; private set; }

    /// <summary>Goes up with every change (new generation, fork, segment, settings): the UI refreshes when it moves.</summary>
    public long Version { get { lock (SyncRoot) return _stamp; } }

    /// <summary>Something changed since the last finished save.</summary>
    public bool HasUnsavedChanges { get { lock (SyncRoot) return _stamp > _savedStamp; } }

    /// <summary>Problems found while loading (e.g. a damaged history file), for the UI to show.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    // ---------------------------------------------------------------- creation and loading

    /// <summary>A new training: branch "main" with generation 0 (not evaluated yet) in segment 0.</summary>
    /// <param name="config">Config of the first segment.</param>
    /// <param name="track">The track (copied into the segment).</param>
    /// <param name="seed">Seed of the main branch.</param>
    /// <param name="evolution">Evolution settings.</param>
    /// <param name="generation0">Generation 0 (the runner's population).</param>
    /// <param name="params0">Parameters recorded for generation 0.</param>
    /// <param name="memoryLimit">History memory limit, bytes.</param>
    public static Session Create(SimConfig config, Track track, ulong seed, EvolutionSettings evolution, Population generation0,
        EvolutionParams params0, long memoryLimit = HistoryStore.DefaultMemoryLimit)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(generation0);
        if (config.SimVersion != SimInfo.SimVersion) throw new ArgumentException("A new session needs a config of this SimVersion.", nameof(config));
        var s = new Session(SimInfo.SimVersion, memoryLimit) { _evolution = (evolution ?? new EvolutionSettings()).Clamped() };
        s._branches.Add(new BranchEntry(RootBranch, null, null, seed));
        s._segments.Add(new SegmentEntry(0, RootBranch, 0, config, track, generation0.Size));
        s.History.GetOrAdd(RootBranch);
        s.ActiveBranch = RootBranch;
        s.Current = new PopulationEntry(RootBranch, 0, 0, params0, generation0);
        return s;
    }

    /// <summary>
    /// Loads a session folder. When the newest header cannot be read or contradicts itself, the previous save's
    /// header (header.json.bak) is used and <see cref="Warnings"/> says so.
    /// </summary>
    /// <exception cref="InvalidDataException">Neither header gives a consistent session.</exception>
    public static Session Load(SessionFolder folder, long memoryLimit = HistoryStore.DefaultMemoryLimit)
    {
        ArgumentNullException.ThrowIfNull(folder);
        string stamp = folder.HeaderStamp(); // taken first: a save during the load makes the session look stale (safe), never fresh
        var loaded = folder.Load();
        Session s;
        try
        {
            s = FromSaved(loaded, memoryLimit);
        }
        catch (InvalidDataException e) when (!loaded.FromBackup && File.Exists(folder.BackupHeaderPath))
        {
            s = FromSaved(folder.Load(backupOnly: true, problem: e.Message), memoryLimit);
        }
        s.LoadedHeaderStamp = stamp;
        return s;
    }

    /// <summary>What header.json looked like when <see cref="Load"/> read the folder (null: not loaded from a folder).</summary>
    public string? LoadedHeaderStamp { get; private set; }

    /// <summary>
    /// The session a folder holds (<see cref="SessionFolder.Load"/>). Checks that the header is consistent, puts the
    /// history into the store (thinning it to <paramref name="memoryLimit"/>; thinned chunks are rewritten by the
    /// next save) and remembers what is on disk.
    /// </summary>
    /// <exception cref="InvalidDataException">The header contradicts itself.</exception>
    public static Session FromSaved(LoadedSession loaded, long memoryLimit = HistoryStore.DefaultMemoryLimit)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        var header = loaded.Header;
        var s = new Session(header.SimVersion, memoryLimit) { _evolution = header.Evolution.Clamped(), _loadedFromBackup = loaded.FromBackup };
        s._warnings.AddRange(loaded.Warnings);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in header.Branches)
        {
            if (!names.Add(b.Name)) throw new InvalidDataException($"Session: branch '{b.Name}' appears twice.");
            if (b.Parent is null)
            {
                if (b.ForkGeneration is not null || b.Kind != ForkKind.Root) throw new InvalidDataException($"Session: branch '{b.Name}' has no parent but is a fork.");
            }
            else if (!names.Contains(b.Parent) || b.Kind == ForkKind.Root || b.ForkGeneration is not int fork || fork < 0)
            {
                throw new InvalidDataException($"Session: branch '{b.Name}' has an invalid parent.");
            }
            else if (b.Kind == ForkKind.BestOnly && b.ForkParams is null)
            {
                throw new InvalidDataException($"Session: branch '{b.Name}' is a best-only fork without its parameters.");
            }
            s._branches.Add(b);
        }
        if (s._branches.Count == 0 || s._branches[0].Parent is not null) throw new InvalidDataException("Session: the first branch must be the root.");
        var segmentIds = new HashSet<int>();
        foreach (var seg in header.Segments)
        {
            if (!names.Contains(seg.Branch)) throw new InvalidDataException($"Session: segment {seg.Id} belongs to an unknown branch.");
            if (!segmentIds.Add(seg.Id)) throw new InvalidDataException($"Session: segment {seg.Id} appears twice.");
            s._segments.Add(seg);
        }
        foreach (var b in s._branches)
            if (!s._segments.Any(seg => seg.Branch == b.Name && seg.FirstGeneration <= b.FirstGeneration))
                throw new InvalidDataException($"Session: branch '{b.Name}' has no segment at its first generation.");

        var current = header.Population ?? throw new InvalidDataException("Session: the current population is missing.");
        if (!names.Contains(header.CurrentBranch) || current.Branch != header.CurrentBranch)
            throw new InvalidDataException("Session: the current population is not on the current branch.");
        if (!segmentIds.Contains(current.Segment)) throw new InvalidDataException("Session: the current population's segment is unknown.");
        s.ActiveBranch = header.CurrentBranch;
        s.Current = current;
        if (current.Generation < s.Branch(current.Branch).FirstGeneration) throw new InvalidDataException("Session: the current generation is before its branch starts.");
        if (s.SimVersion == SimInfo.SimVersion && s.SegmentAt(current.Branch, current.Generation).Config.Brain != current.Population.Shape)
            throw new InvalidDataException("Session: the current population does not fit its segment's brain.");

        foreach (var b in s._branches)
        {
            s.History.GetOrAdd(b.Name);
            if (loaded.History.TryGetValue(b.Name, out var entries))
                foreach (var e in entries)
                    if (e.Generation >= b.FirstGeneration) s.History.Add(b.Name, e);
            s._savedEnds[b.Name] = header.HistoryEnds.TryGetValue(b.Name, out int end) ? end : s.HistoryEnd(b.Name);
        }
        s.TrainingSeconds = header.TrainingSeconds;
        s.TotalTicks = header.TotalTicks;
        s.StepMode = header.StepMode;
        s.Speed = header.Speed;
        s.LoadedSavedAtUtc = header.SavedAtUtc;
        s.LoadedFromBackup = loaded.FromBackup;
        s._stamp = 0;
        s.ThinHistory();
        return s;
    }

    // ---------------------------------------------------------------- lookups

    public BranchEntry Branch(string name) =>
        FindBranch(name) ?? throw new ArgumentException($"There is no branch '{name}'.", nameof(name));

    public BranchEntry? FindBranch(string name)
    {
        lock (SyncRoot) return _branches.Find(b => b.Name == name);
    }

    /// <summary>The generation after a branch's last stored one (its first generation when it has none).</summary>
    public int HistoryEnd(string branch)
    {
        lock (SyncRoot)
        {
            var last = History.Find(branch)?.Last;
            return last is null ? Branch(branch).FirstGeneration : last.Generation + 1;
        }
    }

    /// <summary>The branch whose history holds <paramref name="generation"/> in <paramref name="branch"/>'s lineage.</summary>
    public string OwnerOf(string branch, int generation)
    {
        lock (SyncRoot)
        {
            var b = Branch(branch);
            while (generation < b.FirstGeneration && b.Parent is not null) b = Branch(b.Parent);
            if (generation < b.FirstGeneration) throw new ArgumentOutOfRangeException(nameof(generation));
            return b.Name;
        }
    }

    /// <summary>The stored generation <paramref name="generation"/> of <paramref name="branch"/>'s lineage, or null.</summary>
    public HistoryEntry? EntryAt(string branch, int generation)
    {
        lock (SyncRoot) return History.Get(OwnerOf(branch, generation), generation);
    }

    /// <summary>The lineage of a branch, root first: which branch's history holds which generations.</summary>
    public List<LineagePart> Lineage(string branch)
    {
        lock (SyncRoot)
        {
            var parts = new List<LineagePart>();
            var b = Branch(branch);
            int to = HistoryEnd(branch);
            while (true)
            {
                int from = b.FirstGeneration;
                parts.Add(new LineagePart(b.Name, from, Math.Max(from, to)));
                if (b.Parent is null) break;
                to = from;
                b = Branch(b.Parent);
            }
            parts.Reverse();
            return parts;
        }
    }

    /// <summary>The stored generations of a branch's lineage, oldest first (a copy).</summary>
    public List<HistoryEntry> LineageEntries(string branch)
    {
        lock (SyncRoot)
        {
            var result = new List<HistoryEntry>();
            foreach (var part in Lineage(branch))
                foreach (var e in History.Find(part.Branch)?.Entries ?? [])
                    if (e.Generation >= part.From && e.Generation < part.To) result.Add(e);
            return result;
        }
    }

    /// <summary>The segment generation <paramref name="generation"/> of <paramref name="branch"/>'s lineage runs in.</summary>
    public SegmentEntry SegmentAt(string branch, int generation)
    {
        lock (SyncRoot)
        {
            string owner = OwnerOf(branch, generation);
            SegmentEntry? found = null;
            foreach (var s in _segments)
                if (s.Branch == owner && s.FirstGeneration <= generation && (found is null || s.FirstGeneration >= found.FirstGeneration))
                    found = s;
            return found ?? throw new InvalidOperationException($"No segment covers generation {generation} of '{owner}'.");
        }
    }

    public SegmentEntry Segment(int id)
    {
        lock (SyncRoot) return _segments.Find(s => s.Id == id) ?? throw new ArgumentException($"There is no segment {id}.", nameof(id));
    }

    /// <summary>
    /// First generation of the run of generations, in <paramref name="branch"/>'s lineage and ending at
    /// <paramref name="generation"/>, that share its config hash: where best-ever, stagnation and the graph baseline
    /// restart (PLAN §5).
    /// </summary>
    public int SegmentRunStart(string branch, int generation)
    {
        lock (SyncRoot)
        {
            string hash = SegmentAt(branch, generation).Config.Hash;
            int g = generation;
            while (true)
            {
                var seg = SegmentAt(branch, g);
                if (seg.Config.Hash != hash) return g + 1;
                if (seg.FirstGeneration <= 0) return 0;
                g = seg.FirstGeneration - 1;
            }
        }
    }

    /// <summary>
    /// The generations of <paramref name="branch"/>'s lineage where a new segment run starts (its config hash differs
    /// from the generation before), ascending, up to its current generation (the active branch) or its last stored
    /// one: the graph's divider lines (PLAN §5).
    /// </summary>
    public List<int> SegmentRunStarts(string branch)
    {
        lock (SyncRoot)
        {
            int end = HistoryEnd(branch) - 1;
            if (Current.Branch == branch) end = Math.Max(end, Current.Generation);
            var candidates = new SortedSet<int>();
            foreach (var part in Lineage(branch))
            {
                candidates.Add(part.From);
                foreach (var s in _segments)
                    if (s.Branch == part.Branch && s.FirstGeneration >= part.From) candidates.Add(s.FirstGeneration);
            }
            var starts = new List<int>();
            foreach (int g in candidates)
                if (g > 0 && g <= end && SegmentAt(branch, g).Config.Hash != SegmentAt(branch, g - 1).Config.Hash) starts.Add(g);
            return starts;
        }
    }

    /// <summary>Stats of the stored generations from <see cref="SegmentRunStart"/> up to (not including) <paramref name="generation"/>.</summary>
    public List<GenerationStats> SegmentRunStats(string branch, int generation)
    {
        lock (SyncRoot)
        {
            var stats = new List<GenerationStats>();
            for (int g = SegmentRunStart(branch, generation); g < generation; g++)
                if (EntryAt(branch, g) is { } e) stats.Add(e.Stats);
            return stats;
        }
    }

    /// <summary>The parameters that bred a branch's first population.</summary>
    public EvolutionParams RootParams(string branch)
    {
        lock (SyncRoot)
        {
            var b = Branch(branch);
            return b.Kind switch
            {
                ForkKind.BestOnly => b.ForkParams!.Value,
                ForkKind.Full => History.Get(b.Parent!, b.FirstGeneration)?.Params
                                 ?? throw new InvalidOperationException($"The forked generation {b.FirstGeneration} of '{b.Parent}' is missing."),
                _ => History.Get(b.Name, b.FirstGeneration)?.Params ?? EvolutionParams.From(_evolution, false),
            };
        }
    }

    /// <summary>Where a branch continues when it is made active again: its last stored generation (re-evaluated) and the parameters that bred it.</summary>
    public (int Generation, EvolutionParams Params) ResumePoint(string branch)
    {
        lock (SyncRoot)
        {
            var b = Branch(branch);
            var last = History.Find(branch)?.Last;
            return last is null ? (b.FirstGeneration, RootParams(branch)) : (last.Generation, last.Params);
        }
    }

    // ---------------------------------------------------------------- training progress (training thread)

    /// <summary>
    /// Stores the generation that just ended on the active branch and makes <paramref name="next"/> (bred from it)
    /// current. A generation evaluated again (a branch made active resumes at its last stored generation) must
    /// give the stored result bit for bit, and is not stored twice. Thins the history to its memory limit.
    /// </summary>
    /// <exception cref="InvalidOperationException">A re-evaluated generation gave another result (the simulation is not deterministic).</exception>
    public void AddGeneration(GenerationRecord record, Population next, EvolutionParams nextParams)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(next);
        lock (SyncRoot)
        {
            CheckWritable();
            if (record.Generation != Current.Generation)
                throw new ArgumentException($"Generation {record.Generation} ended, but generation {Current.Generation} is current.", nameof(record));
            var entry = HistoryEntry.From(record);
            var history = History.GetOrAdd(ActiveBranch);
            var existing = history.Get(entry.Generation);
            if (existing is null)
            {
                History.Add(ActiveBranch, entry);
            }
            else if (!existing.SameResultAs(entry))
            {
                throw new InvalidOperationException($"Generation {entry.Generation} of '{ActiveBranch}' gave another result when evaluated again: the simulation is not deterministic.");
            }
            Current = new PopulationEntry(ActiveBranch, record.Generation + 1, Current.Segment, nextParams, next);
            ThinHistory();
            Changed();
        }
    }

    /// <summary>
    /// Starts a new segment on the active branch at the current generation (a SimConfig change at a generation
    /// boundary, PLAN §5). A segment that starts at the same generation and has not run yet is replaced. Returns it.
    /// </summary>
    public SegmentEntry StartSegment(SimConfig config, Track track, int populationSize)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(track);
        lock (SyncRoot)
        {
            CheckWritable();
            int g = Current.Generation;
            if (History.Get(ActiveBranch, g) is not null)
                throw new InvalidOperationException($"Generation {g} of '{ActiveBranch}' already ran; a new segment starts at the next one.");
            int index = _segments.FindLastIndex(s => s.Branch == ActiveBranch && s.FirstGeneration == g);
            int id = index >= 0 ? _segments[index].Id : NextSegmentId();
            var segment = new SegmentEntry(id, ActiveBranch, g, config, track, populationSize);
            if (index >= 0) _segments[index] = segment;
            else _segments.Add(segment);
            Current = Current with { Segment = id };
            Changed();
            return segment;
        }
    }

    /// <summary>
    /// Creates a branch forked from generation <paramref name="generation"/> of <paramref name="fromBranch"/>'s lineage
    /// (it must have been evaluated) and makes it active; its first, not yet evaluated, generation is
    /// <paramref name="generation"/> (PLAN §5 "Fork"):
    /// <list type="bullet">
    /// <item><see cref="ForkKind.Full"/>: <paramref name="population"/> is that generation's full population (the
    /// caller rebuilds it with <see cref="Rebuilder"/>);</item>
    /// <item><see cref="ForkKind.BestOnly"/>: its best genome plus mutated copies (<see cref="Breeder.FromBest"/> with
    /// <paramref name="forkParams"/> and the new branch's seed), built here.</item>
    /// </list>
    /// </summary>
    public BranchEntry CreateFork(string fromBranch, int generation, ForkKind kind, Population? population = null,
        EvolutionParams? forkParams = null, ulong? seed = null)
    {
        lock (SyncRoot)
        {
            CheckWritable();
            if (kind == ForkKind.Root) throw new ArgumentException("A fork is Full or BestOnly.", nameof(kind));
            string owner = OwnerOf(fromBranch, generation);
            var entry = History.Get(owner, generation)
                        ?? throw new ArgumentException($"Generation {generation} of '{owner}' has not been evaluated, so it cannot be forked.", nameof(generation));
            var source = SegmentAt(owner, generation);
            int size = source.PopulationSize > 0 ? source.PopulationSize : Current.Population.Size;
            ulong branchSeed = seed ?? SeedHash.Derive(_branches[0].Seed, ForkSeedTag + (ulong)_branches.Count);
            EvolutionParams firstParams;
            if (kind == ForkKind.Full)
            {
                ArgumentNullException.ThrowIfNull(population);
                if (population.Shape != source.Config.Brain || population.Size != size)
                    throw new ArgumentException("The population does not fit the forked generation.", nameof(population));
                firstParams = entry.Params;
            }
            else
            {
                firstParams = forkParams ?? EvolutionParams.From(_evolution, boost: false);
                population = new Population(source.Config.Brain, size);
                Breeder.FromBest(entry.BestGenome.Weights, population, firstParams, SeedHash.Derive(branchSeed, (ulong)generation));
            }
            string name = NewBranchName();
            var branch = new BranchEntry(name, owner, generation, branchSeed, kind, kind == ForkKind.BestOnly ? firstParams : null);
            _branches.Add(branch);
            History.GetOrAdd(name);
            int id = NextSegmentId();
            _segments.Add(new SegmentEntry(id, name, generation, source.Config, source.Track, size));
            ActiveBranch = name;
            Current = new PopulationEntry(name, generation, id, firstParams, population);
            Changed();
            return branch;
        }
    }

    /// <summary>
    /// Makes another existing branch active, continuing from its <see cref="ResumePoint"/> with
    /// <paramref name="population"/> (rebuilt by the caller with <see cref="Rebuilder"/>).
    /// </summary>
    public void Activate(string branch, Population population)
    {
        ArgumentNullException.ThrowIfNull(population);
        lock (SyncRoot)
        {
            CheckWritable();
            var (generation, parameters) = ResumePoint(branch);
            var segment = SegmentAt(branch, generation);
            if (population.Shape != segment.Config.Brain) throw new ArgumentException("The population does not fit the segment's brain.", nameof(population));
            ActiveBranch = branch;
            Current = new PopulationEntry(branch, generation, segment.Id, parameters, population);
            Changed();
        }
    }

    /// <summary>
    /// Changes the "History memory limit" (PLAN §4, a live setting) and thins the history to it at once; chunks
    /// already on disk that lost snapshots are rewritten by the next save.
    /// </summary>
    public void SetMemoryLimit(long bytes)
    {
        lock (SyncRoot)
        {
            History.MemoryLimit = Math.Max(0, bytes);
            if (ThinHistory() > 0) Changed();
        }
    }

    // ---------------------------------------------------------------- saving

    /// <summary>
    /// Copies what the next save writes (PLAN §5.1): the header with its own copy of the current population, and every
    /// chunk window that holds unsaved generations or was thinned since it was saved. Call it on the training thread
    /// (the current population's weights are the runner's).
    /// </summary>
    public SavePlan PrepareSave(DateTime nowUtc)
    {
        lock (SyncRoot)
        {
            CheckWritable();
            var cur = Current;
            var header = new SessionHeader
            {
                SimVersion = SimVersion,
                CurrentBranch = ActiveBranch,
                Evolution = _evolution,
                Segments = [.. _segments],
                Branches = [.. _branches],
                Population = cur with { Population = new Population(cur.Population.Shape, (float[])cur.Population.Weights.Clone()) },
                SavedAtUtc = nowUtc,
                TrainingSeconds = TrainingSeconds,
                TotalTicks = TotalTicks,
                StepMode = StepMode,
                Speed = Speed,
            };
            var windows = new List<WindowWrite>();
            var newBranches = new List<string>();
            foreach (var b in _branches)
            {
                int first = b.FirstGeneration, end = HistoryEnd(b.Name);
                header.HistoryEnds[b.Name] = end;
                _plannedEnds[b.Name] = end;
                bool saved = _savedEnds.TryGetValue(b.Name, out int savedEnd);
                if (!saved) newBranches.Add(b.Name);
                var touched = new SortedSet<int>();
                for (int g = saved ? Math.Max(savedEnd, first) : first; g < end; g = (g / SessionFolder.ChunkSize + 1) * SessionFolder.ChunkSize)
                    touched.Add(g / SessionFolder.ChunkSize);
                foreach (var ((branch, window), _) in _dirty)
                    if (branch == b.Name) touched.Add(window);
                var history = History.Find(b.Name);
                foreach (int w in touched)
                {
                    int lo = Math.Max(w * SessionFolder.ChunkSize, first), hi = Math.Min(w * SessionFolder.ChunkSize + SessionFolder.ChunkSize - 1, end - 1);
                    var entries = new List<HistoryEntry>();
                    if (history is not null)
                        foreach (var e in history.Entries)
                            if (e.Generation >= lo && e.Generation <= hi) entries.Add(e);
                    windows.Add(new WindowWrite(b.Name, w, first, end, entries));
                }
            }
            return new SavePlan(header, windows, newBranches, _stamp) { ReplacesDamagedHeader = _loadedFromBackup };
        }
    }

    /// <summary>Records a finished save: its history ends are on disk, and windows thinned before it was planned are clean.</summary>
    public void MarkSaved(SavePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        lock (SyncRoot)
        {
            foreach (var (branch, end) in plan.Header.HistoryEnds)
                _savedEnds[branch] = _savedEnds.TryGetValue(branch, out int old) ? Math.Max(old, end) : end;
            foreach (var key in _dirty.Where(d => d.Value <= plan.Stamp).Select(d => d.Key).ToList()) _dirty.Remove(key);
            _savedStamp = Math.Max(_savedStamp, plan.Stamp);
            if (plan.ReplacesDamagedHeader) _loadedFromBackup = false;
        }
    }

    // ---------------------------------------------------------------- internals

    private int ThinHistory() =>
        History.Thin((branch, generation) =>
        {
            // A window already on disk (or being written by a save planned earlier) must be rewritten without the
            // snapshot (PLAN §5.1); newer ones are written thinned anyway.
            if ((_savedEnds.TryGetValue(branch, out int end) && generation < end) || (_plannedEnds.TryGetValue(branch, out end) && generation < end))
                _dirty[(branch, generation / SessionFolder.ChunkSize)] = ++_stamp;
        });

    private void Changed() => _stamp++;

    private void CheckWritable()
    {
        if (ReadOnly) throw new InvalidOperationException(Compatibility.Message);
    }

    private int NextSegmentId() => _segments.Count == 0 ? 0 : _segments.Max(s => s.Id) + 1;

    private string NewBranchName()
    {
        for (int n = _branches.Count; ; n++)
        {
            string name = $"fork-{n}";
            if (_branches.TrueForAll(b => b.Name != name)) return name;
        }
    }
}
