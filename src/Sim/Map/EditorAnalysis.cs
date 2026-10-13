using Nitrogenesis.Sim.Brain;
using Nitrogenesis.Sim.Racing;

namespace Nitrogenesis.Sim.Map;

public enum MessageSeverity { Ok, Warning, Error }

/// <summary>What a status-bar message is about (stable, for tests and the UI).</summary>
public enum MessageCode
{
    Finishable,
    NoFinish,
    StartOutside,
    StartNotPassable,
    StartOverlapsWall,
    FinishUnreachable,
    NoRoadBehindStart,
    GrassShortcut,
    VeryShort,
}

public readonly record struct AnalysisMessage(MessageSeverity Severity, MessageCode Code, string Text);

/// <summary>
/// Everything the editor's live checks know about one snapshot of a track (PLAN §3.2, §6.3). Immutable by
/// convention: the arrays are never changed after <see cref="EditorAnalysis.Run"/> returns.
/// </summary>
public sealed record EditorAnalysisResult
{
    /// <summary>The document revision this result was made for; the UI shows it only if it still equals the document's. Set by the runner (0 from a plain Run).</summary>
    public long Revision { get; init; }

    public int Width { get; init; }
    public int Height { get; init; }

    /// <summary>Why the track is or is not finishable (the same cases as <see cref="Solvability"/>).</summary>
    public SolvabilityResult State { get; init; }
    public bool IsFinishable => State == SolvabilityResult.Solvable;

    /// <summary>Passable cells (for the car) from which the finish cannot be reached; filled only when <see cref="State"/> is FinishUnreachable (else all false). Row-major like the grid.</summary>
    public bool[] UnreachableMask { get; init; } = [];
    public int UnreachableCount { get; init; }

    /// <summary>Non-blocking cells that are too narrow for the car's half-width (PLAN §3.2). Row-major.</summary>
    public bool[] TooNarrowMask { get; init; } = [];
    public int TooNarrowCount { get; init; }

    /// <summary>True when one car length of free road lies behind the start (see <see cref="EditorAnalysis.HasRoadBehindStart"/>); also true when the start is outside the map or unusable (that has its own error).</summary>
    public bool HasRoadBehindStart { get; init; } = true;

    public bool IsGrassShortcut { get; init; }
    /// <summary>Best cost from the start without / with grass, in road-cell units (+∞ = no route).</summary>
    public float RoadOnlyCost { get; init; } = float.PositiveInfinity;
    public float WithGrassCost { get; init; } = float.PositiveInfinity;
    /// <summary>The route that follows the with-grass field from the start to the finish, as cell-centre points in cells; empty unless <see cref="IsGrassShortcut"/>.</summary>
    public (float X, float Y)[] ShortcutRoute { get; init; } = [];

    /// <summary>Start distance in the with-grass field (cells); null when not finishable.</summary>
    public float? PathLength { get; init; }
    /// <summary>pathLength / (<see cref="EditorAnalysis.EstimateSpeedFactor"/> × max speed), seconds; null when not finishable.</summary>
    public float? EstimatedSeconds { get; init; }
    public bool IsVeryShort { get; init; }

    public IReadOnlyList<AnalysisMessage> Messages { get; init; } = [];

    public MessageSeverity Worst
    {
        get
        {
            var worst = MessageSeverity.Ok;
            foreach (var m in Messages) if (m.Severity > worst) worst = m.Severity;
            return worst;
        }
    }

    /// <summary>One line for logs, e.g. "Not finishable: ..." or "Finishable ≈ 27 s".</summary>
    public string Summary => string.Join(" | ", Messages.Select(m => m.Text)) +
                             (EstimatedSeconds is { } s ? $" (≈ {s:0} s)" : "");
}

/// <summary>
/// The editor's live checks (PLAN §3.2): finishable, too-narrow cells, start problems, grass shortcut and the
/// estimated time. A pure function of a track snapshot and the racing settings; safe to run on any thread.
/// </summary>
public static class EditorAnalysis
{
    /// <summary>The live estimate assumes the car averages this share of its top speed (PLAN §3.2).</summary>
    public const float EstimateSpeedFactor = 0.6f;

    /// <summary>A finishable track with an estimate below this many seconds gets a "very short" warning.</summary>
    public const float VeryShortSeconds = 20f;

    /// <summary>Spacing of the samples behind the start (cells).</summary>
    public const float RoadBehindStep = 0.5f;

    /// <summary>
    /// Runs all checks on <paramref name="track"/> (never changed). Checks <paramref name="ct"/> between phases and
    /// throws <see cref="OperationCanceledException"/> when it is cancelled, so a stale run never returns a result.
    /// </summary>
    public static EditorAnalysisResult Run(Track track, RacingSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(settings);
        settings = settings.Clamped();
        Grid grid = track.Grid;
        int n = grid.CellCount;
        const float hw = CarSize.HalfWidth;
        ct.ThrowIfCancellationRequested();

        var clearance = Clearance.Compute(grid);
        ct.ThrowIfCancellationRequested();

        var tooNarrow = new bool[n];
        int tooNarrowCount = 0;
        for (int i = 0; i < n; i++)
        {
            if (CellTypes.IsBlocking((CellType)grid.Cells[i]) || clearance.IsPassable(i, hw)) continue;
            tooNarrow[i] = true;
            tooNarrowCount++;
        }

        var withGrass = DistanceField.ForRacing(grid, clearance, hw, settings.GrassCost);
        ct.ThrowIfCancellationRequested();

        var state = Solvability.Check(track, clearance, withGrass, hw);
        var unreachable = new bool[n];
        int unreachableCount = 0;
        if (state == SolvabilityResult.FinishUnreachable)
        {
            for (int i = 0; i < n; i++)
            {
                if (!clearance.IsPassable(i, hw) || withGrass.Values[i] != DistanceField.Unreachable) continue;
                unreachable[i] = true;
                unreachableCount++;
            }
        }

        // Solvability reports a missing finish before the start; the start can still be broken too (a new map: no finish
        // and the start in wall). Then that is said as well, instead of the less helpful "no road behind the start".
        SolvabilityResult? startProblem = state is SolvabilityResult.StartOutside or SolvabilityResult.StartNotPassable or SolvabilityResult.StartOverlapsWall
            ? state
            : state == SolvabilityResult.NoFinish ? StartProblem(track, clearance, hw) : null;
        // Outside / too narrow / touching a wall already have their own error; the road check would only repeat it.
        bool roadBehind = startProblem is not null || HasRoadBehindStart(track, clearance);
        ct.ThrowIfCancellationRequested();

        bool shortcut = false;
        float roadOnlyCost = float.PositiveInfinity, grassCost = float.PositiveInfinity;
        (float X, float Y)[] route = [];
        float? pathLength = null, estimate = null;
        bool veryShort = false;
        if (state == SolvabilityResult.Solvable)
        {
            var roadOnly = DistanceField.ForRacing(grid, clearance, hw, double.PositiveInfinity);
            ct.ThrowIfCancellationRequested();
            roadOnlyCost = roadOnly.Sample(track.Start.X, track.Start.Y);
            grassCost = withGrass.Sample(track.Start.X, track.Start.Y);
            shortcut = new GrassShortcut.Result(roadOnlyCost, grassCost).IsShortcut;
            if (shortcut) route = Descend(grid, withGrass, track.Start.X, track.Start.Y, ct);
        }
        if (state == SolvabilityResult.Solvable)
        {
            float len = withGrass.Sample(track.Start.X, track.Start.Y);
            pathLength = len;
            estimate = len / (EstimateSpeedFactor * settings.MaxSpeed);
            veryShort = estimate < VeryShortSeconds;
        }
        ct.ThrowIfCancellationRequested();

        var messages = new List<AnalysisMessage>();
        switch (state)
        {
            case SolvabilityResult.Solvable:
                messages.Add(new(MessageSeverity.Ok, MessageCode.Finishable, "Finishable"));
                break;
            case SolvabilityResult.NoFinish:
                messages.Add(new(MessageSeverity.Error, MessageCode.NoFinish, "Not finishable: there is no Finish zone the car fits on"));
                break;
            case SolvabilityResult.StartOutside:
                messages.Add(new(MessageSeverity.Error, MessageCode.StartOutside, "Not finishable: the start is outside the map"));
                break;
            case SolvabilityResult.StartNotPassable:
                messages.Add(new(MessageSeverity.Error, MessageCode.StartNotPassable, "Not finishable: " + StartNotPassableText.ToLowerInvariant()[..1] + StartNotPassableText[1..]));
                break;
            case SolvabilityResult.StartOverlapsWall:
                messages.Add(new(MessageSeverity.Error, MessageCode.StartOverlapsWall, "Not finishable: the car at the start touches a wall or danger"));
                break;
            case SolvabilityResult.FinishUnreachable:
                messages.Add(new(MessageSeverity.Error, MessageCode.FinishUnreachable, "Not finishable: the finish can't be reached from the start (red = cut off)"));
                break;
        }
        if (state == SolvabilityResult.NoFinish && startProblem is { } sp)
            messages.Add(sp switch
            {
                SolvabilityResult.StartOutside => new(MessageSeverity.Error, MessageCode.StartOutside, "The start is outside the map"),
                SolvabilityResult.StartNotPassable => new(MessageSeverity.Error, MessageCode.StartNotPassable, StartNotPassableText),
                _ => new(MessageSeverity.Error, MessageCode.StartOverlapsWall, "The car at the start touches a wall or danger"),
            });
        if (!roadBehind) // a warning: the track can be finished, but the cars start with their tail in grass or wall (PLAN §3.1)
            messages.Add(new(MessageSeverity.Warning, MessageCode.NoRoadBehindStart,
                $"Start needs {CarSize.Length:0} cells of free road behind it"));
        if (shortcut)
            messages.Add(new(MessageSeverity.Warning, MessageCode.GrassShortcut, float.IsPositiveInfinity(roadOnlyCost)
                ? "Cars will cut across the grass here: the finish is only reachable over grass"
                : "Cars will cut across the grass here: the grass route is more than 10 % faster"));
        if (veryShort)
            messages.Add(new(MessageSeverity.Warning, MessageCode.VeryShort, $"Very short track: under {VeryShortSeconds:0} s"));

        return new EditorAnalysisResult
        {
            Width = grid.Width,
            Height = grid.Height,
            State = state,
            UnreachableMask = unreachable,
            UnreachableCount = unreachableCount,
            TooNarrowMask = tooNarrow,
            TooNarrowCount = tooNarrowCount,
            HasRoadBehindStart = roadBehind,
            IsGrassShortcut = shortcut,
            RoadOnlyCost = roadOnlyCost,
            WithGrassCost = grassCost,
            ShortcutRoute = route,
            PathLength = pathLength,
            EstimatedSeconds = estimate,
            IsVeryShort = veryShort,
            Messages = messages,
        };
    }

    private const string StartNotPassableText = "The start is on a wall or danger cell, or in a spot too narrow for the car";

    /// <summary>The start checks of <see cref="Solvability.Check(Track, Clearance, DistanceField, float)"/> on their own (they run there only after the finish check).</summary>
    private static SolvabilityResult? StartProblem(Track track, Clearance clearance, float halfWidth)
    {
        Grid grid = track.Grid;
        if (!StartInside(track, grid)) return SolvabilityResult.StartOutside;
        if (!clearance.IsPassable(grid.Index((int)track.Start.X, (int)track.Start.Y), halfWidth)) return SolvabilityResult.StartNotPassable;
        if (CarPhysics.Overlaps(grid, track.Start.X, track.Start.Y, FastMath.DegreesToUnits(track.Start.AngleDeg))) return SolvabilityResult.StartOverlapsWall;
        return null;
    }

    private static bool StartInside(Track t, Grid g) =>
        t.Start.X >= 0 && t.Start.Y >= 0 && t.Start.X < g.Width && t.Start.Y < g.Height;

    /// <summary>
    /// "At least one car length of free road behind the start" (PLAN §3.1): the car's centre line is sampled from
    /// the start backwards (against the heading) every <see cref="RoadBehindStep"/> cells up to
    /// <see cref="CarSize.Length"/> cells. Every sample must lie in a Road or Finish cell (not grass, wall or danger)
    /// that the car fits on (clearance ≥ half-width). A start outside the map fails.
    /// </summary>
    public static bool HasRoadBehindStart(Track track, Clearance clearance)
    {
        Grid grid = track.Grid;
        if (!StartInside(track, grid)) return false;
        int heading = FastMath.DegreesToUnits(track.Start.AngleDeg);
        float dx = FastMath.CosUnits(heading), dy = FastMath.SinUnits(heading);
        for (float d = 0; d <= CarSize.Length + 1e-4f; d += RoadBehindStep)
        {
            float x = track.Start.X - dx * d, y = track.Start.Y - dy * d;
            if (!(x >= 0 && y >= 0 && x < grid.Width && y < grid.Height)) return false;
            int i = grid.Index((int)x, (int)y);
            var type = (CellType)grid.Cells[i];
            if (type != CellType.Road && type != CellType.Finish) return false;
            if (!clearance.IsPassable(i, CarSize.HalfWidth)) return false;
        }
        return true;
    }

    /// <summary>Follows the field downhill from (x, y) to a Finish cell: always to the lowest of the 8 neighbours. Cell-centre points.</summary>
    private static (float X, float Y)[] Descend(Grid grid, DistanceField field, float x, float y, CancellationToken ct)
    {
        int w = grid.Width, h = grid.Height;
        int cx = (int)x, cy = (int)y;
        var pts = new List<(float, float)> { (x, y) };
        float cur = field.Values[cy * w + cx];
        for (int guard = 0; guard < grid.CellCount; guard++)
        {
            if (guard % 256 == 0) ct.ThrowIfCancellationRequested();
            if (cur == 0f) break;
            int bx = -1, by = -1;
            float best = cur;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int nx = cx + dx, ny = cy + dy;
                    if ((uint)nx >= (uint)w || (uint)ny >= (uint)h) continue;
                    float v = field.Values[ny * w + nx];
                    if (v >= best) continue;
                    if (dx != 0 && dy != 0 && CellTypes.IsBlocking(grid[cx + dx, cy]) && CellTypes.IsBlocking(grid[cx, cy + dy])) continue;
                    best = v; bx = nx; by = ny;
                }
            if (bx < 0) break;
            cx = bx; cy = by; cur = best;
            pts.Add((cx + 0.5f, cy + 0.5f));
        }
        return pts.ToArray();
    }
}
