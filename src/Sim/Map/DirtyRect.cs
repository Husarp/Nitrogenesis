namespace Nitrogenesis.Sim.Map;

/// <summary>A rectangle of cells (x, y, width, height). Empty when width or height is 0.</summary>
public readonly record struct DirtyRect(int X, int Y, int W, int H)
{
    public static readonly DirtyRect Empty = new(0, 0, 0, 0);

    public bool IsEmpty => W <= 0 || H <= 0;

    /// <summary>Smallest rectangle containing both (an empty side is ignored).</summary>
    public DirtyRect Union(DirtyRect other)
    {
        if (other.IsEmpty) return this;
        if (IsEmpty) return other;
        int x0 = Math.Min(X, other.X), y0 = Math.Min(Y, other.Y);
        int x1 = Math.Max(X + W, other.X + other.W), y1 = Math.Max(Y + H, other.Y + other.H);
        return new DirtyRect(x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>Rectangle covering the inclusive cell range x0..x1 × y0..y1.</summary>
    public static DirtyRect FromBounds(int x0, int y0, int x1, int y1) => new(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
}

/// <summary>What one edit call changed: the cells' bounding rectangle and whether the start moved.</summary>
public readonly record struct EditResult(DirtyRect Rect, bool StartChanged)
{
    public static readonly EditResult None = new(DirtyRect.Empty, false);

    /// <summary>True when anything at all changed.</summary>
    public bool Changed => !Rect.IsEmpty || StartChanged;
}
