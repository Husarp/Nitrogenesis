using Godot;

namespace Nitrogenesis.Editor;

/// <summary>
/// The Test button's route in map space (1 unit = 1 cell): the reference driver's path as a polyline with a dark
/// outline, a dot where it started and a marker where it ended (green = finish, red = it stopped there).
/// </summary>
public partial class TestRouteLayer : Node2D
{
    private static readonly Color Line = new(0.3f, 1f, 0.55f, 0.95f);
    private static readonly Color Shadow = new(0f, 0f, 0f, 0.55f);
    private static readonly Color Stopped = new(1f, 0.35f, 0.3f, 1f);
    private const int MaxPoints = 4000;

    private Vector2[] _route = [];
    private bool _finished;

    public TestRouteLayer() => Name = "TestRouteLayer";

    /// <summary>Shows a route (long ones are thinned out to keep the line cheap to draw; the last point is always kept).</summary>
    public void SetRoute(System.Collections.Generic.IReadOnlyList<(float X, float Y)> path, bool finished)
    {
        int step = System.Math.Max(1, path.Count / MaxPoints);
        var points = new System.Collections.Generic.List<Vector2>();
        for (int i = 0; i < path.Count; i += step) points.Add(new Vector2(path[i].X, path[i].Y));
        if (path.Count > 0 && (path.Count - 1) % step != 0) points.Add(new Vector2(path[^1].X, path[^1].Y));
        _route = points.ToArray();
        _finished = finished;
        QueueRedraw();
    }

    public void Clear()
    {
        if (_route.Length == 0) return;
        _route = [];
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_route.Length < 2) return;
        DrawPolyline(_route, Shadow, 1.3f, true);
        DrawPolyline(_route, Line, 0.7f, true);
        DrawCircle(_route[0], 0.9f, Line);
        var end = _route[^1];
        if (_finished) DrawCircle(end, 1.2f, Line);
        else
        {
            DrawCircle(end, 1.8f, Shadow);
            DrawCircle(end, 1.4f, Stopped);
            DrawLine(end + new Vector2(-1f, -1f), end + new Vector2(1f, 1f), Shadow, 0.5f, true);
            DrawLine(end + new Vector2(-1f, 1f), end + new Vector2(1f, -1f), Shadow, 0.5f, true);
        }
    }
}
