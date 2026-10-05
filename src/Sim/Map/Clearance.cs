namespace Nitrogenesis.Sim.Map;

/// <summary>
/// Distance transform from blocking cells (PLAN §3.2): the Euclidean distance to the nearest point of a blocking
/// cell (Wall, Danger, or the area outside the map), at each cell's centre (<see cref="Values"/>) and at the best
/// point inside each cell (<see cref="Best"/>), which decides passability.
/// </summary>
/// <remarks>
/// <para>Distances are measured to the blocking cell's <em>square</em>, not its centre: a road cell next to a wall
/// has centre clearance 0.5, a blocking cell has 0.</para>
/// <para><b>Passable</b> means "the car centre can be somewhere in this cell": the best clearance over the cell's
/// 9 half-cell lattice points (4 corners, 4 edge midpoints, centre) is at least the car's half-width. So a corridor
/// 2 cells wide is passable (its midline, on the cells' shared edge, has clearance 1.0 ≥ 0.9) and a 1-cell
/// corridor is not (0.5 at most), which matches what the car's outline can drive through. Measuring only at
/// cell centres would wrongly need 3 cells.</para>
/// <para>Algorithm (exact): the nearest point of a blocking square to a lattice point is itself a lattice point,
/// so the clearance is the distance to the nearest <em>blocked</em> lattice point (one on a blocking square or the
/// map border). That is a standard Euclidean distance transform on the lattice, computed with the linear-time
/// separable algorithm of Meijster et al. in integers (half-cell units); the square roots are IEEE-exact, so
/// results are identical on every platform.</para>
/// </remarks>
public sealed class Clearance
{
    public int Width { get; }
    public int Height { get; }

    /// <summary>Clearance at each cell's centre, row-major like <see cref="Grid.Cells"/> (the physics broadphase uses it).</summary>
    public float[] Values { get; }

    /// <summary>Largest clearance at any of each cell's 9 half-cell lattice points (≥ the centre value; 0 for blocking cells).</summary>
    public float[] Best { get; }

    private Clearance(int width, int height, float[] values, float[] best)
    {
        Width = width;
        Height = height;
        Values = values;
        Best = best;
    }

    /// <summary>Centre clearance of cell (x, y).</summary>
    public float this[int x, int y] => Values[y * Width + x];

    /// <summary>True when the car centre may be somewhere in cell <paramref name="index"/>: non-blocking, and some point of it has clearance ≥ <paramref name="halfWidth"/>.</summary>
    public bool IsPassable(int index, float halfWidth) => Best[index] > 0f && Best[index] >= halfWidth;

    public static Clearance Compute(Grid grid)
    {
        int w = grid.Width, h = grid.Height;
        int lw = 2 * w + 1, lh = 2 * h + 1; // lattice point (i, j) is at world (i/2, j/2)
        byte[] cells = grid.Cells;

        // Column pass: vertical distance (in half cells) from each lattice point to the nearest blocked lattice
        // point in its column. The map border is blocked (outside counts as wall), so it is always finite.
        var g = new int[lw * lh];
        for (int i = 0; i < lw; i++)
        {
            g[i] = 0;
            for (int j = 1; j < lh; j++) g[j * lw + i] = IsBlocked(cells, w, h, i, j) ? 0 : g[(j - 1) * lw + i] + 1;
            for (int j = lh - 2; j >= 0; j--)
                if (g[(j + 1) * lw + i] + 1 < g[j * lw + i]) g[j * lw + i] = g[(j + 1) * lw + i] + 1;
        }

        // Row pass (Meijster et al., exact integer Euclidean distance transform): per lattice row, the lower envelope
        // of the parabolas (x − u)² + g(u)², so each point gets min over columns u in linear time.
        var bestSq = new int[w * h]; // per cell: largest squared clearance over its 9 lattice points
        var values = new float[w * h];
        var rowSq = new int[lw];
        var s = new int[lw];
        var t = new int[lw];
        for (int j = 0; j < lh; j++)
        {
            int row = j * lw;
            int q = 0;
            s[0] = 0;
            t[0] = 0;
            for (int u = 1; u < lw; u++)
            {
                int gu = g[row + u];
                while (q >= 0 && F(t[q], s[q], g[row + s[q]]) > F(t[q], u, gu)) q--;
                if (q < 0)
                {
                    q = 0;
                    s[0] = u;
                }
                else
                {
                    int sep = 1 + Separation(s[q], u, g[row + s[q]], gu);
                    if (sep < lw)
                    {
                        q++;
                        s[q] = u;
                        t[q] = sep;
                    }
                }
            }
            for (int u = lw - 1; u >= 0; u--)
            {
                rowSq[u] = F(u, s[q], g[row + s[q]]);
                if (u == t[q]) q--;
            }

            // Fold the row into the cells it touches: an even (edge) row belongs to the cells above and below it.
            int k = j >> 1;
            bool centreRow = (j & 1) == 1;
            for (int cy = Math.Max(k - (centreRow ? 0 : 1), 0); cy <= Math.Min(k, h - 1); cy++)
            {
                int cellRow = cy * w;
                for (int x = 0; x < w; x++)
                {
                    int m = Math.Max(rowSq[2 * x], Math.Max(rowSq[2 * x + 1], rowSq[2 * x + 2]));
                    if (m > bestSq[cellRow + x]) bestSq[cellRow + x] = m;
                }
            }
            if (centreRow)
            {
                int cellRow = k * w;
                for (int x = 0; x < w; x++) values[cellRow + x] = (float)(Math.Sqrt(rowSq[2 * x + 1]) * 0.5);
            }
        }

        var best = new float[w * h];
        for (int i = 0; i < best.Length; i++) best[i] = (float)(Math.Sqrt(bestSq[i]) * 0.5);
        return new Clearance(w, h, values, best);
    }

    /// <summary>Squared distance from lattice column x to the parabola apex of column u with vertical distance gu.</summary>
    private static int F(int x, int u, int gu) => (x - u) * (x - u) + gu * gu;

    /// <summary>Last column where the parabola of column i is not above that of column u (i &lt; u); floor division.</summary>
    private static int Separation(int i, int u, int gi, int gu)
    {
        int numerator = u * u - i * i + gu * gu - gi * gi, denominator = 2 * (u - i);
        int quotient = numerator / denominator;
        return numerator % denominator < 0 ? quotient - 1 : quotient;
    }

    /// <summary>
    /// Is lattice point (i, j) part of a blocking square? A point belongs to every cell whose closed square
    /// contains it: 1 (cell centre), 2 (edge midpoint) or 4 (corner) cells; cells outside the map are blocking.
    /// The nearest point of a square to a lattice point is itself a lattice point (clamping a half-cell
    /// coordinate to the square's integer edges), so distances to blocked lattice points are exact.
    /// </summary>
    private static bool IsBlocked(byte[] cells, int w, int h, int i, int j)
    {
        int x0 = (i - 1) >> 1, x1 = i >> 1, y0 = (j - 1) >> 1, y1 = j >> 1; // equal for odd i / j
        return Blocking(cells, w, h, x0, y0) || Blocking(cells, w, h, x1, y0) || Blocking(cells, w, h, x0, y1) || Blocking(cells, w, h, x1, y1);
    }

    private static bool Blocking(byte[] cells, int w, int h, int x, int y) =>
        (uint)x >= (uint)w || (uint)y >= (uint)h || CellTypes.IsBlocking((CellType)cells[y * w + x]);
}
