using System;
using Godot;
using Nitrogenesis.Sim.Brain;
using Nitrogenesis.Sim.Racing;

namespace Nitrogenesis.View;

/// <summary>
/// Draws one car's route trail and sensor rays (PLAN §6.2) under the cars. The owner fills the pre-allocated
/// arrays (<see cref="TrailX"/>, <see cref="TrailY"/>; the rays through <see cref="SetRays"/>) and calls
/// <see cref="Show"/> every frame. Nothing allocates: the trail is drawn as one polyline from a pre-allocated array
/// of the next power-of-two size, its unused tail filled with the last point (zero-length segments draw nothing).
/// </summary>
/// <remarks>
/// Rays come from <see cref="Sensors.CastRays"/>, the very code that feeds the brain: each ray is a faint line from
/// the car to the first wall or danger cell (to the range when there is none), with a yellow tick where it first
/// leaves the road (grass) and a red one where it hits.
/// </remarks>
public partial class RouteOverlay : Node2D
{
    public const int MaxRays = RacingSettings.MaxRayCount;
    private static readonly Color RayColor = new(1f, 1f, 1f, 0.6f);
    private static readonly Color Shadow = new(0f, 0f, 0f, 0.45f);
    private static readonly Color GrassMark = new(1f, 0.85f, 0.25f, 0.95f);
    private static readonly Color HitMark = new(1f, 0.3f, 0.25f, 0.95f);

    private readonly float[] _rayDx = new float[MaxRays], _rayDy = new float[MaxRays];
    private readonly float[] _nonRoad = new float[MaxRays], _wall = new float[MaxRays];
    private int _trailCount, _rayCount;
    private float _rayRange, _carX, _carY;
    private Color _trailColor;
    private float _pixel = 1f / ViewCamera.PixelsPerCell;
    /// <summary>Polyline buffers of 16, 32, … points, up to the trail capacity.</summary>
    private readonly Vector2[][] _lines;

    /// <param name="trailCapacity">Most trail points (the head point included).</param>
    public RouteOverlay(int trailCapacity)
    {
        TrailX = new float[trailCapacity];
        TrailY = new float[trailCapacity];
        int sizes = 1;
        while (16 << (sizes - 1) < trailCapacity) sizes++;
        _lines = new Vector2[sizes][];
        for (int i = 0; i < sizes; i++) _lines[i] = new Vector2[16 << i];
    }

    public RouteOverlay() : this(RouteTrails.MaxSamplesPerCar + 1) { }

    /// <summary>Trail points, filled by the owner.</summary>
    public float[] TrailX { get; }
    public float[] TrailY { get; }

    /// <summary>Casts the rays of a car at (x, y) with <paramref name="heading"/> (angle units) with its sensors.</summary>
    public void SetRays(Sensors sensors, float x, float y, int heading)
    {
        _rayCount = Math.Min(sensors.RayCount, MaxRays);
        _rayRange = sensors.Range;
        sensors.CastRays(x, y, heading, _nonRoad.AsSpan(0, _rayCount), _wall.AsSpan(0, _rayCount));
        for (int r = 0; r < _rayCount; r++)
        {
            int a = sensors.RayAngle(heading, r);
            _rayDx[r] = FastMath.CosUnits(a);
            _rayDy[r] = FastMath.SinUnits(a);
        }
        _carX = x;
        _carY = y;
    }

    public void ClearRays() => _rayCount = 0;

    /// <summary>
    /// Shows <paramref name="trailCount"/> trail points in <paramref name="trailColor"/> (0 = no trail) and the rays
    /// last set, at the camera's current zoom (<paramref name="worldPerPixel"/> cells per screen pixel).
    /// </summary>
    public void Show(int trailCount, Color trailColor, float worldPerPixel)
    {
        _trailCount = Math.Min(trailCount, TrailX.Length);
        _trailColor = trailColor;
        _pixel = worldPerPixel;
        QueueRedraw();
    }

    public override void _Draw()
    {
        // Route: 2 px wide, with a dark 4 px edge so it reads on road, grass and the finish alike.
        if (_trailCount >= 2)
        {
            int size = 0;
            while (_lines[size].Length < _trailCount) size++;
            Vector2[] points = _lines[size];
            for (int i = 0; i < _trailCount; i++) points[i] = new Vector2(TrailX[i], TrailY[i]);
            Vector2 last = points[_trailCount - 1];
            for (int i = _trailCount; i < points.Length; i++) points[i] = last;
            DrawPolyline(points, Shadow, 4f * _pixel);
            DrawPolyline(points, _trailColor, 2f * _pixel);
        }

        var car = new Vector2(_carX, _carY);
        var mark = new Vector2(4f * _pixel, 4f * _pixel);
        for (int r = 0; r < _rayCount; r++)
        {
            var dir = new Vector2(_rayDx[r], _rayDy[r]);
            float wall = _wall[r], nonRoad = _nonRoad[r];
            DrawLine(car, car + dir * wall, RayColor, -1f);
            if (nonRoad < wall) DrawRect(new Rect2(car + dir * nonRoad - mark / 2, mark), GrassMark);
            if (wall < _rayRange) DrawRect(new Rect2(car + dir * wall - mark / 2, mark), HitMark);
        }
    }
}
