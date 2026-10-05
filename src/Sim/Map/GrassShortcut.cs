using Nitrogenesis.Sim.Racing;

namespace Nitrogenesis.Sim.Map;

/// <summary>
/// Grass-shortcut check (PLAN §3.2): compares the best road-only route from the start with the best route
/// that may also use grass. If the grass route is more than 10 % faster, cars will learn to cut across the
/// grass: the editor warns and the generator rejects the track.
/// </summary>
public static class GrassShortcut
{
    /// <summary>"More than 10 % faster": the road-only route takes over 1.1× the time of the grass route.</summary>
    public const double Threshold = 1.1;

    /// <summary>Route costs from the start (in road-cell units, i.e. proportional to time at top speed).</summary>
    /// <param name="RoadOnly">Best cost without entering grass; +∞ if the finish needs grass.</param>
    /// <param name="WithGrass">Best cost when grass is allowed at its normal cost.</param>
    public readonly record struct Result(float RoadOnly, float WithGrass)
    {
        /// <summary>True when the grass route is more than <see cref="Threshold"/> times faster.</summary>
        public bool IsShortcut => WithGrass < float.PositiveInfinity && RoadOnly > WithGrass * Threshold;
    }

    public static Result Check(Track track, Clearance clearance, float halfWidth, double grassCost)
    {
        var withGrass = DistanceField.ForRacing(track.Grid, clearance, halfWidth, grassCost);
        var roadOnly = DistanceField.ForRacing(track.Grid, clearance, halfWidth, double.PositiveInfinity);
        float sx = track.Start.X, sy = track.Start.Y;
        return new Result(roadOnly.Sample(sx, sy), withGrass.Sample(sx, sy));
    }

    /// <summary>Convenience overload with the racing car's half-width.</summary>
    public static Result Check(Track track, double grassCost) =>
        Check(track, Clearance.Compute(track.Grid), CarSize.HalfWidth, grassCost);
}
