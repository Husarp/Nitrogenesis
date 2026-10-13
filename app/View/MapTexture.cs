using Godot;
using Nitrogenesis.Sim.Map;

namespace Nitrogenesis.View;

/// <summary>
/// The map as one texture, 1 px per cell (PLAN §3.1), drawn scaled up with nearest filtering for the pixel look.
/// A faint per-cell brightness jitter keeps large areas from looking flat; Finish cells are checkered. The start
/// (PLAN §3.1 start flag) is painted as a small arrow in the accent colour pointing along the start heading.
/// </summary>
public static class MapTexture
{
    public static readonly Color Road = new(0.25f, 0.26f, 0.28f);
    public static readonly Color Grass = new(0.29f, 0.53f, 0.24f);
    public static readonly Color Wall = new(0.12f, 0.09f, 0.08f);
    public static readonly Color Danger = new(0.92f, 0.32f, 0.12f);
    public static readonly Color FinishLight = new(0.95f, 0.95f, 0.92f);
    public static readonly Color FinishDark = new(0.13f, 0.13f, 0.15f);
    /// <summary>Clear colour around the map (outside counts as wall).</summary>
    public static readonly Color Outside = new(0.06f, 0.05f, 0.05f);

    /// <summary>Start arrow colour (the UI accent).</summary>
    public static Color StartMark => Ui.UiTheme.Accent;

    public static ImageTexture Create(Grid grid, TrackStart? start = null) => ImageTexture.CreateFromImage(CreateImage(grid, start));

    /// <summary>The map's pixels as an image (the editor keeps it and repaints parts of it, see <see cref="Repaint"/>).</summary>
    public static Image CreateImage(Grid grid, TrackStart? start = null)
    {
        int w = grid.Width, h = grid.Height;
        byte[] rgb = new byte[w * h * 3];
        WriteCells(grid, 0, 0, w, h, rgb);
        if (start is { } s) PaintStart(rgb, w, h, s);
        return Image.CreateFromData(w, h, false, Image.Format.Rgb8, rgb);
    }

    /// <summary>Repaints only the cells of <paramref name="rect"/> (clipped to the grid) in an image from <see cref="CreateImage"/>; the caller updates the texture.</summary>
    public static void Repaint(Image image, Grid grid, DirtyRect rect)
    {
        int x0 = Mathf.Max(rect.X, 0), y0 = Mathf.Max(rect.Y, 0);
        int x1 = Mathf.Min(rect.X + rect.W, grid.Width), y1 = Mathf.Min(rect.Y + rect.H, grid.Height);
        int w = x1 - x0, h = y1 - y0;
        if (w <= 0 || h <= 0) return;
        byte[] rgb = new byte[w * h * 3];
        WriteCells(grid, x0, y0, w, h, rgb);
        using var patch = Image.CreateFromData(w, h, false, Image.Format.Rgb8, rgb);
        image.BlitRect(patch, new Rect2I(0, 0, w, h), new Vector2I(x0, y0));
    }

    /// <summary>Writes the colours of the cells x0..x0+w-1 × y0..y0+h-1 into <paramref name="rgb"/> (row-major, 3 bytes each, region-local).</summary>
    private static void WriteCells(Grid grid, int x0, int y0, int w, int h, byte[] rgb)
    {
        for (int y = y0; y < y0 + h; y++)
        {
            for (int x = x0; x < x0 + w; x++)
            {
                var type = (CellType)grid.Cells[y * grid.Width + x];
                Color c = type switch
                {
                    CellType.Road => Road,
                    CellType.Grass => Grass,
                    CellType.Danger => ((x + y) & 1) == 0 ? Danger : Danger.Darkened(0.12f),
                    CellType.Finish => ((x + y) & 1) == 0 ? FinishLight : FinishDark,
                    _ => Wall,
                };
                if (type is CellType.Road or CellType.Grass or CellType.Wall) c = Jitter(c, x, y, type == CellType.Grass ? 0.06f : 0.03f);
                int o = ((y - y0) * w + (x - x0)) * 3;
                rgb[o] = (byte)(c.R * 255f + 0.5f);
                rgb[o + 1] = (byte)(c.G * 255f + 0.5f);
                rgb[o + 2] = (byte)(c.B * 255f + 0.5f);
            }
        }
    }

    /// <summary>
    /// A filled arrow about 4 cells long and 5 wide, centred on the start, pointing along its heading: every cell at
    /// least half inside it (2 × 2 samples per cell). A road marking; the cars start on top of it.
    /// </summary>
    private static void PaintStart(byte[] rgb, int w, int h, TrackStart start)
    {
        float a = Mathf.DegToRad(start.AngleDeg), cos = Mathf.Cos(a), sin = Mathf.Sin(a);
        int x0 = (int)start.X - 4, y0 = (int)start.Y - 4;
        for (int y = Mathf.Max(y0, 0); y <= Mathf.Min(y0 + 8, h - 1); y++)
        {
            for (int x = Mathf.Max(x0, 0); x <= Mathf.Min(x0 + 8, w - 1); x++)
            {
                int hits = 0;
                for (int sample = 0; sample < 4; sample++)
                {
                    float dx = x + 0.25f + 0.5f * (sample & 1) - start.X, dy = y + 0.25f + 0.5f * (sample >> 1) - start.Y;
                    float u = dx * cos + dy * sin, v = -dx * sin + dy * cos; // along / across the heading
                    if (u >= -2f && u <= 2.4f && Mathf.Abs(v) <= (2.4f - u) * 0.6f) hits++;
                }
                if (hits < 2) continue;
                int o = (y * w + x) * 3;
                rgb[o] = (byte)(StartMark.R * 255f + 0.5f);
                rgb[o + 1] = (byte)(StartMark.G * 255f + 0.5f);
                rgb[o + 2] = (byte)(StartMark.B * 255f + 0.5f);
            }
        }
    }

    private static Color Jitter(Color c, int x, int y, float amount)
    {
        uint hsh = (uint)(x * 73856093) ^ (uint)(y * 19349663);
        hsh ^= hsh >> 13;
        hsh *= 0x5bd1e995;
        hsh ^= hsh >> 15;
        float f = 1f + ((hsh & 0xFF) / 255f - 0.5f) * 2f * amount;
        return new Color(Mathf.Clamp(c.R * f, 0, 1), Mathf.Clamp(c.G * f, 0, 1), Mathf.Clamp(c.B * f, 0, 1));
    }
}
