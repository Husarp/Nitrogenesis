using Nitrogenesis.Sim.Brain;
using Nitrogenesis.Sim.Map;

namespace Nitrogenesis.Sim.Racing;

/// <summary>
/// A track prepared for racing with given settings: its clearance and distance field (grass cost from the
/// physics, PLAN §3.2) and the start distance. Built once per track/settings and shared read-only by
/// <see cref="RacingMode"/>, the sensors and the <see cref="ReferenceDriver"/>.
/// </summary>
public sealed class RacingTrack
{
    public RacingTrack(Track track, RacingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(settings);
        Track = track;
        Settings = settings.Clamped();
        Clearance = Clearance.Compute(track.Grid);
        Field = DistanceField.ForRacing(track.Grid, Clearance, CarSize.HalfWidth, Settings.GrassCost);
        StartDistance = Field.Sample(track.Start.X, track.Start.Y);
        StartHeading = FastMath.DegreesToUnits(track.Start.AngleDeg);
        StartOverlapsWall = CarPhysics.Overlaps(track.Grid, track.Start.X, track.Start.Y, StartHeading);
    }

    public Track Track { get; }
    public Grid Grid => Track.Grid;
    /// <summary>The settings, clamped to their hard limits.</summary>
    public RacingSettings Settings { get; }
    public Clearance Clearance { get; }
    public DistanceField Field { get; }

    /// <summary>Distance-field value at the start ("track length" for fitness and the memory input); +∞ if the finish is unreachable.</summary>
    public float StartDistance { get; }

    /// <summary>Start heading in angle units.</summary>
    public int StartHeading { get; }

    /// <summary>The car outline at the start pose touches a wall or danger: no car can ever move on this track.</summary>
    public bool StartOverlapsWall { get; }

    /// <summary>
    /// True when the start can reach the finish: the start distance is finite and positive and the car at the
    /// start pose does not overlap a wall.
    /// </summary>
    public bool IsFinishable => StartDistance > 0f && StartDistance < DistanceField.Unreachable && !StartOverlapsWall;
}
