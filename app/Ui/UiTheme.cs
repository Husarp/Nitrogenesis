using Godot;

namespace Nitrogenesis.Ui;

/// <summary>The app's look: dark background, the orange accent of the M0 screen, light grey text.</summary>
public static class UiTheme
{
    public static readonly Color Background = new(0.07f, 0.08f, 0.12f);
    public static readonly Color Panel = new(0.10f, 0.11f, 0.16f, 0.92f);
    public static readonly Color Raised = new(0.14f, 0.15f, 0.21f);
    public static readonly Color Border = new(0.24f, 0.26f, 0.33f);
    public static readonly Color Accent = new(0.98f, 0.6f, 0.25f);
    public static readonly Color Text = new(0.86f, 0.87f, 0.92f);
    public static readonly Color Muted = new(0.6f, 0.62f, 0.7f);
    public static readonly Color Good = new(0.65f, 0.89f, 0.63f);
    public static readonly Color Bad = new(1f, 0.42f, 0.36f);

    private static Theme? s_theme;

    public static Theme Theme => s_theme ??= Create();

    private static StyleBoxFlat Box(Color bg, Color border, int borderWidth = 1, int padH = 14, int padV = 8)
    {
        var box = new StyleBoxFlat
        {
            BgColor = bg,
            BorderColor = border,
            ContentMarginLeft = padH,
            ContentMarginRight = padH,
            ContentMarginTop = padV,
            ContentMarginBottom = padV,
        };
        box.SetBorderWidthAll(borderWidth);
        box.SetCornerRadiusAll(4);
        return box;
    }

    private static Theme Create()
    {
        var t = new Theme { DefaultFontSize = 18 };
        t.SetColor("font_color", "Label", Text);

        t.SetStylebox("normal", "Button", Box(Raised, Border));
        t.SetStylebox("hover", "Button", Box(Raised, Accent));
        t.SetStylebox("pressed", "Button", Box(Accent.Darkened(0.35f), Accent));
        t.SetStylebox("focus", "Button", Box(new Color(0, 0, 0, 0), Accent, 2));
        t.SetStylebox("disabled", "Button", Box(Raised.Darkened(0.3f), Border.Darkened(0.3f)));
        t.SetColor("font_color", "Button", Text);
        t.SetColor("font_hover_color", "Button", Colors.White);
        t.SetColor("font_pressed_color", "Button", Colors.White);
        t.SetColor("font_focus_color", "Button", Colors.White);
        t.SetColor("font_disabled_color", "Button", Muted.Darkened(0.3f));

        t.SetStylebox("panel", "ItemList", Box(Raised, Border, 1, 6, 6));
        t.SetStylebox("focus", "ItemList", Box(new Color(0, 0, 0, 0), Border, 1));
        t.SetStylebox("selected", "ItemList", Box(Accent.Darkened(0.45f), Accent.Darkened(0.45f), 0, 4, 4));
        t.SetStylebox("selected_focus", "ItemList", Box(Accent.Darkened(0.35f), Accent, 1, 4, 4));
        t.SetStylebox("hovered", "ItemList", Box(Border, Border, 0, 4, 4));
        t.SetColor("font_color", "ItemList", Text);
        t.SetColor("font_selected_color", "ItemList", Colors.White);
        t.SetConstant("v_separation", "ItemList", 10);

        t.SetStylebox("panel", "PanelContainer", Box(Panel, Border, 1, 12, 10));

        t.SetStylebox("slider", "HSlider", Box(Raised, Border, 1, 0, 3));
        t.SetStylebox("grabber_area", "HSlider", Box(Accent.Darkened(0.35f), Accent.Darkened(0.35f), 1, 0, 3));
        t.SetStylebox("grabber_area_highlight", "HSlider", Box(Accent.Darkened(0.2f), Accent.Darkened(0.2f), 1, 0, 3));
        return t;
    }

    /// <summary>A label in the given size and colour.</summary>
    public static Label Label(string text, int size, Color color, HorizontalAlignment align = HorizontalAlignment.Left)
    {
        var label = new Label { Text = text, HorizontalAlignment = align };
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", color);
        return label;
    }
}
