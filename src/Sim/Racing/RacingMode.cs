using Nitrogenesis.Sim.Brain;
using Nitrogenesis.Sim.Core;
using Nitrogenesis.Sim.History;

namespace Nitrogenesis.Sim.Racing;

/// <summary>
/// Top-down racing for N cars (PLAN §3) behind the generic <see cref="IAgentMode"/>. Car state, progress and
/// memory inputs live in per-agent arrays; physics and sensors are shared immutable objects, so disjoint agent
/// ranges can be stepped on different threads at once. Nothing allocates after construction.
/// </summary>
/// <remarks>
/// Outputs per agent: [throttle, steer], each in [−1, 1]. A tick for one car: physics with its outputs →
/// finish / crash → progress update (bilinear distance) → time limit, then stall check.
/// </remarks>
public sealed class RacingMode : IAgentMode
{
    private readonly RacingTrack _track;
    private readonly CarPhysics _physics;
    private readonly Sensors _sensors;
    private readonly int _timeLimitTicks, _stallTicks;
    private readonly float _inverseTimeLimit, _inverseTrackLength;

    // Car state (struct of arrays).
    private readonly float[] _x, _y, _vx, _vy, _yawRate;
    private readonly int[] _heading;
    // Run bookkeeping.
    private readonly int[] _tick;
    private readonly AgentStatus[] _status;
    private readonly float[] _finishTicks, _driven, _prevThrottle, _prevSteer;
    // Progress record (RacingFitness.Progress, split into arrays).
    private readonly float[] _bestDist, _stallRefDist;
    private readonly int[] _bestTick, _stallTick;

    /// <param name="track">Prepared track; its settings are the ones used.</param>
    /// <param name="agentCount">Number of cars.</param>
    /// <param name="timeLimitTicks">Generation time limit (resolved, see <see cref="RacingFitness.ResolveTimeLimitTicks"/>).</param>
    public RacingMode(RacingTrack track, int agentCount, int timeLimitTicks)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentOutOfRangeException.ThrowIfLessThan(agentCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(timeLimitTicks, 1);
        _track = track;
        _physics = new CarPhysics(track.Grid, track.Clearance, track.Settings);
        _sensors = new Sensors(track, _physics);
        _timeLimitTicks = timeLimitTicks;
        _stallTicks = track.Settings.StallTicks;
        _inverseTimeLimit = 1f / timeLimitTicks;
        _inverseTrackLength = track.IsFinishable ? 1f / track.StartDistance : 0f;

        AgentCount = agentCount;
        InputCount = _sensors.InputCount;
        Inputs = new float[agentCount * InputCount];
        Outputs = new float[agentCount * OutputCount];
        _x = new float[agentCount];
        _y = new float[agentCount];
        _vx = new float[agentCount];
        _vy = new float[agentCount];
        _yawRate = new float[agentCount];
        _heading = new int[agentCount];
        _tick = new int[agentCount];
        _status = new AgentStatus[agentCount];
        _finishTicks = new float[agentCount];
        _driven = new float[agentCount];
        _prevThrottle = new float[agentCount];
        _prevSteer = new float[agentCount];
        _bestDist = new float[agentCount];
        _stallRefDist = new float[agentCount];
        _bestTick = new int[agentCount];
        _stallTick = new int[agentCount];
        Reset(AgentRange.All(agentCount));
    }

    public RacingTrack Track => _track;
    public CarPhysics Physics => _physics;
    public Sensors Sensors => _sensors;
    public int TimeLimitTicks => _timeLimitTicks;

    public int AgentCount { get; }
    public int InputCount { get; }
    public int OutputCount => RacingSettings.OutputCount;
    public float[] Inputs { get; }
    public float[] Outputs { get; }

    public void Reset(AgentRange range)
    {
        CheckRange(range);
        var start = _track.Track.Start;
        float startDist = _track.StartDistance;
        for (int i = range.Start; i < range.End; i++)
        {
            _x[i] = start.X;
            _y[i] = start.Y;
            _vx[i] = 0f;
            _vy[i] = 0f;
            _yawRate[i] = 0f;
            _heading[i] = _track.StartHeading;
            _tick[i] = 0;
            _status[i] = AgentStatus.Running;
            _finishTicks[i] = 0f;
            _driven[i] = 0f;
            _prevThrottle[i] = 0f;
            _prevSteer[i] = 0f;
            _bestDist[i] = startDist;
            _stallRefDist[i] = startDist;
            _bestTick[i] = 0;
            _stallTick[i] = 0;
        }
        Array.Clear(Outputs, range.Start * OutputCount, range.Count * OutputCount);
    }

    public void Sense(AgentRange range)
    {
        CheckRange(range);
        for (int i = range.Start; i < range.End; i++)
            if (_status[i] == AgentStatus.Running) SenseOne(i);
    }

    public void Advance(AgentRange range)
    {
        CheckRange(range);
        for (int i = range.Start; i < range.End; i++)
            if (_status[i] == AgentStatus.Running) AdvanceOne(i);
    }

    public void Step(AgentRange range, int ticks, IAgentPolicy policy)
    {
        CheckRange(range);
        ArgumentNullException.ThrowIfNull(policy);
        int inCount = InputCount, outCount = OutputCount;
        for (int i = range.Start; i < range.End; i++)
        {
            for (int t = 0; t < ticks && _status[i] == AgentStatus.Running; t++)
            {
                SenseOne(i);
                policy.Act(i, Inputs.AsSpan(i * inCount, inCount), Outputs.AsSpan(i * outCount, outCount));
                AdvanceOne(i);
            }
        }
    }

    public int Tick(int agent) => _tick[agent];
    public AgentStatus Status(int agent) => _status[agent];
    public bool IsDone(int agent) => _status[agent] != AgentStatus.Running;

    public float Score(int agent) => _status[agent] == AgentStatus.Finished
        ? RacingFitness.FinishedScore(_finishTicks[agent], _timeLimitTicks)
        : RacingFitness.UnfinishedScore(_bestDist[agent], _track.StartDistance, _bestTick[agent], _timeLimitTicks);

    /// <summary>Progress 0…1 along the track (1 once finished).</summary>
    public float Progress(int agent) => _status[agent] == AgentStatus.Finished
        ? 1f
        : RacingFitness.ProgressFraction(_bestDist[agent], _track.StartDistance);

    public float FinishTicks(int agent) => _finishTicks[agent];

    /// <summary>Cells gained along the distance field: start distance − best distance (0 when the track is not finishable).</summary>
    public float ProgressDistance(int agent) => _track.IsFinishable ? _track.StartDistance - _bestDist[agent] : 0f;

    /// <summary>Distance driven since the reset, cells.</summary>
    public float DistanceDriven(int agent) => _driven[agent];

    /// <summary>Best (lowest) distance-field value reached so far.</summary>
    public float BestDistance(int agent) => _bestDist[agent];

    /// <summary>The car's physical state (a copy).</summary>
    public CarState Car(int agent) => new()
    {
        X = _x[agent],
        Y = _y[agent],
        Vx = _vx[agent],
        Vy = _vy[agent],
        Heading = _heading[agent],
        YawRate = _yawRate[agent],
    };

    /// <summary>Overwrites a car's physical state (tests and scripted set-ups; the progress record is kept).</summary>
    public void SetCar(int agent, in CarState car)
    {
        _x[agent] = car.X;
        _y[agent] = car.Y;
        _vx[agent] = car.Vx;
        _vy[agent] = car.Vy;
        _heading[agent] = car.Heading & FastMath.AngleUnitsMask;
        _yawRate[agent] = car.YawRate;
    }

    public void WriteSnapshot(AgentRange range, AgentSnapshot target)
    {
        CheckRange(range);
        ArgumentNullException.ThrowIfNull(target);
        if (range.End > target.Capacity) throw new ArgumentException("Snapshot is too small for the range.", nameof(target));
        for (int i = range.Start; i < range.End; i++)
        {
            target.X[i] = _x[i];
            target.Y[i] = _y[i];
            target.Angle[i] = _heading[i] * FastMath.RadiansPerUnit;
            target.Status[i] = _status[i];
        }
    }

    public void Record(int agent, Recording recording)
    {
        ArgumentNullException.ThrowIfNull(recording);
        recording.Add(_x[agent], _y[agent], _heading[agent]);
    }

    private void SenseOne(int i)
    {
        CarState car = Car(i);
        _sensors.Write(car, _prevThrottle[i], _prevSteer[i], _tick[i] * _inverseTimeLimit, _driven[i] * _inverseTrackLength,
            Inputs.AsSpan(i * InputCount, InputCount));
    }

    private void AdvanceOne(int i)
    {
        int o = i * RacingSettings.OutputCount;
        float throttle = Clamp(Outputs[o]), steer = Clamp(Outputs[o + 1]);
        _prevThrottle[i] = throttle;
        _prevSteer[i] = steer;

        CarState car = Car(i);
        float x0 = car.X, y0 = car.Y;
        TickResult result = _physics.Tick(ref car, throttle, steer);
        SetCar(i, car);

        int tickBefore = _tick[i];
        int tick = tickBefore + 1;
        _tick[i] = tick;
        float mx = car.X - x0, my = car.Y - y0;
        _driven[i] += MathF.Sqrt(mx * mx + my * my); // IEEE sqrt: deterministic

        if (result.Outcome == TickOutcome.Crashed)
        {
            _status[i] = AgentStatus.Crashed;
            return;
        }
        if (result.Outcome == TickOutcome.Finished)
        {
            _finishTicks[i] = tickBefore + result.FinishFraction;
            _bestDist[i] = 0f;
            _bestTick[i] = tick;
            _status[i] = AgentStatus.Finished;
            return;
        }

        var progress = new RacingFitness.Progress
        {
            BestDistance = _bestDist[i],
            BestTick = _bestTick[i],
            StallDistanceRef = _stallRefDist[i],
            StallTick = _stallTick[i],
        };
        progress.Update(_track.Field.Sample(car.X, car.Y), tick);
        _bestDist[i] = progress.BestDistance;
        _bestTick[i] = progress.BestTick;
        _stallRefDist[i] = progress.StallDistanceRef;
        _stallTick[i] = progress.StallTick;

        if (tick >= _timeLimitTicks) _status[i] = AgentStatus.TimedOut;
        else if (progress.IsStalled(tick, _stallTicks)) _status[i] = AgentStatus.Stalled;
    }

    /// <summary>Clamps an output to [−1, 1]; NaN becomes 0 (memory inputs store the value actually applied).</summary>
    private static float Clamp(float v) => v > 1f ? 1f : v < -1f ? -1f : float.IsNaN(v) ? 0f : v;

    private void CheckRange(AgentRange range)
    {
        if (range.Start < 0 || range.Count < 0 || range.End > AgentCount)
            throw new ArgumentOutOfRangeException(nameof(range), $"{range} is outside 0…{AgentCount}.");
    }
}
