using System.Diagnostics;
using Nitrogenesis.Sim.Brain;
using Nitrogenesis.Sim.Core;
using Nitrogenesis.Sim.Evolution;
using Nitrogenesis.Sim.History;
using Nitrogenesis.Sim.Io;
using Nitrogenesis.Sim.Map;
using Nitrogenesis.Sim.Rng;

namespace Nitrogenesis.Sim.Racing;

/// <summary>What a <see cref="TrainingHost"/> trains with.</summary>
public sealed record TrainingHostOptions
{
    /// <summary>Simulation threads W with visuals (the host's own scheduler thread is one of them).</summary>
    public int Threads { get; init; } = 1;
    /// <summary>Simulation threads while training without visuals (PLAN §2.1: logical threads − 1); 0 = <see cref="Threads"/>.</summary>
    public int HeadlessThreads { get; init; }
    public int PopulationSize { get; init; } = EvolutionSettings.DefaultPopulationSize;
    /// <summary>First hidden layer of a new training's brain (4…32).</summary>
    public int Hidden1 { get; init; } = BrainShape.DefaultHidden;
    /// <summary>Second hidden layer (4…32), or 0 for none.</summary>
    public int Hidden2 { get; init; }
    public ulong Seed { get; init; } = 1;
    public RacingSettings Settings { get; init; } = new();
    public EvolutionSettings? Evolution { get; init; }
    /// <summary>Starting speed (snapped to the nearest step; +∞ = MAX).</summary>
    public double Speed { get; init; } = 1;
    /// <summary>Where the session is saved (autosave and on exit), or null for a training that is not saved.</summary>
    public SessionFolder? Folder { get; init; }
    /// <summary>Time between autosaves (PLAN §5.1: 2 min). Zero saves whenever the previous save has finished.</summary>
    public TimeSpan AutosaveInterval { get; init; } = TimeSpan.FromMinutes(2);
    /// <summary>"History memory limit" (PLAN §4), bytes.</summary>
    public long HistoryMemoryLimit { get; init; } = HistoryStore.DefaultMemoryLimit;
}

/// <summary>A copy of the host's state for the HUD and the view (read with <see cref="TrainingHost.ReadStats"/>).</summary>
public struct TrainingStats
{
    /// <summary>The track is prepared and the first generation runs.</summary>
    public bool Ready;
    /// <summary>Why the simulation stopped, or null.</summary>
    public string? Error;
    public int Generation, PopulationSize, Threads;
    public int Alive, Finished;
    public long ElapsedTicks;
    public int TimeLimitTicks;
    /// <summary>Best progress 0…1 of this generation so far.</summary>
    public float BestProgress;
    /// <summary>Fastest finish ever in this segment (PLAN §5: resets with a new segment), in ticks; +∞ when no car has finished yet.</summary>
    public float BestTimeTicks;
    /// <summary>
    /// The leading car still on the track (running or finished) by score, or −1. It changes only when another car
    /// leads by more than <see cref="TrainingHost.LeaderMargin"/> (or the leader leaves the track), so the camera
    /// does not flick between cars that are level.
    /// </summary>
    public int Leader;
    public double TargetSpeed, ActualSpeed;
    public int SpeedIndex;
    public bool Paused;
    /// <summary>Render alpha and whether to interpolate (PLAN §4).</summary>
    public float Alpha;
    public bool Interpolates;
    /// <summary>Real time spent training (not paused), seconds; carried over when a session is resumed.</summary>
    public double TrainingSeconds;
    /// <summary>Ticks run since the start (all generations).</summary>
    public long TotalTicks;
    /// <summary>Ticks and seconds of simulation work in the last frame.</summary>
    public int TicksLastFrame;
    public double SimSecondsLastFrame;

    // ---- M3: training control

    /// <summary>The active branch and the current segment's id.</summary>
    public string? Branch;
    public int Segment;
    /// <summary>The current population was bred with the stagnation boost ("Boost active" badge).</summary>
    public bool BoostActive;
    /// <summary>Generation whose end started the last boost (for the banner), or −1.</summary>
    public int BoostStartedAt;
    /// <summary>Mode Step (PLAN §6.2): pause at the end of each generation.</summary>
    public bool StepMode;
    /// <summary>Paused at a generation end in Step mode, waiting for "Next".</summary>
    public bool WaitingForNext;
    /// <summary>The last generation that ended (−1 = none yet) and its stats (Step mode's summary).</summary>
    public int LastGeneration;
    public GenerationStats LastStats;
    /// <summary>Training without visuals (PLAN §4) and what it waits for.</summary>
    public bool Headless;
    public TrainToCondition TrainTo;
    /// <summary>Generations finished per second while training without visuals.</summary>
    public double GenerationsPerSecond;
    /// <summary>Runs of training without visuals that ended so far (met, cancelled or not needed): the view switches back when it moves.</summary>
    public int TrainToRuns;
    /// <summary>A long operation in progress (e.g. "Rebuilding generation 120…") and its progress 0…1, or null.</summary>
    public string? Busy;
    public float BusyProgress;
    /// <summary>The result of the last fork / branch switch / train-to request when it could not be done, or null.</summary>
    public string? Notice;
    /// <summary>Goes up with every new <see cref="Notice"/> (the view shows each one, also a repeated text).</summary>
    public int NoticeCount;
    /// <summary>Goes up when the graph data changes (<see cref="TrainingHost.CopyGraph"/>).</summary>
    public int GraphVersion;
    /// <summary>Saving: when the last save was planned (UTC; for a resumed session the save it was loaded from until this run saves; default = never), a save is running, why the last one failed.</summary>
    public DateTime SavedAtUtc;
    public bool Saving;
    public string? SaveError;
    /// <summary>Memory the history uses and its limit, bytes.</summary>
    public long HistoryBytes, HistoryLimit;
    /// <summary>The watched car's forward speed (cells/s) and the top speed of the running segment, for the engine sound.</summary>
    public float WatchedSpeed, MaxSpeed;
}

/// <summary>
/// Runs racing training (PLAN §2.1, §4, §5): a <see cref="GenerationRunner"/> on its own scheduler thread with W
/// workers, over a <see cref="History.Session"/> that keeps the history, branches and segments, saved by a
/// <see cref="SessionSaver"/>.
/// </summary>
/// <remarks>
/// <para><b>With visuals</b> the render thread calls <see cref="Frame"/> once per frame with the real frame time. That
/// adds game time to the speed controller and wakes the scheduler thread, which runs the due whole ticks within the
/// 14 ms frame budget and publishes snapshots (<see cref="Snapshots"/>). At 1× and slower it runs one tick per
/// <see cref="GenerationRunner.RunTicks"/>, so prev/curr are always one tick apart; faster, it sizes its pieces
/// from the measured cost per agent-tick. Ticks left over when the budget runs out stay due (up to the controller's
/// backlog cap), and the measured speed drops instead: "100× (actual 64×)".</para>
/// <para><b>Without visuals</b> (<see cref="TrainTo"/>) the scheduler thread runs generations back to back at full
/// speed with the headless thread count until the stop condition is met, then goes back to frames.</para>
/// <para><b>Control</b> (PLAN §6.2): Mode Auto/Step (<see cref="StepMode"/>, <see cref="Next"/>), "Next generation"
/// (<see cref="EndGenerationNow"/>), evolution settings at the next boundary (<see cref="SetEvolution"/>), a
/// SimConfig change as a new segment at the next boundary (<see cref="ChangeSettings"/>), forks and branch switches
/// (<see cref="Fork"/>, <see cref="SwitchBranch"/>). Requests from other threads are queued and run on the scheduler
/// thread between simulation steps.</para>
/// <para><b>Saving</b> (PLAN §5.1): with a <see cref="TrainingHostOptions.Folder"/>, a new session is saved at once,
/// then every <see cref="TrainingHostOptions.AutosaveInterval"/> and on <see cref="Dispose"/>, on the saver thread
/// from a copied snapshot. The session folder's lock is held while the host lives.</para>
/// <para>Results are bit-identical to <see cref="GenerationRunner.RunGeneration"/>: ticks are only split differently.</para>
/// </remarks>
public sealed class TrainingHost : IDisposable
{
    /// <summary>Ticks per piece while training without visuals (short, so requests and autosaves are not held up).</summary>
    public const int HeadlessPieceTicks = 120;

    /// <summary>How long the scheduler thread sleeps with nothing to do before it checks the autosave.</summary>
    private static readonly TimeSpan IdleAutosaveCheck = TimeSpan.FromSeconds(1);

    private readonly object _lock = new();
    private readonly SpeedController _speed = new();
    private readonly SpeedMeter _meter = new();
    private readonly Track? _track;
    private TrainingHostOptions _options; // Threads, HeadlessThreads and HistoryMemoryLimit change while it runs (under _lock)
    private readonly Thread _thread;
    private readonly IDisposable? _folderLock;
    private volatile bool _stop;
    private RacingTraining? _training;
    private Session? _session;
    private SessionSaver? _saver;

    // Under _lock.
    private long _requested, _served;
    private TrainingStats _stats;
    private double _trainingSeconds;
    private readonly Queue<Action> _commands = new();
    private bool _headless, _cancelHeadless, _waitingForNext, _stepMode;
    private RacingSettings? _pendingSettings;
    private int? _pendingPopulation;
    private CancellationTokenSource? _busyCancel;
    private int _trainToThreads;
    // Train-to requests are numbered when queued; a cancel drops every request queued up to then, also one the
    // scheduler thread has not started yet (it would otherwise start after the cancel and never stop).
    private int _trainToTicket, _cancelledTicket;
    private readonly ScoreGraph _graph = new();

    // Route of the watched car (under _lock): written by the scheduler thread after each frame, copied by the view.
    private int _watched = -1;
    private bool _watchChanged;
    private float[] _trailX = Array.Empty<float>(), _trailY = Array.Empty<float>();
    private int _trailCount, _trailAgent = -1, _trailGeneration = -1;

    // The watched car's brain (under _lock): its genome, and the inputs, hidden and output values of the last tick.
    private float[] _brainValues = Array.Empty<float>(), _brainWeights = Array.Empty<float>();
    private BrainShape _brainShape;
    private int _brainAgent = -1, _brainGeneration = -1, _brainVersion;
    private RacingTraining? _brainTraining;

    // Who bred each car of the generation that begins (under _lock; for the family colours).
    private int[] _parents = Array.Empty<int>();
    private int _parentsGeneration = -1, _parentsElites;

    // Scheduler thread only.
    private double _agentTickSeconds = 2e-6; // running estimate of one agent-tick's cost (wall time), seconds
    private float _bestTimeTicks = float.PositiveInfinity;
    private int _leader = -1, _leaderGeneration = -1;
    private long _lastSave;
    private long _rateStart;
    private int _rateGenerations;

    /// <summary>Score lead (≈ progress fraction, 0.01 = 1 % of the track) another car needs to take over as the leader.</summary>
    public const float LeaderMargin = 0.01f;

    /// <summary>Starts a new training on <paramref name="track"/> (saved in <see cref="TrainingHostOptions.Folder"/> if given).</summary>
    /// <exception cref="SessionLockedException">The folder's session is open in another window.</exception>
    public TrainingHost(Track track, TrainingHostOptions options) : this(track, null, options) { }

    /// <summary>Continues a loaded session (<see cref="Session.Load"/>); <see cref="TrainingHostOptions.Folder"/> should be its folder.</summary>
    /// <exception cref="ArgumentException">The session is read-only (another SimVersion, PLAN §5.1).</exception>
    /// <exception cref="SessionLockedException">The session is open in another window.</exception>
    public TrainingHost(Session session, TrainingHostOptions options) : this(null, session, options) { }

    private TrainingHost(Track? track, Session? session, TrainingHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (track is null && session is null) throw new ArgumentNullException(nameof(track));
        if (session is { ReadOnly: true }) throw new ArgumentException(session.Compatibility.Message, nameof(session));
        _track = track;
        _session = session;
        _options = options;
        // A loaded session may be stale by now (loading takes seconds): only lock it if the save on disk is still its own.
        _folderLock = session is not null && (session.LoadedHeaderStamp is not null || !session.LoadedSavedAtUtc.Equals(default))
            ? options.Folder?.AcquireLockForLoaded(session.LoadedSavedAtUtc, session.LoadedFromBackup, session.LoadedHeaderStamp)
            : options.Folder?.AcquireLock();
        _speed.Index = SpeedController.IndexFor(options.Speed);
        _stats.Leader = -1;
        _stats.BestTimeTicks = float.PositiveInfinity;
        _stats.LastGeneration = -1;
        _stats.BoostStartedAt = -1;
        _stats.HistoryLimit = options.HistoryMemoryLimit;
        if (session is not null)
        {
            _trainingSeconds = session.TrainingSeconds;
            _stats.TotalTicks = session.TotalTicks;
            // Mode and speed where the user left them (a file from before M3 does not say: the options count).
            if (session.StepMode is { } stepMode) _stepMode = stepMode;
            // Resumed in Step mode: the generation waits at its start for Next/Resume, as at the end of any generation.
            if (_stepMode)
            {
                _speed.Paused = true;
                // The summary shows the generation before the current one (none at generation 0 or a fork's first one: then no wait).
                if (session.Current.Generation > session.Branch(session.Current.Branch).FirstGeneration
                    && session.EntryAt(session.Current.Branch, session.Current.Generation - 1) is { } last)
                {
                    _stats.LastGeneration = last.Generation;
                    _stats.LastStats = last.Stats;
                    _waitingForNext = true;
                }
            }
            if (session.Speed is { } speed) _speed.Index = SpeedController.IndexFor(speed);
        }
        _thread = new Thread(Loop) { IsBackground = true, Name = "Sim scheduler" };
        _thread.Start();
    }

    /// <summary>The training, once prepared (null before; the reference driver runs first on the scheduler thread). Replaced by forks, branch switches and new segments.</summary>
    public RacingTraining? Training => Volatile.Read(ref _training);

    /// <summary>The session (null until prepared for a new training).</summary>
    public Session? Session => Volatile.Read(ref _session);

    /// <summary>The snapshots the view reads (null until prepared).</summary>
    public SnapshotBuffer? Snapshots => Training?.Runner.Snapshots;

    public int PopulationSize => _session?.Current.Population.Size ?? _options.PopulationSize;

    private int HeadlessThreads => _options.HeadlessThreads > 0 ? _options.HeadlessThreads : _options.Threads;

    /// <summary>Threads of the running training without visuals (the headless count unless <see cref="TrainTo"/> was given another).</summary>
    private int TrainToThreads => _trainToThreads > 0 ? _trainToThreads : HeadlessThreads;

    /// <summary>Raised on the scheduler thread after each generation ends (and is stored), before the next one begins.</summary>
    public event Action<GenerationRecord>? GenerationEnded;

    // ---------------------------------------------------------------- frames (render thread)

    /// <summary>
    /// Called by the render thread once per frame with the real time since the last frame: adds game time and
    /// wakes the simulation if whole ticks are due. Never blocks. Returns true when it asked for ticks at an
    /// interpolating speed (1× or slower), where the view should <see cref="WaitForFrame"/> before it draws.
    /// While training without visuals it only counts the training time.
    /// </summary>
    public bool Frame(double realSeconds)
    {
        lock (_lock)
        {
            if (!_stats.Ready) return false; // no game time piles up while the track is prepared
            if (_headless)
            {
                if (realSeconds > 0) _trainingSeconds += realSeconds;
                return false;
            }
            _speed.Advance(realSeconds);
            bool running = _stats.Error is null && !_speed.Paused && _stats.Busy is null;
            _meter.AddTime(realSeconds, !running, _speed.TargetSpeed);
            if (running && realSeconds > 0) _trainingSeconds += realSeconds;
            if (_stats.Error is not null) return false;
            bool ticks = _speed.WholeTicks > 0;
            if (!ticks && !_watchChanged) return false;
            _watchChanged = false;
            _requested++; // a frame without due ticks (paused, new watched car) only refreshes the stats and the route
            Monitor.PulseAll(_lock);
            return ticks && _speed.Interpolates;
        }
    }

    /// <summary>Waits at most <paramref name="timeout"/> for the work requested by the last <see cref="Frame"/>. Returns true when done.</summary>
    public bool WaitForFrame(TimeSpan timeout)
    {
        long deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        lock (_lock)
        {
            long target = _requested;
            while (_served < target && _stats.Error is null && !_stop)
            {
                long left = deadline - Stopwatch.GetTimestamp();
                if (left <= 0) return false;
                Monitor.Wait(_lock, TimeSpan.FromSeconds((double)left / Stopwatch.Frequency));
            }
            return true;
        }
    }

    public void Faster()
    {
        lock (_lock) _speed.Faster();
    }

    public void Slower()
    {
        lock (_lock) _speed.Slower();
    }

    public void SetSpeed(double speed)
    {
        lock (_lock) _speed.Index = SpeedController.IndexFor(speed);
    }

    public void TogglePause()
    {
        lock (_lock)
        {
            _speed.Paused = !_speed.Paused;
            if (!_speed.Paused) _waitingForNext = false;
        }
    }

    /// <summary>Pauses or resumes (e.g. while a replay is watched, and back to how it was after).</summary>
    public void SetPaused(bool paused)
    {
        lock (_lock)
        {
            _speed.Paused = paused;
            if (!paused) _waitingForNext = false;
        }
    }

    /// <summary>One tick while paused (it runs at the next <see cref="Frame"/>).</summary>
    public void StepTick()
    {
        lock (_lock) _speed.StepOnce();
    }

    // ---------------------------------------------------------------- training control (any thread)

    /// <summary>Mode Step (true) pauses at the end of each generation with its summary until <see cref="Next"/>; Auto (false) runs on.</summary>
    public bool StepMode
    {
        get { lock (_lock) return _stepMode; }
        set { lock (_lock) _stepMode = value; }
    }

    /// <summary>Step mode's "Next": starts the generation waiting after the summary (the same as unpausing).</summary>
    public void Next()
    {
        lock (_lock)
        {
            _waitingForNext = false;
            _speed.Paused = false;
        }
    }

    /// <summary>"Next generation" (PLAN §6.2): ends the running generation now and scores the cars where they are.</summary>
    public void EndGenerationNow() => Enqueue(() =>
    {
        var runner = _training!.Runner;
        if (runner.IsRunning) Committed(() => NextGeneration());
    });

    /// <summary>New evolution settings (PLAN §3.7, §6.4 "Live"): they apply from the next generation boundary and are saved with the session.</summary>
    public void SetEvolution(EvolutionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Enqueue(() =>
        {
            _session!.Evolution = settings;
            _training!.Runner.Settings = settings;
        });
    }

    /// <summary>
    /// New racing settings (PLAN §6.4 "New segment"): at the next generation boundary the population continues under
    /// them as a new segment (best time, stagnation and the graph baseline restart). Nothing happens when they give
    /// the same SimConfig.
    /// </summary>
    public void ChangeSettings(RacingSettings settings, int? populationSize = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_lock)
        {
            _pendingSettings = settings.Clamped();
            _pendingPopulation = populationSize is { } n ? Math.Max(1, n) : null;
        }
    }

    /// <summary>
    /// The change <see cref="ChangeSettings"/> queued for the next generation boundary, or null: a second change before then
    /// replaces it, so it should start from these values (else the first one would be lost).
    /// </summary>
    public (RacingSettings Settings, int? PopulationSize)? PendingChange
    {
        get
        {
            lock (_lock) return _pendingSettings is { } s ? (s, _pendingPopulation) : null;
        }
    }

    /// <summary>
    /// New "Simulation threads" (PLAN §4, a live setting): with visuals and without; 0 for <paramref name="headless"/> = the
    /// same as <paramref name="visual"/>. Applies at once (results do not depend on the thread count).
    /// </summary>
    public void SetThreads(int visual, int headless) => Enqueue(() =>
    {
        lock (_lock) _options = _options with { Threads = Math.Max(1, visual), HeadlessThreads = Math.Max(0, headless) };
        _training?.Runner.SetThreads(CurrentThreads());
    });

    /// <summary>New "History memory limit" (PLAN §4, a live setting): the history is thinned to it at once.</summary>
    public void SetHistoryLimit(long bytes) => Enqueue(() =>
    {
        lock (_lock)
        {
            _options = _options with { HistoryMemoryLimit = bytes };
            _stats.HistoryLimit = bytes;
        }
        _session?.SetMemoryLimit(bytes);
    });

    /// <summary>
    /// Trains without visuals (PLAN §4) until <paramref name="condition"/> is met, at full speed with the headless
    /// thread count (or <paramref name="threads"/>, e.g. while a replay is watched); then frames drive the view
    /// again. The running generation continues where it is. Step mode does not pause it.
    /// </summary>
    public void TrainTo(TrainToCondition condition, int threads = 0)
    {
        int ticket;
        lock (_lock) ticket = ++_trainToTicket;
        Enqueue(() => StartTrainTo(condition, threads, ticket));
    }

    private void StartTrainTo(TrainToCondition condition, int threads, int ticket)
    {
        lock (_lock)
        {
            if (ticket <= _cancelledTicket)
            {
                _stats.TrainToRuns++; // cancelled before it started: this run ends at once
                return;
            }
        }
        var runner = _training!.Runner;
        if (condition.IsMetAt(runner.Generation))
        {
            lock (_lock)
            {
                _stats.Notice = $"Already at generation {runner.Generation}.";
                _stats.NoticeCount++;
                _stats.TrainToRuns++;
            }
            return;
        }
        _trainToThreads = Math.Max(0, threads);
        runner.SetThreads(TrainToThreads);
        _rateStart = Stopwatch.GetTimestamp();
        _rateGenerations = 0;
        lock (_lock)
        {
            _headless = true;
            _cancelHeadless = false;
            _waitingForNext = false;
            _stats.Headless = true;
            _stats.TrainTo = condition;
            _stats.GenerationsPerSecond = 0;
            _stats.Notice = null;
        }
    }

    /// <summary>Stops training without visuals now (the running generation goes on in the view); a train-to still queued never starts.</summary>
    public void CancelTrainTo()
    {
        lock (_lock)
        {
            _cancelHeadless = true;
            _cancelledTicket = _trainToTicket;
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>
    /// "Fork" (PLAN §5): a new branch from generation <paramref name="generation"/> of <paramref name="branch"/>'s
    /// lineage (an evaluated generation), made active. <see cref="ForkKind.Full"/> rebuilds that generation's full
    /// population if its snapshot was thinned (progress in <see cref="TrainingStats.Busy"/>);
    /// <see cref="ForkKind.BestOnly"/> uses its best genome. <paramref name="evolution"/>, if given, becomes the
    /// evolution settings from then on.
    /// </summary>
    public void Fork(string branch, int generation, ForkKind kind, EvolutionSettings? evolution = null) => Enqueue(() =>
    {
        var session = _session!;
        RefuseWhileHeadless("fork");
        Population? population = kind == ForkKind.Full ? RebuildForUse(branch, generation) : null;
        if (kind == ForkKind.Full && population is null) return; // cancelled: nothing changed
        session.CreateFork(branch, generation, kind, population,
            kind == ForkKind.BestOnly ? EvolutionParams.From((evolution ?? session.Evolution).Clamped(), boost: false) : null);
        if (evolution is not null) session.Evolution = evolution; // only once the fork is made (a refused fork changes nothing)
        Committed(() =>
        {
            ReplaceTraining(RacingTraining.ResumeFrom(session, CurrentThreads()));
            _training!.Runner.BeginGeneration();
            ForgetOtherBranchSummary();
        });
    });

    /// <summary>Makes another branch active (PLAN §5 "one is active"); it continues from its last stored generation.</summary>
    public void SwitchBranch(string branch) => Enqueue(() =>
    {
        var session = _session!;
        RefuseWhileHeadless("switch branches");
        if (branch == session.ActiveBranch) throw new InvalidOperationException($"'{branch}' is already the active branch.");
        var (generation, _) = session.ResumePoint(branch);
        var population = RebuildForUse(branch, generation);
        if (population is null) return;
        session.Activate(branch, population);
        Committed(() =>
        {
            ReplaceTraining(RacingTraining.ResumeFrom(session, CurrentThreads()));
            _training!.Runner.BeginGeneration();
            ForgetOtherBranchSummary();
        });
    });

    /// <summary>A fork or branch switch while training without visuals (a train-to) runs would pull the population out from under it: refused with a notice.</summary>
    private void RefuseWhileHeadless(string what)
    {
        bool headless;
        lock (_lock) headless = _headless;
        if (headless) throw new InvalidOperationException($"Cannot {what} while training without visuals runs; cancel it first.");
    }

    /// <summary>After a fork or branch switch the last-generation summary and the boost marker belong to the other branch.</summary>
    private void ForgetOtherBranchSummary()
    {
        lock (_lock)
        {
            _stats.LastGeneration = -1;
            _stats.BoostStartedAt = -1;
        }
    }

    /// <summary>Stops a long operation (a rebuild for a fork or branch switch).</summary>
    public void CancelBusy()
    {
        lock (_lock) _busyCancel?.Cancel();
    }

    /// <summary>Saves now (on the saver thread), e.g. before leaving the view.</summary>
    public void SaveNow() => Enqueue(() => Autosave(force: true));

    // ---------------------------------------------------------------- watched car

    /// <summary>
    /// The car whose route trail the view draws (−1 = none). The route is refreshed after every simulation frame,
    /// and at the next <see cref="Frame"/> when the car changes (also while paused).
    /// </summary>
    public void Watch(int agent)
    {
        lock (_lock)
        {
            if (agent == _watched) return;
            _watched = agent;
            _watchChanged = true;
        }
    }

    /// <summary>
    /// Copies the watched car's route (its centre at the start and every few ticks, see <see cref="RouteTrails"/>)
    /// into the caller's arrays (at least <see cref="RouteTrails.MaxSamplesPerCar"/> long) and returns the number
    /// of points; 0 when there is none yet. <paramref name="agent"/> and <paramref name="generation"/> say whose
    /// route it is. Does not allocate.
    /// </summary>
    public int CopyTrail(float[] x, float[] y, out int agent, out int generation)
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);
        lock (_lock)
        {
            agent = _trailAgent;
            generation = _trailGeneration;
            int count = Math.Min(_trailCount, Math.Min(x.Length, y.Length));
            Array.Copy(_trailX, x, count);
            Array.Copy(_trailY, y, count);
            return count;
        }
    }

    /// <summary>
    /// Copies the watched car's brain (PLAN §6.2 "live brain view"): its genome into <paramref name="weights"/> and the values of
    /// the last tick into <paramref name="values"/> (inputs, then each hidden layer, then outputs: <see cref="BrainProbe"/>).
    /// Returns the number of values, 0 when there is none. <paramref name="version"/> changes when the car, generation or
    /// shape changed (the genome is the same while it stays). Does not allocate when the arrays are big enough.
    /// </summary>
    public int CopyBrain(float[] values, float[] weights, out BrainShape shape, out int agent, out int generation, out int version)
    {
        lock (_lock)
        {
            shape = _brainShape;
            agent = _brainAgent;
            generation = _brainGeneration;
            version = _brainVersion;
            if (_brainAgent < 0 || values.Length < _brainValues.Length || weights.Length < _brainWeights.Length) return 0;
            Array.Copy(_brainValues, values, _brainValues.Length);
            Array.Copy(_brainWeights, weights, _brainWeights.Length);
            return _brainValues.Length;
        }
    }

    /// <summary>
    /// Who bred each car of the generation <paramref name="generation"/> that just began (see <see cref="Breeder.Parents"/>), copied into
    /// <paramref name="parents"/>. Returns the number of cars, 0 when it is not known (a fork, a resume, a resized population).
    /// </summary>
    public int CopyParents(int[] parents, out int generation, out int elites)
    {
        lock (_lock)
        {
            generation = _parentsGeneration;
            elites = _parentsElites;
            if (_parentsGeneration < 0 || parents.Length < _parents.Length) return 0;
            Array.Copy(_parents, parents, _parents.Length);
            return _parents.Length;
        }
    }

    /// <summary>Scheduler thread, workers idle, under _lock: the watched car's genome and what its brain computed in the last tick.</summary>
    private void ReadBrain(RacingTraining training, int agent)
    {
        var shape = training.Shape;
        int generation = training.Runner.Generation, w = shape.WeightCount;
        if (_brainAgent != agent || _brainGeneration != generation || _brainShape != shape || !ReferenceEquals(_brainTraining, training))
        {
            _brainAgent = agent;
            _brainGeneration = generation;
            _brainShape = shape;
            _brainTraining = training;
            _brainVersion++;
        }
        if (_brainWeights.Length != w) _brainWeights = new float[w];
        int n = BrainProbe.ValueCount(shape);
        if (_brainValues.Length != n) _brainValues = new float[n];
        training.Runner.Brain.Weights.AsSpan(agent * w, w).CopyTo(_brainWeights);
        BrainProbe.Fill(shape, _brainWeights, training.Mode.Inputs.AsSpan(agent * training.Mode.InputCount, training.Mode.InputCount), _brainValues);
    }

    /// <summary>
    /// Copies the graph of the active lineage (PLAN §6.2): best and average score per generation downsampled into
    /// <c>best.Length</c> buckets (<see cref="ScoreGraph.Sample"/>), the segment dividers, and how many generations the
    /// series covers. Returns the buckets used. Does not allocate.
    /// </summary>
    public int CopyGraph(Span<float> best, Span<float> average, Span<int> dividers, out int dividerCount, out int generations,
        out float lowest, out float highest)
    {
        lock (_lock)
        {
            dividerCount = _graph.CopyDividers(dividers);
            generations = _graph.Count;
            return _graph.Sample(best, average, out lowest, out highest);
        }
    }

    /// <summary>Copies the current state. Does not allocate.</summary>
    public void ReadStats(out TrainingStats stats)
    {
        lock (_lock)
        {
            stats = _stats;
            stats.TargetSpeed = _speed.TargetSpeed;
            stats.SpeedIndex = _speed.Index;
            stats.ActualSpeed = _meter.Actual;
            stats.Paused = _speed.Paused;
            stats.Alpha = _speed.Alpha;
            stats.Interpolates = _speed.Interpolates;
            stats.TrainingSeconds = _trainingSeconds;
            stats.StepMode = _stepMode;
            stats.WaitingForNext = _waitingForNext;
            if (_saver is { } saver)
            {
                stats.SavedAtUtc = saver.LastSavedUtc != default ? saver.LastSavedUtc : _session?.LoadedSavedAtUtc ?? default;
                stats.Saving = saver.Busy;
                stats.SaveError = saver.LastError;
            }
        }
    }

    // ---------------------------------------------------------------- scheduler thread

    private void Loop()
    {
        try
        {
            Start();
            while (true)
            {
                long id;
                bool headless, idle = false;
                Action[]? commands = null;
                lock (_lock)
                {
                    // Idle (paused, waiting for Next, replay): wake about once a second so the autosave still runs.
                    while (_served == _requested && !_stop && !_headless && _commands.Count == 0)
                        if (!Monitor.Wait(_lock, IdleAutosaveCheck)) { idle = true; break; }
                    if (_stop) return;
                    if (idle && (_served != _requested || _headless || _commands.Count > 0)) idle = false; // work arrived meanwhile
                    id = _requested;
                    headless = _headless;
                    if (_commands.Count > 0)
                    {
                        commands = _commands.ToArray();
                        _commands.Clear();
                    }
                }
                if (idle)
                {
                    Autosave(force: false);
                    continue;
                }
                if (commands is not null)
                    foreach (var command in commands)
                        RunCommand(command);

                int ran = 0;
                double seconds = 0;
                if (headless) RunHeadlessPiece(out ran, out seconds);
                else if (_served < id) RunFrame(out ran, out seconds);
                Autosave(force: false);
                UpdateStats(ran, seconds);
                lock (_lock)
                {
                    if (!headless) _served = id;
                    Monitor.PulseAll(_lock);
                }
            }
        }
        catch (Exception e)
        {
            if (e is CommittedFailure { InnerException: { } inner }) e = inner;
            lock (_lock)
            {
                _stats.Error = $"{e.GetType().Name}: {e.Message}";
                _headless = false;
                _stats.Headless = false;
                Monitor.PulseAll(_lock);
            }
        }
    }

    /// <summary>Prepares the training (a new session, or the loaded one) and begins its current generation.</summary>
    private void Start()
    {
        RacingTraining training;
        if (_session is null)
        {
            training = new RacingTraining(_track!, _options.Settings, _options.Seed, _options.Evolution, _options.PopulationSize, _options.Threads,
                _options.Hidden1, _options.Hidden2);
            var session = Session.Create(training.Config, _track!, _options.Seed, training.Runner.Settings, training.Runner.Population,
                training.Runner.PopulationParams, _options.HistoryMemoryLimit);
            Volatile.Write(ref _session, session);
        }
        else
        {
            _session.SetMemoryLimit(_options.HistoryMemoryLimit);
            training = RacingTraining.ResumeFrom(_session, _options.Threads);
        }
        if (_options.Folder is { } folder) _saver = new SessionSaver(folder, _session!);
        ReplaceTraining(training);
        training.Runner.BeginGeneration();
        if (_saver is not null && _track is not null)
        {
            // A new session is on disk (and in the Sessions list) before the first generation runs.
            Autosave(force: true);
            _saver.WaitIdle(Timeout.InfiniteTimeSpan);
        }
        _lastSave = Stopwatch.GetTimestamp();
        UpdateStats(0, 0, ready: true);
    }

    /// <summary>A failure after a request already changed the session: the training stops (the session itself stays consistent).</summary>
    private sealed class CommittedFailure(Exception inner) : Exception(inner.Message, inner);

    /// <summary>Runs the part of a request that follows a change to the session; a failure there is not a mere notice.</summary>
    private static void Committed(Action action)
    {
        try
        {
            action();
        }
        catch (Exception e) when (e is not CommittedFailure)
        {
            throw new CommittedFailure(e);
        }
    }

    /// <summary>
    /// Runs a queued request; a request refused before it changed anything (a generation not evaluated, a missing
    /// record, a cancelled rebuild) leaves a notice instead of stopping the training.
    /// </summary>
    private void RunCommand(Action command)
    {
        try
        {
            command();
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or OperationCanceledException)
        {
            SetNotice(e is OperationCanceledException ? "Cancelled." : e.Message);
        }
    }

    /// <summary>Runs the due whole ticks within the frame budget; generations roll over on their own.</summary>
    private void RunFrame(out int ran, out double seconds)
    {
        int want;
        bool oneByOne;
        lock (_lock)
        {
            want = _speed.WholeTicks;
            oneByOne = _speed.Interpolates || want == 1;
        }
        long start = Stopwatch.GetTimestamp();
        ran = 0;
        while (ran < want && !_stop)
        {
            var training = _training!;
            var runner = training.Runner;
            double elapsed = Seconds(start);
            if (ran > 0 && elapsed >= SpeedController.FrameBudgetSeconds) break;
            // A tick costs about the same per alive car, and the alive count falls a lot during a generation, so
            // the piece that fits the rest of the budget is sized from the cost per agent-tick.
            int alive = CountAlive(training.Mode);
            int piece = oneByOne
                ? 1
                : (int)Math.Clamp((SpeedController.FrameBudgetSeconds - elapsed) / (_agentTickSeconds * Math.Max(alive, 1)), 1, want - ran);
            long before = runner.ElapsedTicks, pieceStart = Stopwatch.GetTimestamp();
            bool done = runner.RunTicks(piece);
            int r = (int)(runner.ElapsedTicks - before);
            if (r >= 4 && !done && alive > 0)
            {
                double perAgentTick = Seconds(pieceStart) / ((double)r * alive);
                _agentTickSeconds = 0.7 * _agentTickSeconds + 0.3 * perAgentTick;
            }
            ran += r;
            if (done && !NextGeneration()) break; // Step mode paused at the generation end
        }
        seconds = Seconds(start);
        lock (_lock)
        {
            _speed.Consume(ran);
            _meter.AddTicks(ran);
            _stats.TotalTicks += ran;
        }
    }

    /// <summary>One piece of training without visuals; stops it when the condition is met or it was cancelled.</summary>
    private void RunHeadlessPiece(out int ran, out double seconds)
    {
        long start = Stopwatch.GetTimestamp();
        var runner = _training!.Runner;
        long before = runner.ElapsedTicks;
        bool done = runner.RunTicks(HeadlessPieceTicks);
        ran = (int)(runner.ElapsedTicks - before);
        lock (_lock) _stats.TotalTicks += ran;
        if (done) NextGeneration();
        bool cancel;
        lock (_lock) cancel = _cancelHeadless;
        if (cancel) StopHeadless(null);
        seconds = Seconds(start);
    }

    private void StopHeadless(string? notice)
    {
        _training!.Runner.SetThreads(_options.Threads);
        lock (_lock)
        {
            _headless = false;
            _cancelHeadless = false;
            _stats.Headless = false;
            _stats.TrainToRuns++;
            if (notice is not null)
            {
                _stats.Notice = notice;
                _stats.NoticeCount++;
            }
            // Back to the view at the chosen speed, without game time owed from before.
            int owed = _speed.WholeTicks;
            if (owed != int.MaxValue) _speed.Consume(owed);
        }
    }

    private static int CountAlive(RacingMode mode)
    {
        int alive = 0;
        for (int i = 0; i < mode.AgentCount; i++)
            if (!mode.IsDone(i)) alive++;
        return alive;
    }

    /// <summary>
    /// Ends the running generation: stores it in the session, applies a pending settings change (new segment),
    /// checks the train-to condition and Step mode, and begins the next generation. Returns false when Step mode
    /// paused the training.
    /// </summary>
    private bool NextGeneration()
    {
        var session = _session!;
        var runner = _training!.Runner;
        GenerationRecord record = runner.EndGeneration();
        lock (_lock)
        {
            ReadOnlySpan<int> parents = runner.LastParents;
            if (_parents.Length != parents.Length) _parents = new int[parents.Length];
            parents.CopyTo(_parents);
            _parentsGeneration = runner.Generation;
            _parentsElites = runner.LastEliteCount;
        }
        session.AddGeneration(record, runner.Population, runner.PopulationParams);
        if (record.Stats.BestFinishTicks < _bestTimeTicks) _bestTimeTicks = record.Stats.BestFinishTicks;
        bool boostStarted = runner.Stagnation.JustTriggered;
        GenerationEnded?.Invoke(record);
        ApplyPendingSettings();
        _training!.Runner.BeginGeneration();

        bool headless, keepGoing = true;
        lock (_lock)
        {
            _stats.LastGeneration = record.Generation;
            _stats.LastStats = record.Stats;
            _graph.Set(record.Generation, record.Stats.BestScore, record.Stats.AverageScore); // a re-evaluated resume point sets the same values
            _stats.GraphVersion = _graph.Version;
            if (boostStarted) _stats.BoostStartedAt = record.Generation;
            headless = _headless;
            if (!headless && _stepMode)
            {
                _speed.Paused = true;
                _waitingForNext = true;
                keepGoing = false;
            }
        }
        if (headless)
        {
            _rateGenerations++;
            double elapsed = Seconds(_rateStart);
            if (elapsed >= 1)
            {
                lock (_lock) _stats.GenerationsPerSecond = _rateGenerations / elapsed;
                _rateStart = Stopwatch.GetTimestamp();
                _rateGenerations = 0;
            }
            TrainToCondition condition;
            lock (_lock) condition = _stats.TrainTo;
            if (condition.IsMet(record.Stats, _training!.Runner.Generation, RacingSettings.TicksPerSecond)) StopHeadless(null);
        }
        return keepGoing;
    }

    /// <summary>A settings change waiting for the boundary: the population continues as a new segment (if the config changed).</summary>
    private void ApplyPendingSettings()
    {
        RacingSettings? settings;
        int? size;
        lock (_lock)
        {
            settings = _pendingSettings;
            size = _pendingPopulation;
            _pendingSettings = null;
            _pendingPopulation = null;
        }
        if (settings is null) return;
        var session = _session!;
        PopulationEntry current;
        SegmentEntry segment;
        ulong seed;
        lock (session.SyncRoot)
        {
            current = session.Current;
            segment = session.SegmentAt(current.Branch, current.Generation);
            seed = session.Branch(current.Branch).Seed;
        }
        var brain = segment.Config.Brain;
        // Another number of cars: the bred population is resized (trim the tail / add mutated copies), the same way a rebuild does it.
        var population = current.Population;
        int newSize = size ?? population.Size;
        if (newSize != population.Size)
            population = PopulationResize.To(population, newSize, current.Params, PopulationResize.SeedFor(SeedHash.Derive(seed, (ulong)current.Generation)));
        var training = new RacingTraining(segment.Track, settings, seed, session.Evolution, newSize, CurrentThreads(),
            brain.Hidden1, brain.Hidden2, population, current.Generation, current.Params);
        if (training.Config.Hash == segment.Config.Hash && newSize == current.Population.Size || training.Shape != brain)
        {
            training.Dispose();
            if (training.Shape != brain) SetNotice("Those settings change the brain's inputs; start a new training to use them.");
            return;
        }
        session.StartSegment(training.Config, segment.Track, newSize, newSize != current.Population.Size ? population : null);
        ReplaceTraining(training);
    }

    /// <summary>Rebuilds a generation's population for a fork or switch, showing progress; null when cancelled.</summary>
    private Population? RebuildForUse(string branch, int generation)
    {
        var session = _session!;
        int steps = Rebuilder.StepsNeeded(session, branch, generation);
        CancellationTokenSource cancel;
        lock (_lock)
        {
            cancel = _busyCancel = new CancellationTokenSource();
            _stats.Busy = steps > 0 ? $"Rebuilding generation {generation} ({steps} generations to re-run)…" : null;
            _stats.BusyProgress = 0;
            _stats.Notice = null;
        }
        try
        {
            return Rebuilder.Rebuild(session, branch, generation, HeadlessThreads,
                (done, total) =>
                {
                    lock (_lock) _stats.BusyProgress = (float)done / total;
                },
                cancel.Token);
        }
        catch (OperationCanceledException)
        {
            SetNotice("Cancelled.");
            return null;
        }
        finally
        {
            lock (_lock)
            {
                _stats.Busy = null;
                _busyCancel = null;
            }
            cancel.Dispose();
        }
    }

    /// <summary>Switches to another training (new segment, fork, branch switch): trails, leader and best time follow.</summary>
    private void ReplaceTraining(RacingTraining training)
    {
        var old = _training;
        var trails = new RouteTrails(training.Mode.AgentCount, training.TimeLimitTicks);
        training.Mode.Trails = trails;
        if (_headless) training.Runner.SetThreads(TrainToThreads);
        var session = _session!;
        float best = float.PositiveInfinity;
        foreach (var stats in session.SegmentRunStats(session.ActiveBranch, session.Current.Generation))
            best = MathF.Min(best, stats.BestFinishTicks);
        _bestTimeTicks = best;
        _leader = -1;
        var entries = session.LineageEntries(session.ActiveBranch);
        var dividers = session.SegmentRunStarts(session.ActiveBranch);
        lock (_lock)
        {
            _graph.Clear();
            foreach (var e in entries) _graph.Set(e.Generation, e.Stats.BestScore, e.Stats.AverageScore);
            _graph.SetDividers(dividers);
            _stats.GraphVersion = _graph.Version;
            if (_trailX.Length != trails.SamplesPerCar)
            {
                _trailX = new float[trails.SamplesPerCar];
                _trailY = new float[trails.SamplesPerCar];
            }
            _trailCount = 0;
            _trailAgent = -1;
            _watchChanged = true;
            _parentsGeneration = -1; // a new training: nobody's parents known
        }
        Volatile.Write(ref _training, training);
        old?.Dispose();
    }

    private int CurrentThreads()
    {
        lock (_lock) return _headless ? TrainToThreads : _options.Threads;
    }

    /// <summary>Plans a save on this thread and hands it to the saver (every autosave interval, or now when forced).</summary>
    private void Autosave(bool force)
    {
        if (_saver is null) return;
        if (!force && Seconds(_lastSave) < _options.AutosaveInterval.TotalSeconds) return;
        if (_saver.Busy) return; // the next check plans a save that covers this one's changes too
        var session = _session!;
        CopyCountersTo(session);
        _lastSave = Stopwatch.GetTimestamp();
        if (!force && !session.HasUnsavedChanges) return;
        _saver.Start(session.PrepareSave(DateTime.UtcNow));
    }

    private void Enqueue(Action command)
    {
        lock (_lock)
        {
            _commands.Enqueue(command);
            Monitor.PulseAll(_lock);
        }
    }

    private void SetNotice(string notice)
    {
        lock (_lock)
        {
            _stats.Notice = notice;
            _stats.NoticeCount++;
        }
    }

    /// <summary>Live numbers of the running generation (scheduler thread, workers idle).</summary>
    private void UpdateStats(int ran, double seconds, bool ready = true)
    {
        var training = _training!;
        var session = _session!;
        var mode = training.Mode;
        int alive = 0, finished = 0, leader = -1;
        float bestProgress = 0f, bestScore = float.NegativeInfinity, bestTime = _bestTimeTicks;
        for (int i = 0; i < mode.AgentCount; i++)
        {
            AgentStatus status = mode.Status(i);
            float progress = mode.Progress(i);
            if (progress > bestProgress) bestProgress = progress;
            if (status == AgentStatus.Running) alive++;
            else if (status == AgentStatus.Finished)
            {
                finished++;
                if (mode.FinishTicks(i) < bestTime) bestTime = mode.FinishTicks(i);
            }
            else continue;
            float score = mode.Score(i);
            if (score > bestScore)
            {
                bestScore = score;
                leader = i;
            }
        }
        leader = KeepLeader(mode, training.Runner.Generation, leader, bestScore);
        RouteTrails? trails = mode.Trails;
        var current = session.Current;
        lock (_lock)
        {
            int watched = _watched;
            if (trails is not null && watched >= 0 && watched < mode.AgentCount)
            {
                _trailCount = trails.CopyTo(watched, trails.Count(mode.Tick(watched)), _trailX, _trailY);
                _trailAgent = watched;
                _trailGeneration = training.Runner.Generation;
            }
            else
            {
                _trailCount = 0;
                _trailAgent = -1;
            }
            if (watched >= 0 && watched < mode.AgentCount)
            {
                ReadBrain(training, watched);
                _stats.WatchedSpeed = mode.Car(watched).ForwardSpeed;
            }
            else
            {
                _brainAgent = -1;
            }
            _stats.MaxSpeed = training.Track.Settings.MaxSpeed;
            _stats.Ready = ready;
            _stats.Generation = training.Runner.Generation;
            _stats.PopulationSize = mode.AgentCount;
            _stats.Threads = training.Runner.Threads;
            _stats.Alive = alive;
            _stats.Finished = finished;
            _stats.ElapsedTicks = training.Runner.ElapsedTicks;
            _stats.TimeLimitTicks = training.TimeLimitTicks;
            _stats.BestProgress = bestProgress;
            _stats.BestTimeTicks = bestTime;
            _stats.Leader = leader;
            _stats.TicksLastFrame = ran;
            _stats.SimSecondsLastFrame = seconds;
            _stats.Branch = current.Branch;
            _stats.Segment = current.Segment;
            _stats.BoostActive = training.Runner.PopulationParams.Boost;
            _stats.HistoryBytes = session.History.Bytes;
        }
    }

    /// <summary>The previous leader while it is still on the track and within <see cref="LeaderMargin"/> of the best score.</summary>
    private int KeepLeader(RacingMode mode, int generation, int best, float bestScore)
    {
        int previous = generation == _leaderGeneration && _leader < mode.AgentCount ? _leader : -1;
        bool onTrack = previous >= 0 && mode.Status(previous) is AgentStatus.Running or AgentStatus.Finished;
        _leader = ChooseLeader(previous, onTrack, onTrack ? mode.Score(previous) : float.NegativeInfinity, best, bestScore);
        _leaderGeneration = generation;
        return _leader;
    }

    /// <summary>The leader with hysteresis: <paramref name="best"/> takes over only when it leads <paramref name="previous"/> by more than <see cref="LeaderMargin"/>.</summary>
    public static int ChooseLeader(int previous, bool previousOnTrack, float previousScore, int best, float bestScore) =>
        previous >= 0 && previousOnTrack && previousScore >= bestScore - LeaderMargin ? previous : best;

    private static double Seconds(long since) => (double)(Stopwatch.GetTimestamp() - since) / Stopwatch.Frequency;

    /// <summary>Hands the host's counters, mode and speed to the session so the next save holds them.</summary>
    private void CopyCountersTo(Session session)
    {
        lock (_lock)
        {
            session.TrainingSeconds = _trainingSeconds;
            session.TotalTicks = _stats.TotalTicks;
            session.SetViewState(_stepMode, _speed.TargetSpeed);
        }
    }

    private bool _stopped;

    /// <summary>
    /// Why the save made when the training stopped (<see cref="Stop"/>, <see cref="RetryExitSave"/>) failed, or null when
    /// it worked (or there was nothing to save). The previous save on disk stays loadable either way.
    /// </summary>
    public string? ExitSaveError { get; private set; }

    /// <summary>
    /// Stops the simulation, then saves the session one last time (PLAN §5.1 "on exit") and waits for the save to
    /// finish, so nothing trained is lost when the view closes. Idempotent. A failed save is not thrown: it is left
    /// in <see cref="ExitSaveError"/> so the caller can offer <see cref="RetryExitSave"/> before <see cref="Dispose"/>.
    /// </summary>
    public void Stop()
    {
        lock (_lock)
        {
            if (_stopped) return;
            _stopped = true;
            _stop = true;
            _busyCancel?.Cancel();
            Monitor.PulseAll(_lock);
        }
        _thread.Join();
        if (_saver is not null)
        {
            _saver.WaitIdle(Timeout.InfiniteTimeSpan);
            RetryExitSave();
        }
    }

    /// <summary>Saves the stopped session again (after <see cref="Stop"/> reported <see cref="ExitSaveError"/>); true when it worked.</summary>
    public bool RetryExitSave()
    {
        var session = _session;
        if (_saver is null || session is null || _training is null) return true;
        _saver.WaitIdle(Timeout.InfiniteTimeSpan);
        CopyCountersTo(session);
        try
        {
            _saver.Start(session.PrepareSave(DateTime.UtcNow));
            _saver.WaitIdle(Timeout.InfiniteTimeSpan);
            ExitSaveError = _saver.LastError;
        }
        catch (Exception e)
        {
            ExitSaveError = e.Message;
        }
        return ExitSaveError is null;
    }

    /// <summary>Stops (see <see cref="Stop"/>) and releases everything. A final save that failed is only reported in <see cref="ExitSaveError"/>.</summary>
    public void Dispose()
    {
        Stop();
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _saver?.Dispose();
        _training?.Dispose();
        _folderLock?.Dispose();
    }

    private bool _disposed;
}
