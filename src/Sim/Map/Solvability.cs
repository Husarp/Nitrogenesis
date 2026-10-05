using Nitrogenesis.Sim.Brain;
using Nitrogenesis.Sim.Racing;

namespace Nitrogenesis.Sim.Map;

/// <summary>Result of the "finishable" check (PLAN §3.2).</summary>
public enum SolvabilityResult
{
    /// <summary>The finish can be reached from the start.</summary>
    Solvable,
    /// <summary>No Finish cell wide enough for the car exists.</summary>
    NoFinish,
    /// <summary>The start position is outside the map.</summary>
    StartOutside,
    /// <summary>The start cell is blocking or too narrow for the car.</summary>
    StartNotPassable,
    /// <summary>The car outline at the start pose touches a wall or danger, so the car could never move.</summary>
    StartOverlapsWall,
    /// <summary>The start is passable, but no path leads from it to the finish.</summary>
    FinishUnreachable,
}

/// <summary>Is the start connected to the finish in the distance field? (Live in the editor; generator check.)</summary>
public static class Solvability
{
    public static SolvabilityResult Check(Track track, Clearance clearance, DistanceField field, float halfWidth)
    {
        Grid grid = track.Grid;
        bool anyFinish = false;
        for (int i = 0; i < grid.CellCount && !anyFinish; i++)
            anyFinish = (CellType)grid.Cells[i] == CellType.Finish && clearance.IsPassable(i, halfWidth);
        if (!anyFinish) return SolvabilityResult.NoFinish;

        float sx = track.Start.X, sy = track.Start.Y;
        if (!(sx >= 0 && sy >= 0 && sx < grid.Width && sy < grid.Height)) return SolvabilityResult.StartOutside;
        int x = (int)sx, y = (int)sy;
        if (!clearance.IsPassable(grid.Index(x, y), halfWidth)) return SolvabilityResult.StartNotPassable;
        if (CarPhysics.Overlaps(grid, sx, sy, FastMath.DegreesToUnits(track.Start.AngleDeg))) return SolvabilityResult.StartOverlapsWall;
        return field.IsReachable(x, y) ? SolvabilityResult.Solvable : SolvabilityResult.FinishUnreachable;
    }

    /// <summary>Convenience overload: computes clearance and the field with the racing car's half-width.</summary>
    public static SolvabilityResult Check(Track track, double grassCost)
    {
        var clearance = Clearance.Compute(track.Grid);
        var field = DistanceField.ForRacing(track.Grid, clearance, CarSize.HalfWidth, grassCost);
        return Check(track, clearance, field, CarSize.HalfWidth);
    }
}
