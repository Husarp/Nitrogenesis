using Godot;
using Nitrogenesis.Sim.Map;
using Nitrogenesis.Ui;

namespace Nitrogenesis.Editor;

/// <summary>
/// The editor's bottom strip: the live-check messages (green tick = fine, orange = warning, red = error) and the
/// estimated time on the right. While a check is pending the previous result stays but is dimmed and marked
/// "checking…", so a stale result never looks current.
/// </summary>
public partial class EditorStatusBar : HBoxContainer
{
    private const int FontSize = 15;

    private readonly HBoxContainer _messages = new();
    private readonly Label _checking = UiTheme.Label("", FontSize, UiTheme.Muted);
    private readonly Label _estimate = UiTheme.Label("", FontSize, UiTheme.Text, HorizontalAlignment.Right);
    private EditorAnalysisResult? _shown;
    private bool _pending = true;
    private float? _referenceSeconds;

    /// <summary>The text of the messages as shown (for debug logs and checks).</summary>
    public string ShownText { get; private set; } = "";

    public EditorStatusBar()
    {
        Name = "EditorStatusBar";
        AddThemeConstantOverride("separation", 18);
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _messages.AddThemeConstantOverride("separation", 22);
        AddChild(_messages);
        AddChild(_checking);
        var spacer = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        AddChild(spacer);
        AddChild(_estimate);
        Rebuild();
    }

    /// <summary>Shows <paramref name="result"/> as current.</summary>
    public void Show(EditorAnalysisResult result)
    {
        _shown = result;
        _pending = false;
        Rebuild();
    }

    /// <summary>Marks the shown result as out of date (or current again).</summary>
    public void SetPending(bool pending)
    {
        if (_pending == pending) return;
        _pending = pending;
        Rebuild();
    }

    /// <summary>The Test button's reference time, shown instead of the estimate while it is current; null goes back to the estimate.</summary>
    public void SetReferenceTime(float? seconds)
    {
        if (_referenceSeconds == seconds) return;
        _referenceSeconds = seconds;
        Rebuild();
    }

    /// <summary>Forgets the shown result (a different document was opened).</summary>
    public void Clear()
    {
        _shown = null;
        _pending = true;
        Rebuild();
    }

    private void Rebuild()
    {
        foreach (var child in _messages.GetChildren())
        {
            _messages.RemoveChild(child);
            child.QueueFree();
        }
        var texts = new System.Collections.Generic.List<string>();
        if (_shown is not null)
        {
            foreach (var m in _shown.Messages)
            {
                var (icon, color) = m.Severity switch
                {
                    MessageSeverity.Ok => ("✓", UiTheme.Good),
                    MessageSeverity.Warning => ("!", UiTheme.Accent),
                    _ => ("×", UiTheme.Bad),
                };
                string text = $"{icon}  {m.Text}";
                texts.Add(text);
                _messages.AddChild(UiTheme.Label(text, FontSize, color));
            }
        }
        ShownText = string.Join(" | ", texts);
        _messages.Modulate = new Color(1f, 1f, 1f, _pending ? 0.45f : 1f);
        _checking.Text = _pending ? "checking…" : "";
        if (_referenceSeconds is { } r)
        {
            _estimate.Text = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{r:0.0} s (Test)");
            _estimate.Modulate = Colors.White; // the Test time is exact for this map, whatever the live check is doing
        }
        else
        {
            _estimate.Text = _shown?.EstimatedSeconds is { } s ? $"≈ {s:0} s" : "";
            _estimate.Modulate = new Color(1f, 1f, 1f, _pending ? 0.45f : 1f);
        }
    }
}
