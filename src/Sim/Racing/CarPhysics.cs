using Nitrogenesis.Sim.Brain;
using System.Runtime.CompilerServices;
using Nitrogenesis.Sim.Map;

namespace Nitrogenesis.Sim.Racing;

/// <summary>How a physics tick ended for the car.</summary>
public enum TickOutcome : byte
{
    /// <summary>Still driving.</summary>
    Moving,
    /// <summary>The car centre entered a Finish cell during this tick.</summary>
    Finished,
    /// <summary>The car touched danger (or a wall in kill mode) and is destroyed.</summary>
    Crashed,
}

/// <summary>Result of <see cref="CarPhysics.Tick"/>.</summary>
/// <param name="Outcome">How the tick ended.</param>
/// <param name="FinishFraction">When finished: the share of this tick that had passed when the centre entered the finish (0…1].</param>
/// <param name="HitWall">The car touched a wall this tick (blocked or bounced).</param>
/// <param name="OnGrass">The car centre was on grass at the start of the tick (grass grip, drag and top speed applied).</param>
public readonly record struct TickResult(TickOutcome Outcome, float FinishFraction, bool HitWall, bool OnGrass);

/// <summary>
/// Arcade top-down car physics (PLAN §3.3). One <see cref="Tick"/> is 1/60 s:
/// accelerate along the heading → steer (rate scales with speed) → grip removes lateral velocity →
/// surface drag → clamp to the surface's top speed → move in sub-steps with outline collision.
/// </summary>
/// <remarks>
/// <para><b>Determinism:</b> float +, −, ×, ÷, Floor, Round and one IEEE square root (correctly rounded,
/// so identical everywhere) per tick; sin/cos come from <see cref="FastMath"/> tables; the heading is an
/// integer angle. Nothing allocates. The instance is immutable, so one physics object serves all workers.</para>
/// <para><b>Sub-stepping:</b> n = ceil(speed·dt / 0.5) sub-steps, so the car moves at most half a cell
/// between collision checks and cannot tunnel through a 1-cell wall.</para>
/// <para><b>Collision:</b> 20 points on the car outline, at most 0.5 cell apart (7 per long side, 5 per short
/// side, corners shared). A point inside Danger kills; inside Wall (or outside the map) blocks. Grid walls have
/// axis-aligned faces, so the wall normal is found by trying the x and y parts of the move separately.
/// The car's state never overlaps a wall at the end of a sub-step.</para>
/// </remarks>
public sealed class CarPhysics
{
    /// <summary>Longest move between two collision checks, cells.</summary>
    public const float MaxSubStep = 0.5f;
    /// <summary>Largest gap between neighbouring outline sample points, cells.</summary>
    public const float MaxOutlineSpacing = 0.5f;

    /// <summary>
    /// A car whose centre lies in a cell with more clearance than this cannot touch anything blocking, so the
    /// outline check is skipped. The farthest outline point is √(1.5² + 0.9²) ≈ 1.7493 from the centre, and the
    /// centre is at most √½ ≈ 0.7071 from its cell's centre; 2.46 covers both with margin. Exact shortcut:
    /// it never changes a result, only skips work.
    /// </summary>
    private const float BroadphaseClearance = 2.46f;

    private static readonly float[] OutlineX, OutlineY;

    static CarPhysics()
    {
        // Long sides (y = ±half width): ceil(3.0 / 0.5) = 6 gaps → 7 points each, corners included.
        // Short sides (x = ±half length): ceil(1.8 / 0.5) = 4 gaps of 0.45 → 3 interior points each.
        int longGaps = (int)MathF.Ceiling(CarSize.Length / MaxOutlineSpacing);
        int shortGaps = (int)MathF.Ceiling(CarSize.Width / MaxOutlineSpacing);
        var xs = new List<float>();
        var ys = new List<float>();
        for (int i = 0; i <= longGaps; i++)
        {
            float x = -CarSize.HalfLength + i * (CarSize.Length / longGaps);
            xs.Add(x); ys.Add(-CarSize.HalfWidth);
            xs.Add(x); ys.Add(CarSize.HalfWidth);
        }
        for (int i = 1; i < shortGaps; i++)
        {
            float y = -CarSize.HalfWidth + i * (CarSize.Width / shortGaps);
            xs.Add(-CarSize.HalfLength); ys.Add(y);
            xs.Add(CarSize.HalfLength); ys.Add(y);
        }
        OutlineX = xs.ToArray();
        OutlineY = ys.ToArray();
    }

    /// <summary>Outline sample points in car space (x forward, y to the right), for tests and drawing.</summary>
    public static ReadOnlySpan<float> OutlinePointsX => OutlineX;
    public static ReadOnlySpan<float> OutlinePointsY => OutlineY;

    private enum Hit : byte { None, Wall, Danger }

    private readonly int _width, _height;
    private readonly byte[] _cells;
    private readonly float[] _clearance;
    private readonly WallMode _walls;
    private readonly float _maxSpeed, _grassTop, _grassGrip;
    private readonly float _accelPerTick, _brakePerTick, _roadDragPerTick, _grassDragPerTick, _overspeedPerTick;
    private readonly float _fullSteerSpeed, _turnUnitsPerTick;

    public CarPhysics(Grid grid, Clearance clearance, RacingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(clearance);
        ArgumentNullException.ThrowIfNull(settings);
        if (clearance.Width != grid.Width || clearance.Height != grid.Height)
            throw new ArgumentException("Clearance does not belong to this grid.", nameof(clearance));
        settings = settings.Clamped();
        _width = grid.Width;
        _height = grid.Height;
        _cells = grid.Cells;
        _clearance = clearance.Values;
        _walls = settings.Walls;
        const float dt = RacingSettings.Dt;
        _maxSpeed = settings.MaxSpeed;
        _grassTop = settings.MaxSpeed * settings.GrassSpeedFraction;
        _grassGrip = settings.GrassGrip;
        _accelPerTick = settings.Acceleration * dt;
        _brakePerTick = RacingSettings.Brake * dt;
        _roadDragPerTick = settings.Acceleration * RacingSettings.RoadDragFraction * dt;
        _grassDragPerTick = settings.Acceleration * RacingSettings.GrassDragFraction * dt;
        _overspeedPerTick = RacingSettings.OverspeedDecel * dt;
        _fullSteerSpeed = settings.MaxSpeed * RacingSettings.FullSteerSpeedFraction;
        _turnUnitsPerTick = settings.TurnRateDeg * (FastMath.AngleUnitsPerTurn / 360f) * dt;
    }

    /// <summary>Highest turn rate (radians/s) at full steer and at least <see cref="FullSteerSpeed"/>.</summary>
    public float MaxTurnRate => _turnUnitsPerTick * FastMath.RadiansPerUnit * RacingSettings.TicksPerSecond;

    /// <summary>Forward speed from which full steer gives the full turn rate, cells/s.</summary>
    public float FullSteerSpeed => _fullSteerSpeed;

    /// <summary>
    /// Advances the car by one tick with brain outputs <paramref name="throttle"/> and <paramref name="steer"/>
    /// (each clamped to [−1, 1]; NaN counts as 0). Throttle &gt; 0 drives forward (braking first when rolling
    /// backwards); &lt; 0 brakes, then reverses up to the reverse cap. Steer &gt; 0 turns towards +y.
    /// </summary>
    public TickResult Tick(ref CarState car, float throttle, float steer)
    {
        throttle = Sanitize(throttle);
        steer = Sanitize(steer);
        bool onGrass = CellAt(car.X, car.Y) == CellType.Grass;

        float c = FastMath.CosUnits(car.Heading), s = FastMath.SinUnits(car.Heading);
        float vF = car.Vx * c + car.Vy * s;  // along the heading
        float vL = car.Vy * c - car.Vx * s;  // to the car's right
        float vF0 = vF;                      // forward speed before this tick's input (for the top-speed clamp)

        // 1. Accelerate along the heading (brake first when throttle opposes the motion).
        if (throttle > 0f)
            vF = vF < 0f ? MathF.Min(vF + _brakePerTick * throttle, 0f) : vF + _accelPerTick * throttle;
        else if (throttle < 0f)
            vF = vF > 0f ? MathF.Max(vF + _brakePerTick * throttle, 0f) : vF + _accelPerTick * throttle;

        // 2. Steer: the rate grows linearly with forward speed up to the full-steer speed (none while stopped;
        //    reversed when rolling backwards, like a real car). A rotation that would push the outline into a
        //    wall is refused; into danger (or a wall in kill mode) it destroys the car.
        float steerScale = vF / _fullSteerSpeed;
        steerScale = steerScale > 1f ? 1f : steerScale < -1f ? -1f : steerScale;
        int turn = (int)MathF.Round(steer * steerScale * _turnUnitsPerTick);
        if (turn != 0)
        {
            int heading = (car.Heading + turn) & FastMath.AngleUnitsMask;
            Hit hit = Probe(car.X, car.Y, FastMath.CosUnits(heading), FastMath.SinUnits(heading));
            if (hit == Hit.Danger || (hit == Hit.Wall && _walls == WallMode.Kill))
                return Crash(ref car, onGrass);
            if (hit == Hit.None) car.Heading = heading;
            else turn = 0;
        }
        car.YawRate = turn * FastMath.RadiansPerUnit * RacingSettings.TicksPerSecond;

        // Re-express the velocity in the (possibly rotated) car frame.
        float wx = vF * c - vL * s, wy = vF * s + vL * c;
        c = FastMath.CosUnits(car.Heading);
        s = FastMath.SinUnits(car.Heading);
        vF = wx * c + wy * s;
        vL = wy * c - wx * s;

        // 3. Grip removes lateral velocity (all of it on road; drifting on grass).
        vL *= 1f - (onGrass ? _grassGrip : RacingSettings.RoadGrip);

        // 4. Surface drag: a constant deceleration towards standstill.
        float drag = onGrass ? _grassDragPerTick : _roadDragPerTick;
        vF = vF > 0f ? MathF.Max(vF - drag, 0f) : MathF.Min(vF + drag, 0f);

        // 5. Clamp to the surface's top speed (and the reverse cap). This tick's acceleration never takes the
        //    car past the cap (a hard clamp for any acceleration setting). Speed that was already over the cap
        //    when the tick began (e.g. running onto grass) is bled at the overspeed deceleration instead, so the
        //    car slows over a few ticks rather than stopping dead; this tick's acceleration cannot slow the bleed.
        float top = onGrass ? _grassTop : _maxSpeed;
        if (vF > top) vF = vF0 > top ? MathF.Max(MathF.Min(vF, vF0) - _overspeedPerTick, top) : top;
        float reverseTop = MathF.Min(RacingSettings.ReverseCap, top);
        if (vF < -reverseTop) vF = vF0 < -reverseTop ? MathF.Min(MathF.Max(vF, vF0) + _overspeedPerTick, -reverseTop) : -reverseTop;

        car.Vx = vF * c - vL * s;
        car.Vy = vF * s + vL * c;

        return Move(ref car, c, s, onGrass);
    }

    /// <summary>Moves the car through its velocity in sub-steps of at most <see cref="MaxSubStep"/> cells.</summary>
    private TickResult Move(ref CarState car, float c, float s, bool onGrass)
    {
        float speed = MathF.Sqrt(car.Vx * car.Vx + car.Vy * car.Vy); // IEEE sqrt: correctly rounded, deterministic
        int n = (int)MathF.Ceiling(speed * RacingSettings.Dt / MaxSubStep);
        float stepDt = RacingSettings.Dt / n;
        bool contact = false;

        for (int k = 0; k < n; k++)
        {
            float dx = car.Vx * stepDt, dy = car.Vy * stepDt;
            if (dx == 0f && dy == 0f) break;
            float px = car.X, py = car.Y;

            Hit hit = Probe(px + dx, py + dy, c, s);
            if (hit == Hit.Danger) return Crash(ref car, onGrass, contact);
            if (hit == Hit.None)
            {
                car.X = px + dx;
                car.Y = py + dy;
            }
            else
            {
                if (_walls == WallMode.Kill) return Crash(ref car, onGrass, true);

                // Which face did we hit? Try each axis of the move alone (a blocked part counts, whatever blocks it).
                bool blockX = Probe(px + dx, py, c, s) != Hit.None;
                bool blockY = Probe(px, py + dy, c, s) != Hit.None;
                if (!blockX && !blockY)
                {
                    // Only the diagonal move hits: a convex wall corner. Treat the larger move component as the
                    // one going into the wall and slide along the other (that single-axis move is free).
                    if (MathF.Abs(dx) >= MathF.Abs(dy)) blockX = true;
                    else blockY = true;
                }
                if (blockX) car.Vx = WallNormal(car.Vx);
                else if (!contact) car.Vx *= RacingSettings.WallKeepTangential;
                if (blockY) car.Vy = WallNormal(car.Vy);
                else if (!contact) car.Vy *= RacingSettings.WallKeepTangential;
                contact = true;

                // Slide along the free axis (that part of the move was probed free above).
                if (!blockX) car.X = px + dx;
                else if (!blockY) car.Y = py + dy;
            }

            if ((car.X != px || car.Y != py) && CellAt(car.X, car.Y) == CellType.Finish)
            {
                float t = FinishEntry(px, py, car.X, car.Y);
                return new TickResult(TickOutcome.Finished, (k + t) / n, contact, onGrass);
            }
        }
        return new TickResult(TickOutcome.Moving, 0f, contact, onGrass);
    }

    /// <summary>Normal velocity after a wall contact: gone (block) or reversed at the restitution (bounce).</summary>
    private float WallNormal(float v) => _walls == WallMode.Bounce ? -v * RacingSettings.BounceRestitution : 0f;

    private static TickResult Crash(ref CarState car, bool onGrass, bool hitWall = false)
    {
        car.Vx = 0f;
        car.Vy = 0f;
        car.YawRate = 0f;
        return new TickResult(TickOutcome.Crashed, 0f, hitWall, onGrass);
    }

    /// <summary>
    /// Fraction t ∈ [0, 1] of the move p → q at which the centre first enters a Finish cell (q is in one,
    /// p is not). A move is at most half a cell, so it crosses at most one x and one y cell boundary.
    /// </summary>
    private float FinishEntry(float px, float py, float qx, float qy)
    {
        int ax = (int)MathF.Floor(px), ay = (int)MathF.Floor(py);
        int bx = (int)MathF.Floor(qx), by = (int)MathF.Floor(qy);
        float tx = ax != bx ? (Math.Max(ax, bx) - px) / (qx - px) : float.NaN;
        float ty = ay != by ? (Math.Max(ay, by) - py) / (qy - py) : float.NaN;
        float t;
        if (float.IsNaN(tx)) t = float.IsNaN(ty) ? 0f : ty;
        else if (float.IsNaN(ty)) t = tx;
        else if (tx == ty) t = tx;
        else
        {
            // Two crossings: the cell between them may already be Finish.
            bool xFirst = tx < ty;
            CellType middle = xFirst ? CellAtIndex(bx, ay) : CellAtIndex(ax, by);
            t = middle == CellType.Finish ? MathF.Min(tx, ty) : MathF.Max(tx, ty);
        }
        return t < 0f ? 0f : t > 1f ? 1f : t;
    }

    /// <summary>Does the car outline at (x, y) with heading (c, s) touch a wall/outside or danger? Danger wins.</summary>
    private Hit Probe(float x, float y, float c, float s)
    {
        int cx = (int)MathF.Floor(x), cy = (int)MathF.Floor(y);
        if ((uint)cx < (uint)_width && (uint)cy < (uint)_height && _clearance[cy * _width + cx] > BroadphaseClearance)
            return Hit.None;
        return OutlineHit(_cells, _width, _height, x, y, c, s);
    }

    /// <summary>The outline test of <see cref="Probe"/> without the broadphase shortcut.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Hit OutlineHit(byte[] cells, int width, int height, float x, float y, float c, float s)
    {
        Hit hit = Hit.None;
        float[] ox = OutlineX, oy = OutlineY;
        for (int i = 0; i < ox.Length; i++)
        {
            float wx = x + ox[i] * c - oy[i] * s;
            float wy = y + ox[i] * s + oy[i] * c;
            int gx = (int)MathF.Floor(wx), gy = (int)MathF.Floor(wy);
            var type = (uint)gx < (uint)width && (uint)gy < (uint)height ? (CellType)cells[gy * width + gx] : CellType.Wall;
            if (type == CellType.Danger) return Hit.Danger;
            if (type == CellType.Wall) hit = Hit.Wall;
        }
        return hit;
    }

    /// <summary>Does the outline of <paramref name="car"/> touch a wall, the outside or danger? (Tests and the editor.)</summary>
    public bool Overlaps(in CarState car) =>
        Probe(car.X, car.Y, FastMath.CosUnits(car.Heading), FastMath.SinUnits(car.Heading)) != Hit.None;

    /// <summary>
    /// Does a car outline at (<paramref name="x"/>, <paramref name="y"/>) with heading <paramref name="heading"/>
    /// (angle units) touch a wall, the outside or danger on <paramref name="grid"/>? Such a car can never move
    /// (every move would keep it overlapping), so a start pose like this is rejected (<see cref="Solvability"/>).
    /// </summary>
    public static bool Overlaps(Grid grid, float x, float y, int heading)
    {
        ArgumentNullException.ThrowIfNull(grid);
        return OutlineHit(grid.Cells, grid.Width, grid.Height, x, y, FastMath.CosUnits(heading), FastMath.SinUnits(heading)) != Hit.None;
    }

    private CellType CellAt(float x, float y) => CellAtIndex((int)MathF.Floor(x), (int)MathF.Floor(y));

    private CellType CellAtIndex(int x, int y) =>
        (uint)x < (uint)_width && (uint)y < (uint)_height ? (CellType)_cells[y * _width + x] : CellType.Wall;

    private static float Sanitize(float v) => v > 1f ? 1f : v < -1f ? -1f : float.IsNaN(v) ? 0f : v;
}
