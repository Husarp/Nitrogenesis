using System;
using System.Globalization;
using Godot;
using Nitrogenesis.Sim.Core;
using Nitrogenesis.Sim.Racing;
using Nitrogenesis.Ui;

namespace Nitrogenesis.View;

/// <summary>
/// The training HUD, top-left (PLAN §6.2): generation, alive / total, generation time / limit, total training time,
/// best progress this generation, best time ever, speed (target / actual), FPS, and what the camera follows.
/// Optional debug rows (F3) show the frame's simulation and render cost and the main thread's allocations per frame
/// with the garbage collections so far. Below the rows: Pause / Step buttons and the speed slider (PLAN §6.2), the
/// mouse twins of Space, "." and − / +. Text is refreshed a few times per second, not every frame.
/// </summary>
public partial class Hud : CanvasLayer
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private const string Hints = "−/+ speed   Space pause   . step   click a car: follow it   F follow best   wheel zoom   drag / WASD pan   F3 debug   Esc menu";
    private const string PadHints = "Gamepad:  LB / RB speed   A pause   X step   Back menu";

    private Label _title = null!, _status = null!;
    private Label _generation = null!, _alive = null!, _genTime = null!, _training = null!, _progress = null!, _bestTime = null!, _speed = null!, _fps = null!, _camera = null!;
    private Label _simCost = null!, _renderCost = null!, _alloc = null!, _hudText = null!, _gc = null!;
    private Control _debugRows = null!;
    private Button _pause = null!, _step = null!;
    private HSlider _slider = null!;
    private Label _sliderLabel = null!;

    public string TrackName { get; set; } = "";

    /// <summary>The "Menu" button was pressed (same as Esc).</summary>
    public event Action? BackPressed;
    /// <summary>The Pause / Resume button (same as Space).</summary>
    public event Action? PausePressed;
    /// <summary>The Step button (same as "."; enabled while paused).</summary>
    public event Action? StepPressed;
    /// <summary>The speed slider moved to a step index (0 … <see cref="SpeedController.MaxIndex"/>).</summary>
    public event Action<int>? SpeedPicked;

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
        _status = UiTheme.Label("Preparing the track…", 15, UiTheme.Muted);
        box.AddChild(_status);

        var grid = Grid();
        box.AddChild(grid);
        _generation = Row(grid, "Generation");
        _alive = Row(grid, "Alive");
        _genTime = Row(grid, "Gen time");
        _training = Row(grid, "Training");
        _progress = Row(grid, "Best now");
        _bestTime = Row(grid, "Best time");
        _speed = Row(grid, "Speed");
        _fps = Row(grid, "FPS");
        _camera = Row(grid, "Camera");

        var debug = Grid();
        _debugRows = debug;
        box.AddChild(debug);
        _simCost = Row(debug, "Sim");
        _renderCost = Row(debug, "Render");
        _alloc = Row(debug, "Alloc");
        _hudText = Row(debug, "HUD text");
        _gc = Row(debug, "GC");
        debug.Visible = false;

        box.AddChild(Controls());

        root.AddChild(MenuButton(() => BackPressed?.Invoke()));

        var hints = new VBoxContainer();
        hints.AddThemeConstantOverride("separation", 2);
        hints.AddChild(UiTheme.Label(Hints, 14, UiTheme.Muted));
        hints.AddChild(UiTheme.Label(PadHints, 14, UiTheme.Muted));
        hints.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomLeft, Control.LayoutPresetMode.Minsize, 14);
        hints.GrowVertical = Control.GrowDirection.Begin;
        hints.MouseFilter = Control.MouseFilterEnum.Ignore;
        root.AddChild(hints);
    }

    /// <summary>
    /// Pause / Step and the speed slider (13 steps, 0.01× … MAX). None of them takes keyboard focus, so Space and "."
    /// keep reaching the view; the slider ignores the wheel (which zooms).
    /// </summary>
    private Control Controls()
    {
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 6);
        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 8);
        _pause = SmallButton("Pause", () => PausePressed?.Invoke());
        _pause.CustomMinimumSize = new Vector2(96, 0);
        _step = SmallButton("Step", () => StepPressed?.Invoke());
        _step.Disabled = true;
        _step.TooltipText = "One tick (while paused)";
        buttons.AddChild(_pause);
        buttons.AddChild(_step);
        box.AddChild(buttons);

        var speed = new HBoxContainer();
        speed.AddThemeConstantOverride("separation", 10);
        _slider = new HSlider
        {
            MinValue = 0,
            MaxValue = SpeedController.MaxIndex,
            Step = 1,
            Value = SpeedController.DefaultIndex,
            TickCount = SpeedController.MaxIndex + 1,
            TicksOnBorders = true,
            Scrollable = false,
            FocusMode = Control.FocusModeEnum.None,
            CustomMinimumSize = new Vector2(190, 0),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            TooltipText = "Speed",
        };
        _slider.ValueChanged += v => SpeedPicked?.Invoke((int)Math.Round(v));
        speed.AddChild(_slider);
        _sliderLabel = UiTheme.Label(SpeedController.Label(1), 15, UiTheme.Text);
        _sliderLabel.CustomMinimumSize = new Vector2(48, 0);
        speed.AddChild(_sliderLabel);
        box.AddChild(speed);
        return box;
    }

    private static Button SmallButton(string text, Action pressed)
    {
        var button = new Button { Text = text, FocusMode = Control.FocusModeEnum.None };
        button.AddThemeFontSizeOverride("font_size", 15);
        button.Pressed += pressed;
        return button;
    }

    /// <summary>A small "Menu" button in the top-right corner.</summary>
    public static Button MenuButton(Action pressed)
    {
        var back = new Button { Text = "Menu  (Esc)", FocusMode = Control.FocusModeEnum.None };
        back.AddThemeFontSizeOverride("font_size", 15);
        back.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopRight, Control.LayoutPresetMode.Minsize, 12);
        back.GrowHorizontal = Control.GrowDirection.Begin;
        back.Pressed += pressed;
        return back;
    }

    private static GridContainer Grid()
    {
        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 18);
        grid.AddThemeConstantOverride("v_separation", 2);
        return grid;
    }

    private static Label Row(GridContainer grid, string name)
    {
        grid.AddChild(UiTheme.Label(name, 15, UiTheme.Muted));
        var value = UiTheme.Label("—", 15, UiTheme.Text);
        value.CustomMinimumSize = new Vector2(150, 0);
        grid.AddChild(value);
        return value;
    }

    /// <summary>Refreshes the text (call a few times per second).</summary>
    public void Refresh(in TrainingStats s, double fps, double renderMs, string camera, FrameAllocMeter alloc, long hudTextBytes)
    {
        _title.Text = TrackName.ToUpperInvariant();
        if (s.Error is not null)
        {
            _status.Text = "Simulation stopped: " + s.Error;
            _status.AddThemeColorOverride("font_color", UiTheme.Bad);
            _status.Visible = true;
        }
        else if (!s.Ready)
        {
            _status.Text = "Preparing the track…";
            _status.Visible = true;
        }
        else
        {
            // Always one line, so pausing does not move the buttons under the cursor.
            _status.Visible = true;
            _status.Text = s.Paused ? "Paused  ( . = one tick )" : "Running";
            _status.AddThemeColorOverride("font_color", s.Paused ? UiTheme.Accent : UiTheme.Muted);
        }

        _generation.Text = s.Generation.ToString(Inv);
        _alive.Text = $"{s.Alive} / {s.PopulationSize}";
        _genTime.Text = $"{Seconds(s.ElapsedTicks)} / {Seconds(s.TimeLimitTicks)} s";
        _training.Text = Clock(s.TrainingSeconds);
        _progress.Text = (s.BestProgress * 100f).ToString("0", Inv) + " %";
        _bestTime.Text = float.IsFinite(s.BestTimeTicks) ? (s.BestTimeTicks / RacingSettings.TicksPerSecond).ToString("0.00", Inv) + " s" : "—";

        string target = SpeedController.Label(s.TargetSpeed);
        bool behind = !s.Paused && s.Ready && (double.IsPositiveInfinity(s.TargetSpeed) || s.ActualSpeed < 0.9 * s.TargetSpeed) && s.ActualSpeed > 0;
        _speed.Text = s.Paused ? $"paused ({target})"
            : behind ? $"{target} (actual {SpeedController.Label(Round(s.ActualSpeed))})"
            : target;
        _speed.AddThemeColorOverride("font_color", behind && !double.IsPositiveInfinity(s.TargetSpeed) ? UiTheme.Accent : UiTheme.Text);
        _fps.Text = $"{fps:0} (cap {Engine.MaxFps})";
        _pause.Text = s.Paused ? "Resume" : "Pause";
        _step.Disabled = !s.Paused || !s.Ready;
        _slider.SetValueNoSignal(s.SpeedIndex);
        _sliderLabel.Text = target;
        _camera.Text = camera;

        long maxFrame = alloc.TakeMax();
        if (_debugRows.Visible)
        {
            _simCost.Text = $"{s.SimSecondsLastFrame * 1000:0.0} ms, {s.TicksLastFrame} ticks, {s.Threads} threads";
            _renderCost.Text = $"{renderMs:0.00} ms";
            _alloc.Text = $"{maxFrame} B / frame";
            _alloc.AddThemeColorOverride("font_color", maxFrame == 0 ? UiTheme.Good : UiTheme.Bad);
            _hudText.Text = $"{hudTextBytes} B × 10/s (not counted)";
            _gc.Text = $"gen0 {alloc.Gen0} · gen1 {alloc.Gen1} · gen2 {alloc.Gen2} (since start)";
        }
    }

    private static double Round(double speed) => speed >= 10 ? Math.Round(speed) : Math.Round(speed, speed >= 1 ? 1 : 2);

    private static string Seconds(long ticks) => (ticks / (double)RacingSettings.TicksPerSecond).ToString("0.0", Inv);

    private static string Clock(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Floor(seconds));
        return $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";
    }
}
