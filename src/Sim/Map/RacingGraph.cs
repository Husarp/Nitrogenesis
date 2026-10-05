using Nitrogenesis.Sim.Racing;

namespace Nitrogenesis.Sim.Map;

/// <summary>
/// The racing movement graph (PLAN §3.2): 8 neighbours between passable cells. An orthogonal step costs 1,
/// a diagonal step √2, each multiplied by the mean surface factor of its two cells (1 on road and finish,
/// <c>grassCost</c> on grass). A diagonal step is not allowed when both cells it squeezes between are
/// blocking. The graph is symmetric, so incoming edges equal outgoing ones.
/// </summary>
public readonly struct RacingGraph : IReverseGraph
{
    /// <summary>√2 as a constant (no runtime square root in the distance field).</summary>
    public const double Diagonal = 1.4142135623730951;

    private readonly int _width, _height;
    private readonly bool[] _passable;
    private readonly bool[] _blocking;
    private readonly double[] _factor;

    /// <param name="grid">The map.</param>
    /// <param name="clearance">Its clearance; a cell is passable when clearance ≥ <paramref name="halfWidth"/>.</param>
    /// <param name="halfWidth">Car half-width (<see cref="CarSize.HalfWidth"/> for racing).</param>
    /// <param name="grassCost">Cost factor of grass relative to road; +∞ forbids grass entirely.</param>
    public RacingGraph(Grid grid, Clearance clearance, float halfWidth, double grassCost)
        : this(grid, clearance, halfWidth, SurfaceFactors(grid, grassCost))
    {
    }

    /// <param name="grid">The map.</param>
    /// <param name="clearance">Its clearance; a cell is passable when clearance ≥ <paramref name="halfWidth"/>.</param>
    /// <param name="halfWidth">Car half-width.</param>
    /// <param name="cellFactor">Cost factor per cell (≥ 1, row-major), used instead of the surface factor; kept, not copied.</param>
    public RacingGraph(Grid grid, Clearance clearance, float halfWidth, double[] cellFactor)
    {
        ArgumentNullException.ThrowIfNull(cellFactor);
        if (cellFactor.Length != grid.CellCount) throw new ArgumentException("One factor per cell is needed.", nameof(cellFactor));
        _width = grid.Width;
        _height = grid.Height;
        int n = grid.CellCount;
        _passable = new bool[n];
        _blocking = new bool[n];
        _factor = cellFactor;
        for (int i = 0; i < n; i++)
        {
            _passable[i] = clearance.IsPassable(i, halfWidth);
            _blocking[i] = CellTypes.IsBlocking((CellType)grid.Cells[i]);
        }
    }

    /// <summary>Per-cell surface factor: <paramref name="grassCost"/> on grass, 1 elsewhere.</summary>
    public static double[] SurfaceFactors(Grid grid, double grassCost)
    {
        if (!(grassCost >= 1)) throw new ArgumentOutOfRangeException(nameof(grassCost), "Grass cost must be ≥ 1.");
        var factor = new double[grid.CellCount];
        for (int i = 0; i < factor.Length; i++) factor[i] = (CellType)grid.Cells[i] == CellType.Grass ? grassCost : 1.0;
        return factor;
    }

    public int MaxIncoming => 8;

    /// <summary>True when the car centre may be in this cell.</summary>
    public bool IsPassable(int index) => _passable[index];

    public int GetIncoming(int node, Span<int> from, Span<double> cost)
    {
        int w = _width;
        int y = node / w, x = node - y * w;
        double factor = _factor[node];
        int n = 0;
        for (int dy = -1; dy <= 1; dy++)
        {
            int ny = y + dy;
            if ((uint)ny >= (uint)_height) continue;
            for (int dx = -1; dx <= 1; dx++)
            {
                int nx = x + dx;
                if ((dx | dy) == 0 || (uint)nx >= (uint)w) continue;
                int v = ny * w + nx;
                if (!_passable[v]) continue;
                bool diagonal = dx != 0 && dy != 0;
                // Both side cells of a diagonal are inside the grid whenever both endpoints are.
                if (diagonal && _blocking[y * w + nx] && _blocking[ny * w + x]) continue;
                from[n] = v;
                cost[n] = (diagonal ? Diagonal : 1.0) * ((factor + _factor[v]) * 0.5);
                n++;
            }
        }
        return n;
    }
}
