using Nitrogenesis.Sim.Racing;

namespace Nitrogenesis.Sim.Map;

/// <summary>
/// Cost-to-finish for every cell along the real drivable path (PLAN §3.2), plus the bilinear progress
/// lookup used by fitness. Unreachable cells (blocking, too narrow, or cut off) hold +∞.
/// </summary>
public sealed class DistanceField
{
    /// <summary>Value of cells from which the goal cannot be reached.</summary>
    public const float Unreachable = float.PositiveInfinity;

    public int Width { get; }
    public int Height { get; }

    /// <summary>Distance per cell (row-major), in road-cell units: one road cell costs 1, one grass cell grassCost.</summary>
    public float[] Values { get; }

    private DistanceField(int width, int height, float[] values)
    {
        Width = width;
        Height = height;
        Values = values;
    }

    public float this[int x, int y] => Values[y * Width + x];

    /// <summary>True when cell (x, y) is inside the map and the goal can be reached from it.</summary>
    public bool IsReachable(int x, int y) =>
        (uint)x < (uint)Width && (uint)y < (uint)Height && Values[y * Width + x] != Unreachable;

    /// <summary>
    /// The racing field: Dijkstra from every passable Finish cell over cells whose clearance is at least
    /// <paramref name="halfWidth"/>.
    /// </summary>
    /// <param name="grassCost">roadTopSpeed / grassTopSpeed from the physics settings; +∞ = road only.</param>
    public static DistanceField ForRacing(Grid grid, Clearance clearance, float halfWidth, double grassCost) =>
        ForRacing(grid, new RacingGraph(grid, clearance, halfWidth, grassCost));

    /// <summary>A racing field over a prepared graph (e.g. one with its own cell costs): Dijkstra from every passable Finish cell.</summary>
    public static DistanceField ForRacing(Grid grid, RacingGraph graph)
    {
        var sources = new List<int>();
        for (int i = 0; i < grid.CellCount; i++)
            if ((CellType)grid.Cells[i] == CellType.Finish && graph.IsPassable(i)) sources.Add(i);
        return FromGraph(grid.Width, grid.Height, graph, sources);
    }

    /// <summary>Convenience overload that computes the clearance too, with the racing car's half-width.</summary>
    public static DistanceField ForRacing(Grid grid, double grassCost) =>
        ForRacing(grid, Clearance.Compute(grid), CarSize.HalfWidth, grassCost);

    /// <summary>A field over any cell graph (the pluggable form, e.g. the platformer's jump graph).</summary>
    public static DistanceField FromGraph<TGraph>(int width, int height, TGraph graph, IEnumerable<int> sources)
        where TGraph : struct, IReverseGraph
    {
        var dist = new double[width * height];
        Array.Fill(dist, double.PositiveInfinity);
        foreach (int s in sources) dist[s] = 0;
        Dijkstra.Run(graph, dist);

        var values = new float[dist.Length];
        for (int i = 0; i < dist.Length; i++) values[i] = (float)dist[i];
        return new DistanceField(width, height, values);
    }

    /// <summary>
    /// Bilinear interpolation between the 4 cell centres around world position (x, y) (PLAN §3.2).
    /// Unreachable corners are left out and the remaining weights renormalised, so a car hugging a wall
    /// (whose centre lies between a passable and a too-narrow cell) still gets a finite, smooth value.
    /// Returns <see cref="Unreachable"/> only when all four corners are. Allocation-free; tick-path safe.
    /// </summary>
    public float Sample(float x, float y)
    {
        float fx = x - 0.5f, fy = y - 0.5f;
        float flx = MathF.Floor(fx), fly = MathF.Floor(fy);
        int x0 = (int)flx, y0 = (int)fly;
        float tx = fx - flx, ty = fy - fly;

        float sum = 0f, weight = 0f, min = Unreachable;
        Accumulate(x0, y0, (1f - tx) * (1f - ty), ref sum, ref weight, ref min);
        Accumulate(x0 + 1, y0, tx * (1f - ty), ref sum, ref weight, ref min);
        Accumulate(x0, y0 + 1, (1f - tx) * ty, ref sum, ref weight, ref min);
        Accumulate(x0 + 1, y0 + 1, tx * ty, ref sum, ref weight, ref min);

        // weight is 0 when every reachable corner has zero weight (the point lies exactly on an unreachable
        // cell's centre lines): then use the nearest-to-finish reachable corner, or Unreachable if none.
        return weight > 0f ? sum / weight : min;
    }

    private void Accumulate(int cx, int cy, float w, ref float sum, ref float weight, ref float min)
    {
        if ((uint)cx >= (uint)Width || (uint)cy >= (uint)Height) return;
        float v = Values[cy * Width + cx];
        if (v == Unreachable) return;
        sum += v * w;
        weight += w;
        if (v < min) min = v;
    }
}
