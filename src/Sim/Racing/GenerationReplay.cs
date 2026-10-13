using System.Diagnostics;
using Nitrogenesis.Sim.Core;
using Nitrogenesis.Sim.Evolution;
using Nitrogenesis.Sim.History;
using Nitrogenesis.Sim.Io;

namespace Nitrogenesis.Sim.Racing;

/// <summary>A copy of a <see cref="GenerationReplay"/>'s state for its view (read with <see cref="GenerationReplay.ReadStats"/>).</summary>
public struct ReplayStats
{
    /// <summary>The generation is set up and can be watched (after the rebuild).</summary>
    public bool Ready;
    /// <summary>Why the replay stopped, or null.</summary>
    public string? Error;
    /// <summary>The rebuild in progress ("Rebuilding generation 120 …") and its progress 0…1, or null.</summary>
    public string? Busy;
    public float BusyProgress;
    public int Generation, PopulationSize, Alive, Finished;
    public long ElapsedTicks;
    /// <summary>The tick the generation was scored at (it may have been ended early); the replay stops there.</summary>
    public int EndTicks;
    public int TimeLimitTicks;
    /// <summary>Best progress 0…1 so far and the fastest finish so far (ticks, +∞ when none).</summary>
    public float BestProgress, BestTimeTicks;
    public int Leader;
    public double TargetSpeed, ActualSpeed;
    public int SpeedIndex;
    public bool Paused;
    public float Alpha;
    public bool Interpolates;
    /// <summary>The replay reached the end of the generation.</summary>
    public bool Ended;
    /// <summary>At the end: the best score and car equal the stored record bit for bit (checked every time).</summary>
    public bool MatchesRecord;
    /// <summary>The stored stats of the generation.</summary>
    public GenerationStats Record;
    public int TicksLastFrame;
    public double SimSecondsLastFrame;
}

/// <summary>
/// "Watch whole generation" (PLAN §5 "Replay"): re-simulates every car of a stored generation (same SimVersion only),
/// with its own speed control, on its own thread, while the training goes on or stays paused. A generation whose
/// snapshot was thinned is rebuilt first (<see cref="Rebuilder"/>, progress in <see cref="ReplayStats.Busy"/>).
/// </summary>
/// <remarks>
/// Frames work as in <see cref="TrainingHost"/>: the render thread calls <see cref="Frame"/> (adds game time, wakes
/// the replay thread), which runs the due whole ticks within the 14 ms budget and publishes snapshots
/// (<see cref="Snapshots"/>). The replay stops at the tick the generation was scored at, and then checks the scores
/// against the record (a difference is an error: the physics is not deterministic).
/// </remarks>
public sealed class GenerationReplay : IDisposable
{
    private readonly object _lock = new();
    private readonly SpeedController _speed = new();
    private readonly SpeedMeter _meter = new();
    private readonly Session _session;
    private readonly string _branch;
    private readonly int _generation, _threads, _rebuildThreads;
    private readonly Thread _thread;
    private readonly CancellationTokenSource _cancel = new();
    private volatile bool _stop;
    private RacingTraining? _training;
    private Population? _population;
    private HistoryEntry? _entry;
    private SegmentEntry? _segment;
    private ulong _seed;

    // Under _lock.
    private long _requested, _served;
    private bool _restart;
    private ReplayStats _stats;

    // Replay thread only.
    private double _agentTickSeconds = 2e-6;
    private int _leader = -1;
    private float _bestTime = float.PositiveInfinity;

    /// <param name="session">The session (the same SimVersion as this app).</param>
    /// <param name="branch">A branch whose lineage holds the generation.</param>
    /// <param name="generation">A stored (evaluated) generation.</param>
    /// <param name="threads">Simulation threads while watching.</param>
    /// <param name="rebuildThreads">Threads for the rebuild (0 = <paramref name="threads"/>).</param>
    /// <param name="speed">Starting speed.</param>
    /// <exception cref="InvalidOperationException">The session is from another SimVersion (PLAN §5.1).</exception>
    public GenerationReplay(Session session, string branch, int generation, int threads, int rebuildThreads = 0, double speed = 1)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(branch);
        if (!session.Compatibility.CanResimulate) throw new InvalidOperationException(session.Compatibility.Message);
        _session = session;
        _branch = branch;
        _generation = generation;
        _threads = Math.Max(1, threads);
        _rebuildThreads = rebuildThreads > 0 ? rebuildThreads : _threads;
        _speed.Index = SpeedController.IndexFor(speed);
        _stats.Generation = generation;
        _stats.Leader = -1;
        _stats.BestTimeTicks = float.PositiveInfinity;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Replay" };
        _thread.Start();
    }

    public int Generation => _generation;
    public string Branch => _branch;

    /// <summary>The training that re-simulates the generation (null until ready; replaced by <see cref="Restart"/>).</summary>
    public RacingTraining? Training => Volatile.Read(ref _training);

    public SnapshotBuffer? Snapshots => Training?.Runner.Snapshots;

    // ---------------------------------------------------------------- render thread

    /// <summary>Adds game time and wakes the replay thread when ticks are due. True when the view should <see cref="WaitForFrame"/> (1× and slower).</summary>
    public bool Frame(double realSeconds)
    {
        lock (_lock)
        {
            if (!_stats.Ready || _stats.Error is not null) return false;
            if (_stats.Ended)
            {
                _meter.AddTime(realSeconds, true, _speed.TargetSpeed);
                return false;
            }
            _speed.Advance(realSeconds);
            _meter.AddTime(realSeconds, _speed.Paused, _speed.TargetSpeed);
            if (_speed.WholeTicks <= 0) return false;
            _requested++;
            Monitor.PulseAll(_lock);
            return _speed.Interpolates;
        }
    }

    /// <summary>Waits at most <paramref name="timeout"/> for the work of the last <see cref="Frame"/>.</summary>
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
        lock (_lock) _speed.Paused = !_speed.Paused;
    }

    public void StepTick()
    {
        lock (_lock) _speed.StepOnce();
    }

    /// <summary>Watches the generation again from its start.</summary>
    public void Restart()
    {
        lock (_lock)
        {
            if (!_stats.Ready) return;
            _restart = true;
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>Stops the rebuild (the view goes back).</summary>
    public void Cancel() => _cancel.Cancel();

    public void ReadStats(out ReplayStats stats)
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
        }
    }

    // ---------------------------------------------------------------- replay thread

    private void Loop()
    {
        try
        {
            Prepare();
            while (true)
            {
                long id;
                bool restart;
                lock (_lock)
                {
                    while (_served == _requested && !_stop && !_restart) Monitor.Wait(_lock);
                    if (_stop) return;
                    id = _requested;
                    restart = _restart;
                    _restart = false;
                }
                int ran = 0;
                double seconds = 0;
                if (restart) StartRun();
                else RunFrame(out ran, out seconds);
                UpdateStats(ran, seconds);
                lock (_lock)
                {
                    _served = id;
                    Monitor.PulseAll(_lock);
                }
            }
        }
        catch (OperationCanceledException)
        {
            lock (_lock)
            {
                _stats.Error = "Cancelled.";
                _stats.Busy = null;
                Monitor.PulseAll(_lock);
            }
        }
        catch (Exception e)
        {
            lock (_lock)
            {
                _stats.Error = e.Message;
                _stats.Busy = null;
                Monitor.PulseAll(_lock);
            }
        }
    }

    /// <summary>Finds the generation, rebuilds its population (if its snapshot was thinned) and sets up the first run.</summary>
    private void Prepare()
    {
        HistoryEntry entry;
        lock (_session.SyncRoot)
        {
            entry = _session.EntryAt(_branch, _generation)
                    ?? throw new InvalidOperationException($"Generation {_generation} has not been evaluated (or its record is missing).");
            _segment = _session.SegmentAt(_branch, _generation);
            _seed = _session.Branch(_session.OwnerOf(_branch, _generation)).Seed;
        }
        _entry = entry;
        int steps = Rebuilder.StepsNeeded(_session, _branch, _generation);
        lock (_lock)
        {
            _stats.Record = entry.Stats;
            _stats.EndTicks = entry.Ticks;
            _stats.Busy = steps > 0 ? $"Rebuilding generation {_generation} ({steps} generations to re-run)…" : null;
        }
        _population = Rebuilder.Rebuild(_session, _branch, _generation, _rebuildThreads,
            (done, total) =>
            {
                lock (_lock) _stats.BusyProgress = (float)done / total;
            },
            _cancel.Token);
        lock (_lock) _stats.Busy = null;
        StartRun();
        UpdateStats(0, 0);
        lock (_lock)
        {
            _stats.Ready = true;
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>A fresh run of the generation from tick 0 (also for <see cref="Restart"/>).</summary>
    private void StartRun()
    {
        var segment = _segment!;
        var population = new Population(_population!.Shape, (float[])_population.Weights.Clone());
        var training = new RacingTraining(segment.Track, segment.Config, _seed, null, population.Size, _threads, population, _generation, _entry!.Params);
        training.Runner.RecordBestCar = false;
        training.Runner.KeepPopulationSnapshots = false;
        training.Runner.BeginGeneration();
        var old = _training;
        Volatile.Write(ref _training, training);
        old?.Dispose();
        _leader = -1;
        _bestTime = float.PositiveInfinity;
        lock (_lock)
        {
            _stats.Ended = false;
            _stats.MatchesRecord = false;
            int owed = _speed.WholeTicks;
            if (owed != int.MaxValue) _speed.Consume(owed);
        }
    }

    private int EndTick => _entry!.Ticks >= 0 ? _entry.Ticks : int.MaxValue;

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
        var training = _training!;
        var runner = training.Runner;
        bool done = false;
        while (ran < want && !_stop && runner.ElapsedTicks < EndTick)
        {
            double elapsed = Seconds(start);
            if (ran > 0 && elapsed >= SpeedController.FrameBudgetSeconds) break;
            int alive = CountAlive(training.Mode);
            int left = (int)Math.Min(want - ran, EndTick - runner.ElapsedTicks);
            int piece = oneByOne
                ? 1
                : (int)Math.Clamp((SpeedController.FrameBudgetSeconds - elapsed) / (_agentTickSeconds * Math.Max(alive, 1)), 1, left);
            long before = runner.ElapsedTicks, pieceStart = Stopwatch.GetTimestamp();
            done = runner.RunTicks(Math.Min(piece, left));
            int r = (int)(runner.ElapsedTicks - before);
            if (r >= 4 && !done && alive > 0) _agentTickSeconds = 0.7 * _agentTickSeconds + 0.3 * Seconds(pieceStart) / ((double)r * alive);
            ran += r;
            if (done || r == 0) break;
        }
        seconds = Seconds(start);
        bool ended = done || runner.ElapsedTicks >= EndTick;
        bool matches = ended && MatchesRecord(training.Mode);
        lock (_lock)
        {
            _speed.Consume(ran);
            _meter.AddTicks(ran);
            if (ended)
            {
                int owed = _speed.WholeTicks; // nothing more to run: drop what is still owed
                if (owed != int.MaxValue) _speed.Consume(owed);
                _stats.Ended = true;
                _stats.MatchesRecord = matches;
                if (!matches) _stats.Error = $"Watching generation {_generation} again gave another result than its record: the simulation is not deterministic.";
            }
        }
    }

    /// <summary>The best score and car (the runner's rule: highest score, lowest index on a tie) equal the record's, bit for bit.</summary>
    private bool MatchesRecord(RacingMode mode)
    {
        int best = 0;
        for (int i = 1; i < mode.AgentCount; i++)
        {
            float s = mode.Score(i), b = mode.Score(best);
            if (s > b || float.IsNaN(b) && !float.IsNaN(s)) best = i;
        }
        var record = _entry!.Stats;
        return best == record.BestIndex && BitConverter.SingleToUInt32Bits(mode.Score(best)) == BitConverter.SingleToUInt32Bits(record.BestScore);
    }

    private static int CountAlive(RacingMode mode)
    {
        int alive = 0;
        for (int i = 0; i < mode.AgentCount; i++)
            if (!mode.IsDone(i)) alive++;
        return alive;
    }

    private void UpdateStats(int ran, double seconds)
    {
        var training = _training!;
        var mode = training.Mode;
        int alive = 0, finished = 0, leader = -1;
        float bestProgress = 0f, bestScore = float.NegativeInfinity;
        for (int i = 0; i < mode.AgentCount; i++)
        {
            AgentStatus status = mode.Status(i);
            float progress = mode.Progress(i);
            if (progress > bestProgress) bestProgress = progress;
            if (status == AgentStatus.Running) alive++;
            else if (status == AgentStatus.Finished)
            {
                finished++;
                if (mode.FinishTicks(i) < _bestTime) _bestTime = mode.FinishTicks(i);
            }
            else continue;
            float score = mode.Score(i);
            if (score > bestScore)
            {
                bestScore = score;
                leader = i;
            }
        }
        int previous = _leader >= 0 && _leader < mode.AgentCount ? _leader : -1;
        bool onTrack = previous >= 0 && mode.Status(previous) is AgentStatus.Running or AgentStatus.Finished;
        _leader = TrainingHost.ChooseLeader(previous, onTrack, onTrack ? mode.Score(previous) : float.NegativeInfinity, leader, bestScore);
        lock (_lock)
        {
            _stats.PopulationSize = mode.AgentCount;
            _stats.Alive = alive;
            _stats.Finished = finished;
            _stats.ElapsedTicks = training.Runner.ElapsedTicks;
            _stats.TimeLimitTicks = training.TimeLimitTicks;
            _stats.BestProgress = bestProgress;
            _stats.BestTimeTicks = _bestTime;
            _stats.Leader = _leader;
            _stats.TicksLastFrame = ran;
            _stats.SimSecondsLastFrame = seconds;
        }
    }

    private static double Seconds(long since) => (double)(Stopwatch.GetTimestamp() - since) / Stopwatch.Frequency;

    /// <summary>Stops the replay thread (cancels a rebuild) and frees the simulation.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_stop) return;
            _stop = true;
            Monitor.PulseAll(_lock);
        }
        _cancel.Cancel();
        _thread.Join();
        _training?.Dispose();
        _cancel.Dispose();
    }
}
