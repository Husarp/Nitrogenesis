using Godot;
using Nitrogenesis.Sim.Map;
using Nitrogenesis.Ui;
using Nitrogenesis.View;

namespace Nitrogenesis.Editor;

/// <summary>A toggle button showing a paint type's map colour (Finish and Danger checkered); the chosen one has an accent frame.</summary>
public partial class PaintSwatch : Button
{
    public CellType Type { get; init; }

    public PaintSwatch()
    {
        ToggleMode = true;
        FocusMode = FocusModeEnum.None;
        CustomMinimumSize = new Vector2(36, 36);
        var empty = new StyleBoxEmpty();
        foreach (string state in new[] { "normal", "hover", "pressed", "hover_pressed", "disabled", "focus" }) AddThemeStyleboxOverride(state, empty);
    }

    public override void _Draw()
    {
        var rect = new Rect2(Vector2.Zero, Size).Grow(-3);
        var (a, b) = Type switch
        {
            CellType.Road => (MapTexture.Road, MapTexture.Road),
            CellType.Grass => (MapTexture.Grass, MapTexture.Grass),
            CellType.Danger => (MapTexture.Danger, MapTexture.Danger.Darkened(0.12f)),
            CellType.Finish => (MapTexture.FinishLight, MapTexture.FinishDark),
            _ => (MapTexture.Wall, MapTexture.Wall),
        };
        DrawRect(rect, a);
        if (a != b)
        {
            const int n = 4;
            var cell = rect.Size / n;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                    if (((x + y) & 1) == 1) DrawRect(new Rect2(rect.Position + new Vector2(x * cell.X, y * cell.Y), cell), b);
        }
        if (Type == CellType.Wall) DrawRect(rect, UiTheme.Border, false);
        DrawRect(new Rect2(Vector2.Zero, Size).Grow(-1), ButtonPressed ? UiTheme.Accent : IsHovered() ? UiTheme.Muted : UiTheme.Border, false, ButtonPressed ? 3 : 1);
    }
}
