using System;
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

        t.SetStylebox("panel", "Tree", Box(Raised, Border, 1, 6, 6));
        t.SetStylebox("focus", "Tree", Box(new Color(0, 0, 0, 0), Border, 1));
        t.SetStylebox("selected", "Tree", Box(Accent.Darkened(0.45f), Accent.Darkened(0.45f), 0, 4, 4));
        t.SetStylebox("selected_focus", "Tree", Box(Accent.Darkened(0.35f), Accent, 1, 4, 4));
        t.SetStylebox("cursor", "Tree", new StyleBoxEmpty());
        t.SetStylebox("cursor_unfocused", "Tree", new StyleBoxEmpty());
        t.SetStylebox("hovered", "Tree", Box(Border, Border, 0, 4, 4));
        t.SetStylebox("title_button_normal", "Tree", Box(Panel, Border, 0, 8, 6));
        t.SetStylebox("title_button_hover", "Tree", Box(Panel, Border, 0, 8, 6));
        t.SetStylebox("title_button_pressed", "Tree", Box(Panel, Border, 0, 8, 6));
        t.SetColor("font_color", "Tree", Text);
        t.SetColor("font_selected_color", "Tree", Colors.White);
        t.SetColor("title_button_color", "Tree", Muted);
        t.SetColor("guide_color", "Tree", new Color(0, 0, 0, 0));
        t.SetConstant("v_separation", "Tree", 12);
        t.SetConstant("h_separation", "Tree", 12);
        t.SetConstant("draw_guides", "Tree", 0);

        t.SetStylebox("slider", "HSlider", Box(Raised, Border, 1, 0, 3));
        t.SetStylebox("grabber_area", "HSlider", Box(Accent.Darkened(0.35f), Accent.Darkened(0.35f), 1, 0, 3));
        t.SetStylebox("grabber_area_highlight", "HSlider", Box(Accent.Darkened(0.2f), Accent.Darkened(0.2f), 1, 0, 3));

        // M3: text fields, check boxes, progress bars, scroll bars, tabs.
        t.SetStylebox("normal", "LineEdit", Box(Background, Border, 1, 8, 5));
        t.SetStylebox("focus", "LineEdit", Box(new Color(0, 0, 0, 0), Accent, 1, 8, 5));
        t.SetStylebox("read_only", "LineEdit", Box(Raised.Darkened(0.3f), Border.Darkened(0.3f), 1, 8, 5));
        t.SetColor("font_color", "LineEdit", Text);
        t.SetColor("caret_color", "LineEdit", Accent);
        t.SetColor("selection_color", "LineEdit", Accent.Darkened(0.4f));
        foreach (string type in new[] { "CheckBox", "CheckButton" })
        {
            var empty = new StyleBoxEmpty { ContentMarginLeft = 2, ContentMarginRight = 6, ContentMarginTop = 3, ContentMarginBottom = 3 };
            foreach (string state in new[] { "normal", "hover", "pressed", "hover_pressed", "disabled", "focus" }) t.SetStylebox(state, type, empty);
            t.SetColor("font_color", type, Text);
            t.SetColor("font_hover_color", type, Colors.White);
            t.SetColor("font_pressed_color", type, Text);
            t.SetColor("font_hover_pressed_color", type, Colors.White);
            t.SetColor("font_disabled_color", type, Muted.Darkened(0.3f));
        }
        foreach (bool disabled in new[] { false, true })
        {
            string suffix = disabled ? "_disabled" : "";
            t.SetIcon("checked" + suffix, "CheckBox", ToggleIcon(false, true, disabled));
            t.SetIcon("unchecked" + suffix, "CheckBox", ToggleIcon(false, false, disabled));
            t.SetIcon("radio_checked" + suffix, "CheckBox", ToggleIcon(true, true, disabled));
            t.SetIcon("radio_unchecked" + suffix, "CheckBox", ToggleIcon(true, false, disabled));
        }
        t.SetStylebox("background", "ProgressBar", Box(Background, Border, 1, 0, 0));
        t.SetStylebox("fill", "ProgressBar", Box(Accent.Darkened(0.2f), Accent.Darkened(0.2f), 0, 0, 0));
        t.SetColor("font_color", "ProgressBar", Text);
        t.SetStylebox("scroll", "VScrollBar", Box(Background, Border, 1, 4, 4));
        t.SetStylebox("grabber", "VScrollBar", Box(Border, Border, 0, 4, 4));
        t.SetStylebox("grabber_highlight", "VScrollBar", Box(Muted.Darkened(0.2f), Muted.Darkened(0.2f), 0, 4, 4));
        t.SetStylebox("grabber_pressed", "VScrollBar", Box(Accent.Darkened(0.3f), Accent.Darkened(0.3f), 0, 4, 4));
        t.SetStylebox("tab_selected", "TabBar", Box(Raised, Accent, 0, 14, 6));
        t.SetStylebox("tab_unselected", "TabBar", Box(Panel, Border, 0, 14, 6));
        t.SetStylebox("tab_hovered", "TabBar", Box(Raised, Border, 0, 14, 6));
        t.SetColor("font_selected_color", "TabBar", Colors.White);
        t.SetColor("font_unselected_color", "TabBar", Muted);
        t.SetColor("font_hovered_color", "TabBar", Text);
        t.SetStylebox("tab_selected", "TabContainer", Box(Raised, Accent, 0, 14, 6));
        t.SetStylebox("tab_unselected", "TabContainer", Box(Panel, Border, 0, 14, 6));
        t.SetStylebox("tab_hovered", "TabContainer", Box(Raised, Border, 0, 14, 6));
        t.SetStylebox("panel", "TabContainer", new StyleBoxEmpty { ContentMarginTop = 8 });
        t.SetColor("font_selected_color", "TabContainer", Colors.White);
        t.SetColor("font_unselected_color", "TabContainer", Muted);
        t.SetColor("font_hovered_color", "TabContainer", Text);
        // M7: drop-down choices (the option button looks like a button; its list like the item list).
        foreach (string state in new[] { "normal", "hover", "pressed", "disabled", "focus" }) t.SetStylebox(state, "OptionButton", t.GetStylebox(state, "Button"));
        t.SetColor("font_color", "OptionButton", Text);
        t.SetColor("font_hover_color", "OptionButton", Colors.White);
        t.SetColor("font_pressed_color", "OptionButton", Colors.White);
        t.SetColor("font_focus_color", "OptionButton", Colors.White);
        t.SetColor("font_disabled_color", "OptionButton", Muted.Darkened(0.3f));
        t.SetStylebox("panel", "PopupMenu", Box(Raised, Border, 1, 4, 4));
        t.SetStylebox("hover", "PopupMenu", Box(Accent.Darkened(0.45f), Accent.Darkened(0.45f), 0, 6, 3));
        t.SetColor("font_color", "PopupMenu", Text);
        t.SetColor("font_hover_color", "PopupMenu", Colors.White);
        t.SetColor("font_disabled_color", "PopupMenu", Muted.Darkened(0.3f));
        t.SetConstant("v_separation", "PopupMenu", 8);
        t.SetStylebox("panel", "TooltipPanel", Box(Raised, Border, 1, 8, 5));
        t.SetColor("font_color", "TooltipLabel", Text);
        return t;
    }

    /// <summary>
    /// An 18 × 18 px check box or radio icon in the app's colours (Godot's default icons are dark on our dark panels):
    /// a light outline, filled with the accent (and a check mark / dot) when on.
    /// </summary>
    private static ImageTexture ToggleIcon(bool radio, bool on, bool disabled)
    {
        const int size = 18;
        var img = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        Color edge = disabled ? Border : Muted, fill = disabled ? Accent.Darkened(0.5f) : Accent, mark = Background;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float px = x + 0.5f - size / 2f, py = y + 0.5f - size / 2f;
                Color c = new(0, 0, 0, 0);
                if (radio)
                {
                    float d = MathF.Sqrt(px * px + py * py);
                    if (d <= 7.5f) c = on ? fill : new Color(0, 0, 0, 0.25f);
                    if (d is > 6f and <= 7.5f) c = on ? fill : edge;
                    if (on && d <= 3.2f) c = mark;
                }
                else
                {
                    float m = MathF.Max(MathF.Abs(px), MathF.Abs(py));
                    if (m <= 7.5f) c = on ? fill : new Color(0, 0, 0, 0.25f);
                    if (m is > 6f and <= 7.5f) c = on ? fill : edge;
                    // Check mark: two strokes, (−4, 0) → (−1, 3) → (4, −3).
                    if (on && (Near(px, py, -4f, 0f, -1f, 3f) || Near(px, py, -1f, 3f, 4f, -3f))) c = mark;
                }
                img.SetPixel(x, y, c);
            }
        }
        return ImageTexture.CreateFromImage(img);

        static bool Near(float x, float y, float ax, float ay, float bx, float by)
        {
            float dx = bx - ax, dy = by - ay;
            float t = Math.Clamp(((x - ax) * dx + (y - ay) * dy) / (dx * dx + dy * dy), 0f, 1f);
            float ex = ax + t * dx - x, ey = ay + t * dy - y;
            return ex * ex + ey * ey <= 1.1f * 1.1f;
        }
    }

    /// <summary>The panel style without its slight transparency, for a panel over busy text (the enlarged graph over the HUD).</summary>
    public static StyleBoxFlat SolidPanel() => Box(new Color(Panel.R, Panel.G, Panel.B), Border, 1, 12, 10);

    /// <summary>A small button that takes no keyboard focus (so Space and the other keys keep reaching the view).</summary>
    public static Button SmallButton(string text, System.Action pressed, int size = 15)
    {
        var button = new Button { Text = text, FocusMode = Control.FocusModeEnum.None };
        button.AddThemeFontSizeOverride("font_size", size);
        button.Pressed += pressed;
        return button;
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
