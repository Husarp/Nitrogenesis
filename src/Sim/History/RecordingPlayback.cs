using Nitrogenesis.Sim.Brain;
using Nitrogenesis.Sim.Core;

namespace Nitrogenesis.Sim.History;

/// <summary>
/// Plays a <see cref="Recording"/> ("Watch best", PLAN §5 "Replay"): its own <see cref="SpeedController"/> turns real
/// time into ticks, one recording sample per tick, and the car is drawn between two samples at 1× and slower (the
/// same rule as the training view, PLAN §4). It stops at the last sample. MAX is not offered: a recording is short,
/// so the fastest step is 100×.
/// </summary>
/// <remarks>Sample t is the pose after t ticks (sample 0 = the start). Does not allocate. Not thread-safe.</remarks>
public sealed class RecordingPlayback
{
    private readonly Recording _recording;
    private readonly SpeedController _clock = new();
    private readonly SpeedMeter _meter = new();

    public RecordingPlayback(Recording recording)
    {
        ArgumentNullException.ThrowIfNull(recording);
        if (recording.Count == 0) throw new ArgumentException("The recording is empty.", nameof(recording));
        _recording = recording;
    }

    public Recording Recording => _recording;

    /// <summary>The sample shown (ticks played), 0 … <see cref="LastTick"/>.</summary>
    public int Tick { get; private set; }

    /// <summary>The last sample: the tick the car stopped (finished, crashed, stalled) or the generation ended.</summary>
    public int LastTick => _recording.Count - 1;

    public bool Ended => Tick >= LastTick;

    public bool Paused
    {
        get => _clock.Paused;
        set => _clock.Paused = value;
    }

    public int SpeedIndex
    {
        get => _clock.Index;
        set => _clock.Index = Math.Clamp(value, 0, MaxSpeedIndex);
    }

    /// <summary>The fastest step offered (100×).</summary>
    public static int MaxSpeedIndex => SpeedController.MaxIndex - 1;

    public double TargetSpeed => _clock.TargetSpeed;
    public double ActualSpeed => _meter.Actual;

    public void Faster() => SpeedIndex = _clock.Index + 1;
    public void Slower() => SpeedIndex = _clock.Index - 1;

    /// <summary>One tick while paused.</summary>
    public void StepOnce() => _clock.StepOnce();

    /// <summary>Back to the start (keeps speed and pause).</summary>
    public void Restart()
    {
        Tick = 0;
        _clock.Consume(int.MaxValue); // drop the whole ticks still owed
    }

    /// <summary>Adds <paramref name="realSeconds"/> of real time and moves on by the whole ticks due (up to the last sample).</summary>
    public void Advance(double realSeconds)
    {
        bool running = !_clock.Paused && !Ended;
        if (!Ended) _clock.Advance(realSeconds);
        int due = _clock.WholeTicks;
        int ran = Math.Min(due, LastTick - Tick);
        Tick += ran;
        _clock.Consume(Ended ? due : ran);
        _meter.AddTicks(ran);
        _meter.AddTime(realSeconds, !running, _clock.TargetSpeed);
    }

    /// <summary>
    /// The pose to draw: between sample <see cref="Tick"/> and the next by the render fraction at 1× and slower,
    /// else the sample itself. Angle in radians (shortest arc).
    /// </summary>
    public void Pose(out float x, out float y, out float angle)
    {
        int t = Tick;
        float alpha = _clock.Interpolates && t < LastTick ? _clock.Alpha : 0f;
        if (alpha >= 1f) alpha = 0f; // whole ticks still owed: draw the sample itself
        float x0 = _recording.X(t), y0 = _recording.Y(t), a0 = _recording.Heading(t) * FastMath.RadiansPerUnit;
        if (alpha == 0f)
        {
            x = x0;
            y = y0;
            angle = a0;
            return;
        }
        x = x0 + (_recording.X(t + 1) - x0) * alpha;
        y = y0 + (_recording.Y(t + 1) - y0) * alpha;
        angle = SnapshotInterpolation.LerpAngle(a0, _recording.Heading(t + 1) * FastMath.RadiansPerUnit, alpha);
    }
}
