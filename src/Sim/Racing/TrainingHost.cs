using System.Diagnostics;
using Nitrogenesis.Sim.Core;
using Nitrogenesis.Sim.Evolution;
using Nitrogenesis.Sim.History;
using Nitrogenesis.Sim.Map;

namespace Nitrogenesis.Sim.Racing;

/// <summary>What a <see cref="TrainingHost"/> trains with.</summary>
public sealed record TrainingHostOptions
{
    /// <summary>Simulation threads W (the host's own scheduler thread is one of them).</summary>
    public int Threads { get; init; } = 1;
    public int PopulationSize { get; init; } = EvolutionSettings.DefaultPopulationSize;
    public ulong Seed { get; init; } = 1;
    public RacingSettings Settings { get; init; } = new();
    public EvolutionSettings? Evolution { get; init; }
    /// <summary>Starting speed (snapped to the nearest step; +∞ = MAX).</summary>
    public double Speed { get; init; } = 1;
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
    /// <summary>Fastest finish ever in this training (this segment), in ticks; +∞ when no car has finished yet.</summary>
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
    /// <summary>Real time spent training (not paused), seconds.</summary>
    public double TrainingSeconds;
    /// <summary>Ticks run since the start (all generations).</summary>
    public long TotalTicks;
    /// <summary>Ticks and seconds of simulation work in the last frame.</summary>
    public int TicksLastFrame;
    public double SimSecondsLastFrame;
}

/// <summary>
/// Runs racing training for the view (PLAN §2.1, §4): a <see cref="GenerationRunner"/> on its own scheduler thread
/// with W workers, driven by the <see cref="SpeedController"/>. Generations run back to back.
/// </summary>
/// <remarks>
/// <para>The render thread calls <see cref="Frame"/> once per frame with the real frame time. That adds game time
/// to the speed controller and wakes the scheduler thread, which runs the due whole ticks within the 14 ms frame
/// budget and publishes snapshots (<see cref="Snapshots"/>). At 1× and slower it runs one tick per
/// <see cref="GenerationRunner.RunTicks"/>, so prev/curr are always one tick apart; faster, it sizes its pieces
/// from the measured cost per agent-tick. Ticks left over when the budget runs out stay due (up to the controller's
/// backlog cap), and the measured speed drops instead: "100× (actual 64×)".</para>
/// <para>The render thread never waits for the simulation, except at 1× and slower, where
/// <see cref="WaitForFrame"/> gives the (tiny) work of the frame a few milliseconds so the alpha matches the
/// snapshot pair.</para>
/// <para>Results are bit-identical to <see cref="GenerationRunner.RunGeneration"/>: ticks are only split
/// differently.</para>
/// </remarks>
public sealed class TrainingHost : IDisposable
{
    private readonly object _lock = new();
    private readonly SpeedController _speed = new();
    private readonly SpeedMeter _meter = new();
    private readonly Track _track;
    private readonly TrainingHostOptions _options;
    private readonly Thread _thread;
    private volatile bool _stop;
    private RacingTraining? _training;

    // Under _lock.
    private long _requested, _served;
    private TrainingStats _stats;
    private double _trainingSeconds;

    // Route of the watched car (under _lock): written by the scheduler thread after each frame, copied by the view.
    private int _watched = -1;
    private bool _watchChanged;
    private float[] _trailX = Array.Empty<float>(), _trailY = Array.Empty<float>();
    private int _trailCount, _trailAgent = -1, _trailGeneration = -1;

    // Scheduler thread only.
    private double _agentTickSeconds = 2e-6; // running estimate of one agent-tick's cost (wall time), seconds
    private float _bestTimeTicks = float.PositiveInfinity;
    private int _leader = -1, _leaderGeneration = -1;

    /// <summary>Score lead (≈ progress fraction, 0.01 = 1 % of the track) another car needs to take over as the leader.</summary>
    public const float LeaderMargin = 0.01f;

    public TrainingHost(Track track, TrainingHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(options);
        _track = track;
        _options = options;
        _speed.Index = SpeedController.IndexFor(options.Speed);
        _stats.Leader = -1;
        _stats.BestTimeTicks = float.PositiveInfinity;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Sim scheduler" };
        _thread.Start();
    }

    /// <summary>The training, once prepared (null before; the reference driver runs first on the scheduler thread).</summary>
    public RacingTraining? Training => Volatile.Read(ref _training);

    /// <summary>The snapshots the view reads (null until prepared).</summary>
    public SnapshotBuffer? Snapshots => Training?.Runner.Snapshots;

    public int PopulationSize => _options.PopulationSize;

    /// <summary>Raised on the scheduler thread after each generation ends, before the next one begins.</summary>
    public event Action<GenerationRecord>? GenerationEnded;

    /// <summary>
    /// Called by the render thread once per frame with the real time since the last frame: adds game time and
    /// wakes the simulation if whole ticks are due. Never blocks. Returns true when it asked for ticks at an
    /// interpolating speed (1× or slower), where the view should <see cref="WaitForFrame"/> before it draws.
    /// </summary>
    public bool Frame(double realSeconds)
    {
        lock (_lock)
        {
            if (!_stats.Ready) return false; // no game time piles up while the track is prepared
            _speed.Advance(realSeconds);
            bool running = _stats.Error is null && !_speed.Paused;
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
        lock (_lock) _speed.Paused = !_speed.Paused;
    }

    /// <summary>One tick while paused (it runs at the next <see cref="Frame"/>).</summary>
    public void StepTick()
    {
        lock (_lock) _speed.StepOnce();
    }

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
        }
    }

    private void Loop()
    {
        try
        {
            var training = new RacingTraining(_track, _options.Settings, _options.Seed, _options.Evolution,
                _options.PopulationSize, _options.Threads);
            training.Runner.KeepPopulationSnapshots = false; // no history store in the view yet (M3)
            var trails = new RouteTrails(training.Mode.AgentCount, training.TimeLimitTicks);
            training.Mode.Trails = trails;
            lock (_lock)
            {
                _trailX = new float[trails.SamplesPerCar];
                _trailY = new float[trails.SamplesPerCar];
            }
            training.Runner.BeginGeneration();
            Volatile.Write(ref _training, training);
            UpdateStats(training, 0, 0, ready: true);

            while (true)
            {
                long id;
                lock (_lock)
                {
                    while (_served == _requested && !_stop) Monitor.Wait(_lock);
                    if (_stop) return;
                    id = _requested;
                }
                RunFrame(training, out int ran, out double seconds);
                UpdateStats(training, ran, seconds, ready: true);
                lock (_lock)
                {
                    _served = id;
                    Monitor.PulseAll(_lock);
                }
            }
        }
        catch (Exception e)
        {
            lock (_lock)
            {
                _stats.Error = $"{e.GetType().Name}: {e.Message}";
                Monitor.PulseAll(_lock);
            }
        }
    }

    /// <summary>Runs the due whole ticks within the frame budget; generations roll over on their own.</summary>
    private void RunFrame(RacingTraining training, out int ran, out double seconds)
    {
        int want;
        bool oneByOne;
        lock (_lock)
        {
            want = _speed.WholeTicks;
            oneByOne = _speed.Interpolates || want == 1;
        }
        var runner = training.Runner;
        long start = Stopwatch.GetTimestamp();
        ran = 0;
        while (ran < want && !_stop)
        {
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
            if (done) NextGeneration(training);
        }
        seconds = Seconds(start);
        lock (_lock)
        {
            _speed.Consume(ran);
            _meter.AddTicks(ran);
            _stats.TotalTicks += ran;
        }
    }

    private static int CountAlive(RacingMode mode)
    {
        int alive = 0;
        for (int i = 0; i < mode.AgentCount; i++)
            if (!mode.IsDone(i)) alive++;
        return alive;
    }

    private void NextGeneration(RacingTraining training)
    {
        var runner = training.Runner;
        GenerationRecord record = runner.EndGeneration();
        if (record.Stats.BestFinishTicks < _bestTimeTicks) _bestTimeTicks = record.Stats.BestFinishTicks;
        GenerationEnded?.Invoke(record);
        runner.BeginGeneration();
    }

    /// <summary>Live numbers of the running generation (scheduler thread, workers idle).</summary>
    private void UpdateStats(RacingTraining training, int ran, double seconds, bool ready)
    {
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

    public void Dispose()
    {
        lock (_lock)
        {
            _stop = true;
            Monitor.PulseAll(_lock);
        }
        _thread.Join();
        _training?.Dispose();
    }
}
