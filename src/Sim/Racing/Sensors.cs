using System.Runtime.CompilerServices;
using Nitrogenesis.Sim.Brain;
using Nitrogenesis.Sim.Map;

namespace Nitrogenesis.Sim.Racing;

/// <summary>
/// The car's inputs (PLAN §3.4), each normalised to about [−1, 1], written in this order:
/// <list type="number">
/// <item>Rays, from the leftmost (most negative angle) to the rightmost, two values each: distance to the first
/// non-road cell / range, then distance to the first wall-or-danger cell / range (1 = nothing within range).
/// Finish counts as road; outside the map counts as wall.</item>
/// <item>Body: forward speed / max speed, lateral slip / max speed, yaw rate / max turn rate, on-grass (0 or 1).</item>
/// <item>Memory (when on): previous throttle, previous steer, elapsed ticks / time limit, distance driven / track length.</item>
/// <item>Direction hint (when on): angle from the heading to the distance field's downhill direction / π.</item>
/// </list>
/// </summary>
/// <remarks>Immutable and allocation-free; one instance serves all workers. Rays use a single DDA grid walk each.</remarks>
public sealed class Sensors
{
    private readonly int _width, _height;
    private readonly byte[] _cells;
    private readonly DistanceField _field;
    private readonly int[] _rayOffsets;
    private readonly float _range, _inverseRange;
    private readonly float _inverseMaxSpeed, _inverseTurnRate;
    private readonly bool _memory, _hint;

    public Sensors(RacingTrack track, CarPhysics physics)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(physics);
        RacingSettings settings = track.Settings;
        _width = track.Grid.Width;
        _height = track.Grid.Height;
        _cells = track.Grid.Cells;
        _field = track.Field;
        _range = settings.RayRange;
        _inverseRange = 1f / settings.RayRange;
        _inverseMaxSpeed = 1f / settings.MaxSpeed;
        _inverseTurnRate = 1f / physics.MaxTurnRate;
        _memory = settings.MemoryInputs;
        _hint = settings.DirectionHint;
        _rayOffsets = RayOffsets(settings.RayCount, settings.RaySpreadDeg);
        InputCount = settings.InputCount;
    }

    public int InputCount { get; }

    /// <summary>Number of rays.</summary>
    public int RayCount => _rayOffsets.Length;

    /// <summary>Ray range, cells.</summary>
    public float Range => _range;

    /// <summary>Angle of ray <paramref name="ray"/> for a car with heading <paramref name="heading"/>, both in angle units.</summary>
    public int RayAngle(int heading, int ray) => (heading + _rayOffsets[ray]) & FastMath.AngleUnitsMask;

    /// <summary>
    /// The rays of <see cref="Write"/> in cells (for drawing them): for each ray the distance to the first non-road
    /// cell and to the first wall-or-danger cell, capped at <see cref="Range"/>. Write's ray inputs are these
    /// values / range. Spans must hold <see cref="RayCount"/> values. Does not allocate.
    /// </summary>
    public void CastRays(float x, float y, int heading, Span<float> nonRoad, Span<float> wall)
    {
        for (int r = 0; r < _rayOffsets.Length; r++)
        {
            int angle = heading + _rayOffsets[r];
            CastRay(x, y, FastMath.CosUnits(angle), FastMath.SinUnits(angle), out nonRoad[r], out wall[r]);
        }
    }

    /// <summary>
    /// Ray angles relative to the heading, in angle units: evenly from −spread/2 to +spread/2 inclusive; at a
    /// full 360° the last ray would repeat the first, so the turn is divided by the count instead.
    /// </summary>
    public static int[] RayOffsets(int count, float spreadDeg)
    {
        var offsets = new int[count];
        bool fullCircle = spreadDeg >= 360f;
        float step = fullCircle ? 360f / count : spreadDeg / (count - 1);
        float first = fullCircle ? -180f : -spreadDeg / 2;
        for (int i = 0; i < count; i++)
            offsets[i] = (int)MathF.Round((first + i * step) * (FastMath.AngleUnitsPerTurn / 360f));
        return offsets;
    }

    /// <summary>Writes all inputs for a car into <paramref name="dest"/> (length <see cref="InputCount"/>).</summary>
    /// <param name="car">The car.</param>
    /// <param name="prevThrottle">Throttle applied last tick (after clamping).</param>
    /// <param name="prevSteer">Steer applied last tick (after clamping).</param>
    /// <param name="elapsedFraction">Ticks run / time-limit ticks.</param>
    /// <param name="drivenFraction">Distance driven / track length (start distance).</param>
    public void Write(in CarState car, float prevThrottle, float prevSteer, float elapsedFraction, float drivenFraction, Span<float> dest)
    {
        int o = 0;
        for (int r = 0; r < _rayOffsets.Length; r++)
        {
            int angle = car.Heading + _rayOffsets[r];
            CastRay(car.X, car.Y, FastMath.CosUnits(angle), FastMath.SinUnits(angle), out float nonRoad, out float wall);
            dest[o++] = nonRoad * _inverseRange;
            dest[o++] = wall * _inverseRange;
        }

        float c = FastMath.CosUnits(car.Heading), s = FastMath.SinUnits(car.Heading);
        dest[o++] = (car.Vx * c + car.Vy * s) * _inverseMaxSpeed;
        dest[o++] = (car.Vy * c - car.Vx * s) * _inverseMaxSpeed;
        dest[o++] = car.YawRate * _inverseTurnRate;
        dest[o++] = CellAt((int)MathF.Floor(car.X), (int)MathF.Floor(car.Y)) == CellType.Grass ? 1f : 0f;

        if (_memory)
        {
            dest[o++] = prevThrottle;
            dest[o++] = prevSteer;
            dest[o++] = elapsedFraction;
            dest[o++] = drivenFraction;
        }
        if (_hint) dest[o++] = DirectionHint(_field, car.X, car.Y, car.Heading);
    }

    /// <summary>
    /// Walks the grid along a ray from (x, y) in unit direction (dx, dy) (Amanatides–Woo DDA). Returns the
    /// distance at which the ray enters the first non-road cell and the first wall/danger cell, each capped
    /// at the range. A start cell that is itself non-road gives 0.
    /// </summary>
    public void CastRay(float x, float y, float dx, float dy, out float nonRoad, out float wall)
    {
        float range = _range;
        nonRoad = range;
        wall = range;
        int ix = (int)MathF.Floor(x), iy = (int)MathF.Floor(y);
        CellType type = CellAt(ix, iy);
        if (IsBlocking(type))
        {
            nonRoad = 0f;
            wall = 0f;
            return;
        }

        int stepX = dx > 0f ? 1 : -1, stepY = dy > 0f ? 1 : -1;
        float deltaX = dx != 0f ? MathF.Abs(1f / dx) : float.PositiveInfinity;
        float deltaY = dy != 0f ? MathF.Abs(1f / dy) : float.PositiveInfinity;
        float nextX = dx > 0f ? (ix + 1 - x) * deltaX : dx < 0f ? (x - ix) * deltaX : float.PositiveInfinity;
        float nextY = dy > 0f ? (iy + 1 - y) * deltaY : dy < 0f ? (y - iy) * deltaY : float.PositiveInfinity;
        float t;

        // Two tight loops instead of one general one: first over road (until the first non-road cell), then
        // over grass (until the first wall or danger). Each cell is still visited in DDA order.
        if (IsRoad(type))
        {
            do
            {
                t = Step(ref nextX, ref nextY, ref ix, ref iy, deltaX, deltaY, stepX, stepY);
                if (t >= range) return;
                type = CellAt(ix, iy);
            } while (IsRoad(type));
            nonRoad = t;
            if (IsBlocking(type))
            {
                wall = t;
                return;
            }
        }
        else
        {
            nonRoad = 0f; // the ray starts on grass
        }

        while (true)
        {
            t = Step(ref nextX, ref nextY, ref ix, ref iy, deltaX, deltaY, stepX, stepY);
            if (t >= range) return;
            if (IsBlocking(CellAt(ix, iy)))
            {
                wall = t;
                return;
            }
        }
    }

    /// <summary>One DDA step (Amanatides–Woo) to the nearer cell boundary; returns the ray distance at it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Step(ref float nextX, ref float nextY, ref int ix, ref int iy, float deltaX, float deltaY, int stepX, int stepY)
    {
        float t;
        if (nextX < nextY)
        {
            t = nextX;
            ix += stepX;
            nextX += deltaX;
        }
        else
        {
            t = nextY;
            iy += stepY;
            nextY += deltaY;
        }
        return t;
    }

    /// <summary>Road for the rays: Road and Finish.</summary>
    private static bool IsRoad(CellType type) => type is CellType.Road or CellType.Finish;

    private static bool IsBlocking(CellType type) => type is CellType.Wall or CellType.Danger;

    /// <summary>
    /// Signed angle from the heading to the downhill direction of the distance field at (x, y), divided by π
    /// (so in [−1, 1]). The gradient is a central difference of the bilinear sample half a cell either side.
    /// 0 when the slope is flat or not finite (e.g. next to unreachable cells).
    /// </summary>
    public static float DirectionHint(DistanceField field, float x, float y, int heading)
    {
        if (!DownhillDirection(field, x, y, out float gx, out float gy)) return 0f;
        float diff = FastMath.AngleDiff(heading * FastMath.RadiansPerUnit, FastMath.Atan2(gy, gx));
        return diff * (1f / FastMath.Pi);
    }

    /// <summary>The (unnormalised) downhill vector of the field at (x, y); false when flat or not finite.</summary>
    public static bool DownhillDirection(DistanceField field, float x, float y, out float gx, out float gy)
    {
        const float h = 0.5f;
        gx = field.Sample(x - h, y) - field.Sample(x + h, y);
        gy = field.Sample(x, y - h) - field.Sample(x, y + h);
        return float.IsFinite(gx) && float.IsFinite(gy) && (gx != 0f || gy != 0f);
    }

    private CellType CellAt(int x, int y) =>
        (uint)x < (uint)_width && (uint)y < (uint)_height ? (CellType)_cells[y * _width + x] : CellType.Wall;
}
