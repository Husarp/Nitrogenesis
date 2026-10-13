using System;
using System.Globalization;
using Godot;
using Nitrogenesis.Sim.Racing;
using Nitrogenesis.Ui;

namespace Nitrogenesis.View;

/// <summary>
/// The HUD of a race (PLAN §6.5): track, live timer, progress and speed top-left; under them the physics warning (if
/// any) and the split table: your time at 25 / 50 / 75 % and, per ghost, how far ahead (green, −) or behind (red, +)
/// you were there. The countdown, GO! and CRASHED are big in the middle; the finish is the results screen's job.
/// </summary>
public partial class RaceHud : CanvasLayer
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private const string Hints = "W/S or ↑/↓ throttle · brake   A/D or ←/→ steer   gamepad: left stick + triggers   R restart   wheel zoom   Esc back";
    /// <summary>"GO!" stays this long after the start signal, s.</summary>
    private const float GoSeconds = 0.7f;
    private const int NameWidth = 200;

    private Race _race = null!;
    private string _trackName = "";
    private Label _time = null!, _progress = null!, _speed = null!, _attempt = null!, _big = null!, _small = null!;
    private Label[] _you = new Label[RaceSplits.Count];
    private Label[][] _cells = [];
    private PlayerPhase _shownPhase = (PlayerPhase)255;
    private int _shownCount = -1, _shownCrossed = -1, _shownAttempt = -1;

    public event Action? BackPressed;

    public static RaceHud Create(Race race, string trackName) => new() { _race = race, _trackName = trackName };

    public override void _Ready()
    {
        var root = new Control { Theme = UiTheme.Theme, MouseFilter = Control.MouseFilterEnum.Ignore };
        root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        AddChild(root);

        var panel = new PanelContainer { Position = new Vector2(12, 12), MouseFilter = Control.MouseFilterEnum.Ignore };
        root.AddChild(panel);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 6);
        panel.AddChild(box);
        box.AddChild(UiTheme.Label(_trackName.ToUpperInvariant(), 20, UiTheme.Accent));
        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 18);
        grid.AddThemeConstantOverride("v_separation", 2);
        box.AddChild(grid);
        _time = Row(grid, "Time", 22);
        _progress = Row(grid, "Progress", 15);
        _speed = Row(grid, "Speed", 15);
        _attempt = Row(grid, "Attempt", 15);

        if (_race.Physics.Warning is { } warning)
        {
            var w = UiTheme.Label(warning, 14, UiTheme.Accent);
            w.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            w.CustomMinimumSize = new Vector2(NameWidth + 3 * 70, 0);
            box.AddChild(w);
        }

        // The split table: a row for you, one per ghost (colour swatch + name), a column per split.
        var splits = new GridContainer { Columns = 1 + RaceSplits.Count };
        splits.AddThemeConstantOverride("h_separation", 10);
        splits.AddThemeConstantOverride("v_separation", 3);
        box.AddChild(splits);
        splits.AddChild(UiTheme.Label("Splits", 15, UiTheme.Muted));
        for (int s = 0; s < RaceSplits.Count; s++)
            splits.AddChild(Cell((int)(RaceSplits.Fraction(s) * 100) + " %", UiTheme.Muted));
        splits.AddChild(Named("You", UiTheme.Accent, UiTheme.Accent));
        for (int s = 0; s < RaceSplits.Count; s++) splits.AddChild(_you[s] = Cell("–", UiTheme.Accent));
        _cells = new Label[_race.Ghosts.Count][];
        for (int g = 0; g < _cells.Length; g++)
        {
            var entry = _race.Ghosts[g].Entry;
            var name = Named(entry.Label, RaceView.GhostColor(g), UiTheme.Text);
            name.TooltipText = entry.Label + (entry.Finished ? $" · {entry.FinishSeconds.ToString("0.00", Inv)} s" : " · no finish");
            splits.AddChild(name);
            _cells[g] = new Label[RaceSplits.Count];
            for (int s = 0; s < RaceSplits.Count; s++) splits.AddChild(_cells[g][s] = Cell("–", UiTheme.Muted));
        }
        if (_cells.Length > 0)
            box.AddChild(UiTheme.Label("− ahead of the ghost · + behind · out: the ghost never got that far", 13, UiTheme.Muted));

        // The upper part of the screen, so the car (in the middle) stays visible.
        var center = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.Center };
        center.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        center.AnchorBottom = 0.5f;
        center.OffsetBottom = -40;
        root.AddChild(center);
        _big = UiTheme.Label("", 96, UiTheme.Accent, HorizontalAlignment.Center);
        _big.AddThemeConstantOverride("outline_size", 12);
        _big.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.8f));
        center.AddChild(_big);
        _small = UiTheme.Label("", 22, UiTheme.Text, HorizontalAlignment.Center);
        _small.AddThemeConstantOverride("outline_size", 6);
        _small.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.8f));
        center.AddChild(_small);

        var hints = UiTheme.Label(Hints, 14, UiTheme.Muted);
        hints.SetAnchorsPreset(Control.LayoutPreset.BottomLeft);
        hints.Position = new Vector2(14, -30);
        hints.GrowVertical = Control.GrowDirection.Begin;
        root.AddChild(hints);

        root.AddChild(Hud.MenuButton(() => BackPressed?.Invoke(), "Back  (Esc)"));
    }

    private static Label Row(GridContainer grid, string name, int size)
    {
        grid.AddChild(UiTheme.Label(name, 15, UiTheme.Muted));
        var value = UiTheme.Label("—", size, UiTheme.Text);
        value.CustomMinimumSize = new Vector2(150, 0);
        grid.AddChild(value);
        return value;
    }

    private static Label Cell(string text, Color color)
    {
        var label = UiTheme.Label(text, 15, color, HorizontalAlignment.Right);
        label.CustomMinimumSize = new Vector2(70, 0);
        return label;
    }

    /// <summary>A colour swatch and a name (cut with … when long).</summary>
    private static HBoxContainer Named(string name, Color swatch, Color text)
    {
        var row = new HBoxContainer { CustomMinimumSize = new Vector2(NameWidth, 0) };
        row.AddThemeConstantOverride("separation", 8);
        row.AddChild(new ColorRect { Color = swatch, CustomMinimumSize = new Vector2(12, 12), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter });
        var label = UiTheme.Label(name, 15, text);
        label.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        row.AddChild(label);
        return row;
    }

    /// <summary>Refreshes the text (every frame: the timer runs); <paramref name="alpha"/> is the render fraction of a tick.</summary>
    public void Refresh(float alpha)
    {
        var run = _race.Run;
        _time.Text = Seconds(run.TimeSeconds(alpha));
        _time.AddThemeColorOverride("font_color", run.Phase switch
        {
            PlayerPhase.Finished => UiTheme.Good,
            PlayerPhase.Crashed => UiTheme.Bad,
            _ => UiTheme.Text,
        });
        _progress.Text = (run.Progress * 100f).ToString("0", Inv) + " %";
        float speed = run.Phase == PlayerPhase.Driving ? MathF.Abs(run.Car.ForwardSpeed) : 0f;
        _speed.Text = speed.ToString("0.0", Inv) + " cells/s";
        _attempt.Text = run.Attempts.ToString(Inv);

        if (_race.Splits.Crossed != _shownCrossed || run.Attempts != _shownAttempt) RefreshSplits();

        // The big message only changes a few times per run.
        int count = run.Phase switch
        {
            PlayerPhase.Countdown => (run.CountdownLeft + RacingSettings.TicksPerSecond - 1) / RacingSettings.TicksPerSecond,
            PlayerPhase.Driving => run.Ticks < GoSeconds * RacingSettings.TicksPerSecond ? 0 : -1,
            _ => run.Attempts,
        };
        if (run.Phase == _shownPhase && count == _shownCount) return;
        _shownPhase = run.Phase;
        _shownCount = count;
        switch (run.Phase)
        {
            case PlayerPhase.Countdown:
                Show(count.ToString(Inv), UiTheme.Accent, "Get ready");
                break;
            case PlayerPhase.Driving:
                Show(count == 0 ? "GO!" : "", UiTheme.Good, "");
                break;
            case PlayerPhase.Crashed:
                Show("CRASHED", UiTheme.Bad, "R  restart     Esc  back");
                break;
            default:
                Show("", UiTheme.Good, "");
                break;
        }
    }

    private void RefreshSplits()
    {
        var splits = _race.Splits;
        _shownCrossed = splits.Crossed;
        _shownAttempt = _race.Run.Attempts;
        for (int s = 0; s < RaceSplits.Count; s++)
        {
            _you[s].Text = splits.PlayerTick(s) < 0 ? "–" : Seconds(splits.PlayerTick(s) / (float)RacingSettings.TicksPerSecond);
            for (int g = 0; g < _cells.Length; g++)
            {
                var delta = splits.Delta(s, g);
                Label cell = _cells[g][s];
                cell.Text = delta is not { } d ? "–" : !d.GhostReached ? "out" : d.Seconds.ToString("+0.00;-0.00;0.00", Inv);
                cell.AddThemeColorOverride("font_color", delta is not { } dd ? UiTheme.Muted : dd.Seconds == 0f && dd.GhostReached ? UiTheme.Text : dd.Ahead ? UiTheme.Good : UiTheme.Bad);
            }
        }
    }

    private void Show(string big, Color color, string small)
    {
        _big.Text = big;
        _big.AddThemeColorOverride("font_color", color);
        _small.Text = small;
    }

    private static string Seconds(float s) => s.ToString("0.00", Inv) + " s";
}
