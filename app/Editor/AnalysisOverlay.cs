using Godot;
using Nitrogenesis.Sim.Map;

namespace Nitrogenesis.Editor;

/// <summary>
/// The live checks drawn over the map (map space, 1 unit = 1 cell): the area cut off from the finish (red tint),
/// the cells too narrow for the car (orange diagonal hatch) and the grass-shortcut route (a cyan line). The two
/// masks are one RGBA texture, 1 px per cell with nearest filtering, so a result is one texture update.
/// </summary>
public partial class AnalysisOverlay : Node2D
{
    private static readonly Color UnreachableTint = new(1f, 0.15f, 0.12f, 0.5f);
    private static readonly Color NarrowStripe = new(1f, 0.72f, 0.1f, 0.62f);
    private static readonly Color NarrowGap = new(1f, 0.72f, 0.1f, 0.18f);
    private static readonly Color RouteColor = new(0.3f, 0.92f, 1f, 0.95f);
    private static readonly Color RouteShadow = new(0f, 0f, 0f, 0.55f);

    private readonly Sprite2D _sprite = new() { Centered = false, TextureFilter = TextureFilterEnum.Nearest, Name = "Masks" };
    private ImageTexture? _texture;
    private Vector2[] _route = [];
    private bool _pending;

    public AnalysisOverlay()
    {
        Name = "AnalysisOverlay";
        AddChild(_sprite);
    }

    /// <summary>Shows a result's masks and route. The unreachable mask is only filled when the finish is cut off.</summary>
    public void Show(EditorAnalysisResult r)
    {
        int w = r.Width, h = r.Height;
        byte[] rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                Color c;
                if (r.UnreachableMask[i]) c = UnreachableTint;
                else if (r.TooNarrowMask[i]) c = ((x + y) & 3) < 2 ? NarrowStripe : NarrowGap;
                else continue;
                int o = i * 4;
                rgba[o] = (byte)(c.R * 255f + 0.5f);
                rgba[o + 1] = (byte)(c.G * 255f + 0.5f);
                rgba[o + 2] = (byte)(c.B * 255f + 0.5f);
                rgba[o + 3] = (byte)(c.A * 255f + 0.5f);
            }
        }
        using var image = Image.CreateFromData(w, h, false, Image.Format.Rgba8, rgba);
        if (_texture is not null && _texture.GetWidth() == w && _texture.GetHeight() == h) _texture.Update(image);
        else
        {
            _texture = ImageTexture.CreateFromImage(image);
            _sprite.Texture = _texture;
        }

        _route = new Vector2[r.ShortcutRoute.Length];
        for (int i = 0; i < _route.Length; i++) _route[i] = new Vector2(r.ShortcutRoute[i].X, r.ShortcutRoute[i].Y);
        QueueRedraw();
    }

    /// <summary>Fades the overlay while a newer check is on its way (the shown result is for an older state of the map).</summary>
    public void SetPending(bool pending)
    {
        if (_pending == pending) return;
        _pending = pending;
        Modulate = new Color(1f, 1f, 1f, pending ? 0.35f : 1f);
    }

    public override void _Draw()
    {
        if (_route.Length < 2) return;
        DrawPolyline(_route, RouteShadow, 1.1f, true);
        DrawPolyline(_route, RouteColor, 0.6f, true);
        DrawCircle(_route[^1], 0.9f, RouteColor);
    }
}
