namespace Nitrogenesis.Sim.Map;

/// <summary>
/// The track's cell grid: <see cref="Width"/> × <see cref="Height"/> cells in row-major order
/// (index = y·Width + x). Cell (x, y) covers [x, x+1) × [y, y+1) in world units, so its centre is at
/// (x + 0.5, y + 0.5). Everything outside the grid counts as <see cref="CellType.Wall"/>.
/// </summary>
public sealed class Grid
{
    /// <summary>Largest allowed side length; keeps files and per-cell arrays at a sane size.</summary>
    public const int MaxSide = 4096;

    public int Width { get; }
    public int Height { get; }

    /// <summary>Raw cell bytes (values of <see cref="CellType"/>). Exposed for fast loops and file I/O.</summary>
    public byte[] Cells { get; }

    /// <summary>Creates a grid filled with Wall (a new map starts as wall, PLAN §6.3).</summary>
    public Grid(int width, int height)
    {
        ValidateSize(width, height);
        Width = width;
        Height = height;
        Cells = new byte[width * height];
        Array.Fill(Cells, (byte)CellType.Wall);
    }

    /// <summary>Wraps existing cell bytes (not copied). Every byte must be a valid <see cref="CellType"/>.</summary>
    public Grid(int width, int height, byte[] cells)
    {
        ValidateSize(width, height);
        ArgumentNullException.ThrowIfNull(cells);
        if (cells.Length != width * height)
            throw new ArgumentException($"Expected {width * height} cells, got {cells.Length}.", nameof(cells));
        foreach (byte b in cells)
            if (b > (byte)CellTypes.MaxValue) throw new ArgumentException($"Invalid cell value {b}.", nameof(cells));
        Width = width;
        Height = height;
        Cells = cells;
    }

    public int CellCount => Cells.Length;

    public bool InBounds(int x, int y) => (uint)x < (uint)Width && (uint)y < (uint)Height;

    public int Index(int x, int y) => y * Width + x;

    /// <summary>The cell at (x, y); Wall outside the grid.</summary>
    public CellType this[int x, int y]
    {
        get => InBounds(x, y) ? (CellType)Cells[y * Width + x] : CellType.Wall;
        set
        {
            if (!InBounds(x, y)) throw new ArgumentOutOfRangeException($"({x}, {y}) is outside the grid.");
            Cells[y * Width + x] = (byte)value;
        }
    }

    /// <summary>Blocking (Wall/Danger) test that treats outside the grid as Wall.</summary>
    public bool IsBlocking(int x, int y) => CellTypes.IsBlocking(this[x, y]);

    public Grid Clone() => new(Width, Height, (byte[])Cells.Clone());

    private static void ValidateSize(int width, int height)
    {
        if (width < 1 || height < 1 || width > MaxSide || height > MaxSide)
            throw new ArgumentOutOfRangeException(nameof(width), $"Grid size {width}×{height} must be 1…{MaxSide} per side.");
    }
}
