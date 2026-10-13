using Nitrogenesis.Sim.Brain;
using Nitrogenesis.Sim.Core;
using Nitrogenesis.Sim.History;

namespace Nitrogenesis.Sim.Racing;

/// <summary>
/// A ghost in a race (PLAN §6.5): a recording played one sample per tick on the race's clock (so it stays in step
/// with the player and restarts with them), with the ticks at which it first reached 25 / 50 / 75 % of the track
/// worked out on the race's own distance field. A ghost that did not finish stops where its recording ends and is
/// then hidden, like a crashed or stalled car in training; one that finished stays on the finish.
/// </summary>
/// <remarks>Pose and status are pure functions of the tick, so two races given the same inputs show the same ghosts.</remarks>
public sealed class RaceGhost
{
    /// <summary>Most route points kept for drawing (the recording is thinned by a power of two to fit).</summary>
    public const int MaxRoutePoints = RouteTrails.MaxSamplesPerCar;

    private readonly Recording _recording;
    private readonly int[] _splitTicks = new int[RaceSplits.Count];

    public RaceGhost(GhostEntry entry, RacingTrack track)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(track);
        Entry = entry;
        _recording = entry.DecodeRecording();
        if (_recording.Count == 0) throw new InvalidDataException("The ghost's recording is empty.");
        while ((_recording.Count + RouteStride - 1) / RouteStride > MaxRoutePoints) RouteStride *= 2;

        // Progress like a training car's: the best distance-field value so far along the recorded route.
        Array.Fill(_splitTicks, -1);
        float best = track.StartDistance;
        float progress = 0f;
        int next = 0;
        for (int t = 0; t < _recording.Count; t++)
        {
            float d = track.Field.Sample(_recording.X(t), _recording.Y(t));
            if (d < best) best = d;
            progress = RacingFitness.ProgressFraction(best, track.StartDistance);
            while (next < RaceSplits.Count && progress >= RaceSplits.Fraction(next)) _splitTicks[next++] = t;
        }
        Progress = entry.Finished ? 1f : progress;
    }

    public GhostEntry Entry { get; }
    public Recording Recording => _recording;

    /// <summary>The last sample: the tick the car stopped (or the generation ended).</summary>
    public int LastTick => _recording.Count - 1;

    public bool Finished => Entry.Finished;

    /// <summary>Best progress 0…1 along the race's track over the whole recording (1 when it finished).</summary>
    public float Progress { get; }

    /// <summary>Ticks between two route points.</summary>
    public int RouteStride { get; } = 1;

    /// <summary>The tick at which the ghost first reached split <paramref name="split"/> (25 / 50 / 75 %), or −1 when it never did.</summary>
    public int SplitTick(int split) => _splitTicks[split];

    /// <summary>Running until its last sample, then Finished or (a car that did not finish) Crashed, which the view hides.</summary>
    public AgentStatus StatusAt(int tick) => tick < LastTick ? AgentStatus.Running : Finished ? AgentStatus.Finished : AgentStatus.Crashed;

    /// <summary>
    /// The pose after <paramref name="tick"/> ticks (sample <paramref name="tick"/>, the last one when past the end),
    /// moved on towards the next sample by <paramref name="alpha"/> (0…1) so slow motion is smooth. Angle in radians.
    /// </summary>
    public void Pose(int tick, float alpha, out float x, out float y, out float angle)
    {
        int t = Math.Clamp(tick, 0, LastTick);
        alpha = t < LastTick && alpha > 0f ? Math.Min(alpha, 1f) : 0f;
        x = _recording.X(t);
        y = _recording.Y(t);
        angle = _recording.Heading(t) * FastMath.RadiansPerUnit;
        if (alpha == 0f) return;
        x += (_recording.X(t + 1) - x) * alpha;
        y += (_recording.Y(t + 1) - y) * alpha;
        angle = SnapshotInterpolation.LerpAngle(angle, _recording.Heading(t + 1) * FastMath.RadiansPerUnit, alpha);
    }

    /// <summary>Copies the route up to <paramref name="tick"/> (every <see cref="RouteStride"/>-th sample) into the arrays; returns the point count.</summary>
    public int CopyRoute(int tick, float[] x, float[] y)
    {
        int last = Math.Clamp(tick, 0, LastTick);
        int n = 0;
        for (int t = 0; t <= last; t += RouteStride, n++)
        {
            x[n] = _recording.X(t);
            y[n] = _recording.Y(t);
        }
        return n;
    }
}
