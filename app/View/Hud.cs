using System;
using System.Globalization;
using Godot;
using Nitrogenesis.Sim.Core;
using Nitrogenesis.Sim.Evolution;
using Nitrogenesis.Sim.Racing;
using Nitrogenesis.Ui;

namespace Nitrogenesis.View;

/// <summary>
/// The training HUD (PLAN §6.2). Top-left: generation, alive / total, generation time / limit, total training time,
/// best progress this generation, best time ever (this segment), speed (target / actual), FPS, branch and segment,
/// what the camera follows, the "Boost active" / "Hint on" badges, optional debug rows (F3), and the controls: Pause /
/// Step, Mode Auto/Step, the speed slider, Next generation, Train to…, Replay best and the manual mutation slider.
/// Top-right: Menu, Training panel and the "saved" indicator. Bottom-right: the small graph (click to enlarge). Top
/// centre: a banner (stagnation boost, notices). Text is refreshed a few times per second, not every frame.
/// </summary>
public partial class Hud : CanvasLayer
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private const string Hints = "−/+ speed   Space pause   . step   N next generation   M mode   T train to   R replay best   H panel   G graph";
    private const string Hints2 = "click a car: follow it   F follow best   wheel zoom   drag / WASD pan   F3 debug   Esc menu      Gamepad: LB / RB speed   A pause   X step   Back menu";
    /// <summary>Manual mutation slider: a factor 2^v on the default σ and chance, v = −2 … 2 in quarter steps (×0.25 … ×4).</summary>
    public const float MutationMinLog = -2f, MutationMaxLog = 2f, MutationStep = 0.25f;

    private Label _title = null!, _status = null!, _boostBadge = null!, _hintBadge = null!;
    private Label _generation = null!, _alive = null!, _genTime = null!, _training = null!, _progress = null!, _bestTime = null!, _speed = null!, _fps = null!, _branch = null!, _camera = null!;
    private Label _simCost = null!, _renderCost = null!, _alloc = null!, _hudText = null!, _gc = null!;
    private Control _debugRows = null!, _graphBox = null!;
    private Button _pause = null!, _step = null!, _mode = null!, _nextGen = null!, _trainTo = null!, _replayBest = null!, _panel = null!;
    private HSlider _slider = null!, _mutation = null!;
    private Label _sliderLabel = null!, _mutationLabel = null!, _mutationNote = null!, _saved = null!;
    private PanelContainer _banner = null!;
    private Label _bannerText = null!;
    private double _bannerLeft;
    private int _mutationPendingUntil = -1;

    public string TrackName { get; set; } = "";
    /// <summary>The session is saved (a folder was given); otherwise the indicator says "not saved".</summary>
    public bool Saves { get; set; } = true;
    /// <summary>The mutation factor shown at the start (from the session's evolution settings).</summary>
    public float InitialMutationFactor { get; set; } = 1f;

    public GraphView Graph { get; } = new(300, false);

    public event Action? BackPressed, PausePressed, StepPressed, ModePressed, NextGenerationPressed, TrainToPressed, ReplayBestPressed, PanelPressed;
    /// <summary>The speed slider moved to a step index (0 … <see cref="SpeedController.MaxIndex"/>).</summary>
    public event Action<int>? SpeedPicked;
    /// <summary>The mutation slider moved: the new factor on σ and chance.</summary>
    public event Action<float>? MutationPicked;

    public bool DebugVisible
    {
        get => _debugRows.Visible;
        set => _debugRows.Visible = value;
    }

    /// <summary>The small graph is shown (hidden while the Training panel covers that corner).</summary>
    public bool GraphVisible
    {
        get => _graphBox.Visible;
        set => _graphBox.Visible = value;
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
        var titleRow = new HBoxContainer();
        titleRow.AddThemeConstantOverride("separation", 8);
        _title = UiTheme.Label(TrackName.ToUpperInvariant(), 20, UiTheme.Accent);
        titleRow.AddChild(_title);
        _boostBadge = Badge("BOOST ACTIVE", UiTheme.Accent);
        _boostBadge.TooltipText = "Stagnation: mutation σ and chance are doubled for 5 generations (PLAN §3.7)";
        titleRow.AddChild(_boostBadge);
        _hintBadge = Badge("HINT ON", UiTheme.Good);
        _hintBadge.TooltipText = "The direction hint input is on";
        titleRow.AddChild(_hintBadge);
        box.AddChild(titleRow);
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
        _branch = Row(grid, "Branch");
        _camera = Row(grid, "Camera");

        var debug = Grid();
        _debugRows = debug;
        box.AddChild(debug);
        _simCost = Row(debug, "Sim");
        _renderCost = Row(debug, "Render");
        _alloc = Row(debug, "Alloc");
        _hudText = Row(debug, "UI text");
        _gc = Row(debug, "GC");
        debug.Visible = false;

        box.AddChild(Controls());

        // Top-right: Menu, Training panel, saved indicator.
        var right = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        right.AddThemeConstantOverride("separation", 6);
        right.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopRight, Control.LayoutPresetMode.Minsize, 12);
        right.GrowHorizontal = Control.GrowDirection.Begin;
        root.AddChild(right);
        var menu = UiTheme.SmallButton("Menu  (Esc)", () => BackPressed?.Invoke());
        right.AddChild(menu);
        _panel = UiTheme.SmallButton("Training panel  (H)", () => PanelPressed?.Invoke());
        _panel.TooltipText = "History, branches and segments";
        right.AddChild(_panel);
        _saved = UiTheme.Label("", 13, UiTheme.Muted, HorizontalAlignment.Right);
        _saved.MouseFilter = Control.MouseFilterEnum.Pass;
        right.AddChild(_saved);

        // Bottom-right: the small graph.
        var graphPanel = new PanelContainer();
        graphPanel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomRight, Control.LayoutPresetMode.Minsize, 12);
        graphPanel.GrowHorizontal = Control.GrowDirection.Begin;
        graphPanel.GrowVertical = Control.GrowDirection.Begin;
        graphPanel.TooltipText = "Click to enlarge (G)";
        var graphColumn = new VBoxContainer();
        graphColumn.AddThemeConstantOverride("separation", 4);
        graphPanel.AddChild(graphColumn);
        var graphHead = new HBoxContainer();
        graphHead.AddChild(UiTheme.Label("Score per generation", 13, UiTheme.Muted));
        graphHead.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        graphHead.AddChild(UiTheme.Label("click: enlarge", 12, UiTheme.Muted.Darkened(0.25f)));
        graphColumn.AddChild(graphHead);
        Graph.CustomMinimumSize = new Vector2(300, 110);
        Graph.TooltipText = "Click to enlarge (G)";
        graphColumn.AddChild(Graph);
        graphColumn.AddChild(GraphView.Legend(12));
        _graphBox = graphPanel;
        root.AddChild(graphPanel);

        // Top centre: the banner.
        _banner = new PanelContainer { Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        _banner.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterTop, Control.LayoutPresetMode.Minsize, 14);
        _banner.GrowHorizontal = Control.GrowDirection.Both;
        _bannerText = UiTheme.Label("", 16, UiTheme.Accent, HorizontalAlignment.Center);
        // Fixed width with word wrap: a long message (a save error with its path) wraps instead of covering the HUD
        // panel and the Menu / Training panel buttons.
        _bannerText.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _bannerText.CustomMinimumSize = new Vector2(520, 0);
        _banner.AddChild(_bannerText);
        root.AddChild(_banner);

        var hints = new VBoxContainer();
        hints.AddThemeConstantOverride("separation", 2);
        hints.AddChild(UiTheme.Label(Hints, 13, UiTheme.Muted));
        hints.AddChild(UiTheme.Label(Hints2, 13, UiTheme.Muted));
        hints.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomLeft, Control.LayoutPresetMode.Minsize, 14);
        hints.GrowVertical = Control.GrowDirection.Begin;
        hints.MouseFilter = Control.MouseFilterEnum.Ignore;
        root.AddChild(hints);
    }

    /// <summary>
    /// Pause / Step / Mode, the speed slider (13 steps, 0.01× … MAX), Next generation / Train to… / Replay best and the
    /// mutation slider. None of them takes keyboard focus, so Space and the other keys keep reaching the view; the
    /// sliders ignore the wheel (which zooms).
    /// </summary>
    private Control Controls()
    {
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 6);

        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 8);
        _pause = UiTheme.SmallButton("Pause", () => PausePressed?.Invoke());
        _pause.CustomMinimumSize = new Vector2(96, 0);
        _step = UiTheme.SmallButton("Step", () => StepPressed?.Invoke());
        _step.Disabled = true;
        _step.TooltipText = "One tick (while paused)";
        _mode = UiTheme.SmallButton("Mode: Auto", () => ModePressed?.Invoke());
        _mode.CustomMinimumSize = new Vector2(128, 0);
        _mode.TooltipText = "Auto: the next generation starts at once.\nStep: pause at the end of each generation with a summary (M)";
        buttons.AddChild(_pause);
        buttons.AddChild(_step);
        buttons.AddChild(_mode);
        box.AddChild(buttons);

        var speed = new HBoxContainer();
        speed.AddThemeConstantOverride("separation", 10);
        _slider = Slider(0, SpeedController.MaxIndex, 1, SpeedController.DefaultIndex, "Speed");
        _slider.TickCount = SpeedController.MaxIndex + 1;
        _slider.TicksOnBorders = true;
        _slider.ValueChanged += v => SpeedPicked?.Invoke((int)Math.Round(v));
        speed.AddChild(_slider);
        _sliderLabel = UiTheme.Label(SpeedController.Label(1), 15, UiTheme.Text);
        _sliderLabel.CustomMinimumSize = new Vector2(48, 0);
        speed.AddChild(_sliderLabel);
        box.AddChild(speed);

        var actions = new HBoxContainer();
        actions.AddThemeConstantOverride("separation", 8);
        _nextGen = UiTheme.SmallButton("Next gen", () => NextGenerationPressed?.Invoke());
        _nextGen.TooltipText = "End the running generation now and score the cars where they are (N)";
        _trainTo = UiTheme.SmallButton("Train to…", () => TrainToPressed?.Invoke());
        _trainTo.TooltipText = "Train without visuals until generation N, a finish, or a best time (T)";
        _replayBest = UiTheme.SmallButton("Replay best", () => ReplayBestPressed?.Invoke());
        _replayBest.TooltipText = "Watch the best car of the last generation (R)";
        actions.AddChild(_nextGen);
        actions.AddChild(_trainTo);
        actions.AddChild(_replayBest);
        box.AddChild(actions);

        var mutation = new HBoxContainer();
        mutation.AddThemeConstantOverride("separation", 10);
        mutation.AddChild(UiTheme.Label("Mutation", 15, UiTheme.Muted));
        float initial = MathF.Round(MathF.Log2(Math.Clamp(InitialMutationFactor, 0.25f, 4f)) / MutationStep) * MutationStep;
        _mutation = Slider(MutationMinLog, MutationMaxLog, MutationStep, initial, "");
        _mutation.CustomMinimumSize = new Vector2(120, 0);
        _mutation.ValueChanged += v =>
        {
            float factor = MathF.Pow(2f, (float)v);
            UpdateMutationLabel(factor);
            _mutationPendingUntil = int.MaxValue; // set by the next Refresh to the generation it applies from
            MutationPicked?.Invoke(factor);
        };
        mutation.AddChild(_mutation);
        _mutationLabel = UiTheme.Label("", 15, UiTheme.Text);
        _mutationLabel.CustomMinimumSize = new Vector2(48, 0);
        mutation.AddChild(_mutationLabel);
        box.AddChild(mutation);
        _mutationNote = UiTheme.Label("", 13, UiTheme.Accent);
        _mutationNote.Visible = false;
        box.AddChild(_mutationNote);
        UpdateMutationLabel(MathF.Pow(2f, initial));
        return box;
    }

    /// <summary>Moves the mutation slider as a click would (debug <c>mutation</c> step).</summary>
    public void SetMutationFromScript(float factor) => _mutation.Value = MathF.Round(MathF.Log2(factor) / MutationStep) * MutationStep;

    private void UpdateMutationLabel(float factor)
    {
        var d = new EvolutionSettings();
        _mutationLabel.Text = "×" + factor.ToString(factor < 1 ? "0.##" : "0.0#", Inv);
        _mutation.TooltipText = $"Manual mutation: σ {Math.Clamp(d.MutationSigma * factor, EvolutionSettings.MinMutationSigma, EvolutionSettings.MaxMutationSigma):0.00##} · " +
                                $"chance {Math.Min(1f, d.MutationChance * factor) * 100:0.#} % per weight. Applies at the next generation boundary.";
    }

    private static HSlider Slider(double min, double max, double step, double value, string tooltip) => new()
    {
        MinValue = min,
        MaxValue = max,
        Step = step,
        Value = value,
        Scrollable = false,
        FocusMode = Control.FocusModeEnum.None,
        CustomMinimumSize = new Vector2(190, 0),
        SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        TooltipText = tooltip,
    };

    private static Label Badge(string text, Color color)
    {
        var label = UiTheme.Label(text, 12, UiTheme.Background);
        var style = new StyleBoxFlat { BgColor = color, ContentMarginLeft = 6, ContentMarginRight = 6, ContentMarginTop = 1, ContentMarginBottom = 1 };
        style.SetCornerRadiusAll(3);
        label.AddThemeStyleboxOverride("normal", style);
        label.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        label.MouseFilter = Control.MouseFilterEnum.Pass;
        label.Visible = false;
        return label;
    }

    /// <summary>A small "Menu" button in the top-right corner.</summary>
    public static Button MenuButton(Action pressed, string text = "Menu  (Esc)")
    {
        var back = UiTheme.SmallButton(text, pressed);
        back.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopRight, Control.LayoutPresetMode.Minsize, 12);
        back.GrowHorizontal = Control.GrowDirection.Begin;
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

    /// <summary>Shows a banner at the top for <paramref name="seconds"/>.</summary>
    public void ShowBanner(string text, Color color, double seconds = 6)
    {
        _bannerText.Text = text;
        _bannerText.AddThemeColorOverride("font_color", color);
        _banner.Visible = true;
        _bannerLeft = seconds;
    }

    /// <summary>Counts the banner down (every frame).</summary>
    public void Tick(double delta)
    {
        if (_bannerLeft <= 0) return;
        _bannerLeft -= delta;
        if (_bannerLeft <= 0) _banner.Visible = false;
    }

    /// <summary>Refreshes the text (call a few times per second).</summary>
    public void Refresh(in TrainingStats s, double fps, double renderMs, string camera, FrameAllocMeter alloc, long hudTextBytes, bool hintOn)
    {
        _title.Text = TrackName.ToUpperInvariant();
        _boostBadge.Visible = s.Ready && s.BoostActive;
        _hintBadge.Visible = hintOn;
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
            _status.Text = s.WaitingForNext ? $"Generation {s.LastGeneration} ended  ( Enter = next )"
                : s.Busy is not null ? "Busy…"
                : s.Paused ? "Paused  ( . = one tick )" : "Running";
            _status.AddThemeColorOverride("font_color", s.Paused || s.WaitingForNext ? UiTheme.Accent : UiTheme.Muted);
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
        _branch.Text = s.Branch is null ? "—" : $"{s.Branch} · segment {s.Segment}";
        _pause.Text = s.Paused ? "Resume" : "Pause";
        _step.Disabled = !s.Paused || !s.Ready;
        _mode.Text = s.StepMode ? "Mode: Step" : "Mode: Auto";
        bool idle = s.Ready && s.Error is null && s.Busy is null;
        _nextGen.Disabled = !idle;
        _trainTo.Disabled = !idle;
        _replayBest.Disabled = !idle || s.Generation <= 0;
        _panel.Disabled = !s.Ready;
        _slider.SetValueNoSignal(s.SpeedIndex);
        _sliderLabel.Text = target;
        _camera.Text = camera;

        if (_mutationPendingUntil == int.MaxValue) _mutationPendingUntil = s.Generation + 1;
        bool pending = s.Generation < _mutationPendingUntil;
        _mutationNote.Visible = pending;
        if (pending) _mutationNote.Text = $"applies from generation {_mutationPendingUntil}";

        if (!Saves) _saved.Text = "not saved";
        else if (s.SaveError is not null)
        {
            _saved.Text = "save failed · training goes on";
            _saved.TooltipText = s.SaveError;
            _saved.AddThemeColorOverride("font_color", UiTheme.Bad);
        }
        else if (s.Saving)
        {
            _saved.Text = "saving…";
            _saved.AddThemeColorOverride("font_color", UiTheme.Accent);
        }
        else if (s.SavedAtUtc != default)
        {
            DateTime local = s.SavedAtUtc.ToLocalTime();
            _saved.Text = "saved " + local.ToString(local.Date == DateTime.Today ? "HH:mm" : "yyyy-MM-dd HH:mm", Inv);
            _saved.TooltipText = "Saved automatically every 2 minutes and when you leave";
            _saved.AddThemeColorOverride("font_color", UiTheme.Muted);
        }

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

    public static string Clock(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Floor(seconds));
        return $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";
    }
}
