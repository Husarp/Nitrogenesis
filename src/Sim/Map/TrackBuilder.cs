namespace Nitrogenesis.Sim.Map;

/// <summary>
/// Paints shapes into a <see cref="Grid"/> from code: used to author bundled tracks and test maps, and later
/// by the generator. Shapes are given in world units (cells); a cell is painted when its centre
/// (x + 0.5, y + 0.5) lies inside the shape. Shapes are clipped to the grid.
/// </summary>
/// <remarks>
/// Only exact comparisons of squared distances are used (no square roots or trig), so the same calls
/// produce the same cells on every platform. Every paint method takes an optional <c>onlyOver</c>: when set,
/// only cells currently of that type are changed (e.g. paint a grass verge over Wall without touching road).
/// </remarks>
public sealed class TrackBuilder(Grid grid)
{
    public Grid Grid { get; } = grid ?? throw new ArgumentNullException(nameof(grid));

    /// <summary>Sets every cell (or every cell of type <paramref name="onlyOver"/>) to <paramref name="type"/>.</summary>
    public TrackBuilder Fill(CellType type, CellType? onlyOver = null)
    {
        for (int i = 0; i < Grid.CellCount; i++) Paint(i, type, onlyOver);
        return this;
    }

    /// <summary>Paints the cells x0..x1 × y0..y1 (inclusive, any corner order).</summary>
    public TrackBuilder Rect(int x0, int y0, int x1, int y1, CellType type, CellType? onlyOver = null)
    {
        int minX = Math.Max(Math.Min(x0, x1), 0), maxX = Math.Min(Math.Max(x0, x1), Grid.Width - 1);
        int minY = Math.Max(Math.Min(y0, y1), 0), maxY = Math.Min(Math.Max(y0, y1), Grid.Height - 1);
        for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
                Paint(Grid.Index(x, y), type, onlyOver);
        return this;
    }

    /// <summary>Paints a disc of <paramref name="radius"/> around (cx, cy).</summary>
    public TrackBuilder Circle(double cx, double cy, double radius, CellType type, CellType? onlyOver = null) =>
        Line(cx, cy, cx, cy, radius, type, onlyOver);

    /// <summary>
    /// Paints a thick line: every cell whose centre is within <paramref name="radius"/> of the segment
    /// (a capsule, i.e. a round brush dragged from one end to the other).
    /// </summary>
    public TrackBuilder Line(double x0, double y0, double x1, double y1, double radius, CellType type, CellType? onlyOver = null)
    {
        if (!(radius >= 0)) throw new ArgumentOutOfRangeException(nameof(radius));
        int minX = Math.Max((int)Math.Floor(Math.Min(x0, x1) - radius), 0);
        int maxX = Math.Min((int)Math.Ceiling(Math.Max(x0, x1) + radius), Grid.Width - 1);
        int minY = Math.Max((int)Math.Floor(Math.Min(y0, y1) - radius), 0);
        int maxY = Math.Min((int)Math.Ceiling(Math.Max(y0, y1) + radius), Grid.Height - 1);
        double r2 = radius * radius;
        for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
                if (SegmentDistanceSquared(x + 0.5, y + 0.5, x0, y0, x1, y1) <= r2)
                    Paint(Grid.Index(x, y), type, onlyOver);
        return this;
    }

    /// <summary>Paints a thick line through consecutive points (round joins).</summary>
    public TrackBuilder Polyline(IReadOnlyList<(double X, double Y)> points, double radius, CellType type, CellType? onlyOver = null)
    {
        if (points.Count == 0) throw new ArgumentException("A polyline needs at least one point.", nameof(points));
        if (points.Count == 1) return Circle(points[0].X, points[0].Y, radius, type, onlyOver);
        for (int i = 1; i < points.Count; i++)
            Line(points[i - 1].X, points[i - 1].Y, points[i].X, points[i].Y, radius, type, onlyOver);
        return this;
    }

    private void Paint(int index, CellType type, CellType? onlyOver)
    {
        if (onlyOver is { } only && (CellType)Grid.Cells[index] != only) return;
        Grid.Cells[index] = (byte)type;
    }

    private static double SegmentDistanceSquared(double px, double py, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax, dy = by - ay;
        double len2 = dx * dx + dy * dy;
        double t = len2 > 0 ? ((px - ax) * dx + (py - ay) * dy) / len2 : 0;
        if (t < 0) t = 0;
        else if (t > 1) t = 1;
        double ex = px - (ax + t * dx), ey = py - (ay + t * dy);
        return ex * ex + ey * ey;
    }
}
