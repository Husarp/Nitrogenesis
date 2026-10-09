using System;
using System.Globalization;
using Godot;
using Nitrogenesis.Sim.Racing;
using Nitrogenesis.Ui;

namespace Nitrogenesis.View;

/// <summary>
/// The HUD while the player drives (PLAN §6.5, M2 part): track, live timer, best time, progress, speed and the
/// attempt in a panel top-left; the countdown, the finish time or the crash in big letters in the middle.
/// Optional debug rows (F3): allocations per frame and garbage collections.
/// </summary>
public partial class DriveHud : CanvasLayer
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private const string Hints = "W/S or ↑/↓ throttle · brake   A/D or ←/→ steer   gamepad: left stick + triggers   R restart   wheel zoom   F3 debug   Esc menu";
    /// <summary>"GO!" stays this long after the start signal, s.</summary>
    private const float GoSeconds = 0.7f;

    private Label _title = null!, _time = null!, _best = null!, _progress = null!, _speed = null!, _attempt = null!;
    private Label _big = null!, _small = null!, _alloc = null!, _gc = null!;
    private Control _debugRows = null!;
    private PlayerPhase _shownPhase = (PlayerPhase)255;
    private int _shownCount = -1;

    public string TrackName { get; set; } = "";

    public event Action? BackPressed;

    public bool DebugVisible
    {
        get => _debugRows.Visible;
        set => _debugRows.Visible = value;
    }

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
        _title = UiTheme.Label(TrackName.ToUpperInvariant(), 20, UiTheme.Accent);
        box.AddChild(_title);
        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 18);
        grid.AddThemeConstantOverride("v_separation", 2);
        box.AddChild(grid);
        _time = Row(grid, "Time", 22);
        _best = Row(grid, "Best", 15);
        _progress = Row(grid, "Progress", 15);
        _speed = Row(grid, "Speed", 15);
        _attempt = Row(grid, "Attempt", 15);

        var debug = new GridContainer { Columns = 2, Visible = false };
        debug.AddThemeConstantOverride("h_separation", 18);
        _debugRows = debug;
        box.AddChild(debug);
        _alloc = Row(debug, "Alloc", 15);
        _gc = Row(debug, "GC", 15);

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

        root.AddChild(Hud.MenuButton(() => BackPressed?.Invoke()));
    }

    private static Label Row(GridContainer grid, string name, int size)
    {
        grid.AddChild(UiTheme.Label(name, 15, UiTheme.Muted));
        var value = UiTheme.Label("—", size, UiTheme.Text);
        value.CustomMinimumSize = new Vector2(150, 0);
        grid.AddChild(value);
        return value;
    }

    /// <summary>Refreshes the text (every frame: the timer runs). Allocates a few short strings; the view excludes them from its allocation check.</summary>
    public void Refresh(PlayerRun run, float alpha, FrameAllocMeter alloc)
    {
        _title.Text = TrackName.ToUpperInvariant();
        _time.Text = Seconds(run.TimeSeconds(alpha));
        _time.AddThemeColorOverride("font_color", run.Phase switch
        {
            PlayerPhase.Finished => UiTheme.Good,
            PlayerPhase.Crashed => UiTheme.Bad,
            _ => UiTheme.Text,
        });
        _best.Text = float.IsFinite(run.BestFinishTicks) ? Seconds(run.BestFinishTicks / RacingSettings.TicksPerSecond) : "—";
        _progress.Text = (run.Progress * 100f).ToString("0", Inv) + " %";
        float speed = run.Phase == PlayerPhase.Driving ? MathF.Abs(run.Car.ForwardSpeed) : 0f;
        _speed.Text = speed.ToString("0.0", Inv) + " cells/s";
        _attempt.Text = run.Attempts.ToString(Inv);

        // The big message only changes a few times per run.
        int count = run.Phase switch
        {
            PlayerPhase.Countdown => (run.CountdownLeft + RacingSettings.TicksPerSecond - 1) / RacingSettings.TicksPerSecond,
            PlayerPhase.Driving => run.Ticks < GoSeconds * RacingSettings.TicksPerSecond ? 0 : -1,
            _ => run.Attempts,
        };
        if (run.Phase != _shownPhase || count != _shownCount)
        {
            _shownPhase = run.Phase;
            _shownCount = count;
            ShowMessage(run, count);
        }

        if (_debugRows.Visible)
        {
            long max = alloc.TakeMax();
            _alloc.Text = $"{max} B / frame (HUD text apart)";
            _alloc.AddThemeColorOverride("font_color", max == 0 ? UiTheme.Good : UiTheme.Bad);
            _gc.Text = $"gen0 {alloc.Gen0} · gen1 {alloc.Gen1} · gen2 {alloc.Gen2}";
        }
    }

    private void ShowMessage(PlayerRun run, int count)
    {
        switch (run.Phase)
        {
            case PlayerPhase.Countdown:
                Show(count.ToString(Inv), UiTheme.Accent, "Get ready");
                break;
            case PlayerPhase.Driving:
                Show(count == 0 ? "GO!" : "", UiTheme.Good, "");
                break;
            case PlayerPhase.Finished:
                Show(Seconds(run.FinishTicks / RacingSettings.TicksPerSecond), UiTheme.Good,
                    (run.NewBest && run.Finishes > 1 ? "FINISHED · new best!" : "FINISHED") + "\nR  restart     Esc  menu");
                break;
            case PlayerPhase.Crashed:
                Show("CRASHED", UiTheme.Bad, "R  restart     Esc  menu");
                break;
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
