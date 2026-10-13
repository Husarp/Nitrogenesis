using System;
using System.Diagnostics;
using System.Globalization;
using Godot;
using Nitrogenesis.Sim.Brain;
using Nitrogenesis.Sim.Core;
using Nitrogenesis.Sim.Evolution;
using Nitrogenesis.Sim.History;
using Nitrogenesis.Sim.Map;
using Nitrogenesis.Sim.Racing;
using Nitrogenesis.Ui;

namespace Nitrogenesis.View;

/// <summary>
/// The training view (PLAN §6.2): the map texture, all cars in one MultiMesh, the camera and the HUD over a
/// <see cref="TrainingHost"/> that trains on its own threads, plus the M3 training control: Mode Auto/Step with the
/// end-of-generation summary, Next generation, Train to… (without visuals, on <see cref="ProgressScreen"/>), Replay best,
/// the graph (small; click to enlarge), the stagnation banner, the manual mutation slider, the Training panel
/// (history, branches, segments; Watch best / Watch whole generation / Fork) and the replay view.
/// </summary>
/// <remarks>
/// Per frame (live): <see cref="TrainingHost.Frame"/> (adds game time, wakes the sim) → at 1× and slower a short wait
/// for that frame's tick → copy the latest snapshot pair → interpolate (PLAN §4) → fill and push the MultiMesh buffer →
/// the followed car's route and rays → the graph data when it changed (copied from the host into fixed buffers). None
/// of it allocates; text is refreshed 10 times per second outside that path (the "UI text" debug row). The view owns
/// the host: freeing the view stops the simulation and saves the session one last time.
/// </remarks>
public partial class TrainingView : Node2D
{
    private enum State { Live, Progress, Replay }

    /// <summary>Longest the render thread waits for the frame's tick at 1× and slower.</summary>
    private static readonly TimeSpan SlowSpeedWait = TimeSpan.FromMilliseconds(4);
    private const double HudInterval = 0.1;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>"Keep training while watching" (remembered while the app runs).</summary>
    private static bool s_keepTrainingWhileWatching;

    private Track _track = null!;
    private TrainingHost _host = null!;
    private TrainingHostOptions _options = null!;
    private ulong _colorSeed;
    private bool _saves;
    private int _population;

    private Sprite2D _map = null!;
    private CarRenderer _cars = null!;
    private RouteOverlay _route = null!;
    private ViewCamera _camera = null!;
    private Hud _hud = null!;
    private CanvasLayer _ui = null!;
    private TrainingPanel? _panel;
    private StepSummary _summary = null!;
    private PanelContainer _busy = null!;
    private Label _busyText = null!;
    private ProgressBar _busyBar = null!;
    private Control _bigGraphLayer = null!;
    private GraphView _bigGraph = null!;
    private ProgressScreen _progress = null!;
    private ReplayView? _replay;

    private readonly FrameAllocMeter _alloc = new();
    private long _hudTextBytes;
    private int _selected = -1, _selectedGeneration = -1;
    private int _focus = -1;

    private AgentSnapshot _prev = null!, _curr = null!;
    private float[] _x = null!, _y = null!, _angle = null!;
    private float[] _lastX = null!, _lastY = null!;
    private TrainingStats _stats;
    private double _hudTimer, _renderMs;
    private bool _backRequested, _started;

    private State _state = State.Live;
    private int _trainToRunsAtStart, _savedMaxFps = 60;
    private bool _pausedBeforeReplay, _backgroundTraining, _panelBeforeHiding;
    private int _lastNotice, _lastBoost = -1;
    private string? _lastSaveError;

    /// <summary>Raised when the user leaves the view (Esc or the Menu button). The owner frees the view, which stops the simulation.</summary>
    public event Action? BackRequested;

    public TrainingStats Stats => _stats;
    public double RenderMs => _renderMs;
    public FrameAllocMeter Alloc => _alloc;
    public ViewCamera Camera => _camera;
    public int Selected => _selected;
    public TrainingHost Host => _host;

    /// <summary>The view over <paramref name="host"/> (the view owns it from now on).</summary>
    /// <param name="host">A new or resumed training.</param>
    /// <param name="track">Its track (the map drawn).</param>
    /// <param name="options">What the host was made with (threads for replays).</param>
    /// <param name="colorSeed">Seed of the car colours.</param>
    /// <param name="saves">The session is saved (shown by the "saved" indicator).</param>
    public static TrainingView Create(TrainingHost host, Track track, TrainingHostOptions options, ulong colorSeed, bool saves) =>
        new() { _host = host, _track = track, _options = options, _colorSeed = colorSeed, _saves = saves };

    public override void _Ready()
    {
        RenderingServer.SetDefaultClearColor(MapTexture.Outside);
        _map = new Sprite2D { Texture = MapTexture.Create(_track.Grid, _track.Start), Centered = false, TextureFilter = TextureFilterEnum.Nearest };
        AddChild(_map);

        _route = new RouteOverlay();
        AddChild(_route);

        _population = _host.PopulationSize;
        int n = _population;
        _cars = new CarRenderer();
        _cars.Setup(n, _colorSeed, new Vector2(_track.Grid.Width, _track.Grid.Height));
        AddChild(_cars);

        _camera = new ViewCamera { Position = new Vector2(_track.Start.X, _track.Start.Y) };
        AddChild(_camera);
        _camera.MakeCurrent();
        _camera.Clicked += PickCar;

        float factor = _host.Session?.Evolution is { } evo ? evo.MutationSigma / new EvolutionSettings().MutationSigma : 1f;
        _hud = new Hud { TrackName = _track.Name, Saves = _saves, InitialMutationFactor = factor };
        _hud.BackPressed += Back;
        _hud.PausePressed += () => Changed(_host.TogglePause);
        _hud.StepPressed += () => Changed(_host.StepTick);
        _hud.SpeedPicked += index => Changed(() => _host.SetSpeed(SpeedController.StepValue(index)));
        _hud.ModePressed += () => Changed(ToggleMode);
        _hud.NextGenerationPressed += () => Changed(_host.EndGenerationNow);
        _hud.TrainToPressed += OpenTrainToDialog;
        _hud.ReplayBestPressed += ReplayLastBest;
        _hud.PanelPressed += TogglePanel;
        _hud.MutationPicked += SetMutation;
        AddChild(_hud);
        _hud.Graph.Clicked += () => ShowBigGraph(true);

        BuildUi();

        _prev = new AgentSnapshot(n);
        _curr = new AgentSnapshot(n);
        _x = new float[n];
        _y = new float[n];
        _angle = new float[n];
        _lastX = new float[n];
        _lastY = new float[n];
    }

    private void BuildUi()
    {
        _ui = new CanvasLayer { Layer = 2 };
        AddChild(_ui);
        var root = new Control { Theme = UiTheme.Theme, MouseFilter = Control.MouseFilterEnum.Ignore };
        root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _ui.AddChild(root);

        // Step mode: the summary near the bottom centre.
        _summary = new StepSummary { Visible = false };
        _summary.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterBottom, Control.LayoutPresetMode.Minsize, 64);
        _summary.GrowHorizontal = Control.GrowDirection.Both;
        _summary.GrowVertical = Control.GrowDirection.Begin;
        _summary.NextPressed += () => Changed(_host.Next);
        _summary.AutoPressed += () => Changed(() =>
        {
            _host.StepMode = false;
            _host.Next();
        });
        root.AddChild(_summary);

        // A rebuild for a fork or a branch switch.
        var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        center.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        root.AddChild(center);
        _busy = new PanelContainer { Visible = false };
        center.AddChild(_busy);
        var busyBox = new VBoxContainer();
        busyBox.AddThemeConstantOverride("separation", 10);
        _busy.AddChild(busyBox);
        _busyText = UiTheme.Label("", 17, UiTheme.Text);
        busyBox.AddChild(_busyText);
        _busyBar = new ProgressBar { MinValue = 0, MaxValue = 1, CustomMinimumSize = new Vector2(420, 20) };
        busyBox.AddChild(_busyBar);
        var cancel = UiTheme.SmallButton("Cancel", _host.CancelBusy);
        cancel.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;
        busyBox.AddChild(cancel);

        // The enlarged graph.
        _bigGraphLayer = new Control { Visible = false, MouseFilter = Control.MouseFilterEnum.Stop };
        _bigGraphLayer.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.55f), MouseFilter = Control.MouseFilterEnum.Ignore };
        dim.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _bigGraphLayer.AddChild(dim);
        var bigPanel = new PanelContainer();
        bigPanel.AddThemeStyleboxOverride("panel", UiTheme.SolidPanel());
        bigPanel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect, Control.LayoutPresetMode.KeepSize, 48);
        _bigGraphLayer.AddChild(bigPanel);
        var bigColumn = new VBoxContainer();
        bigColumn.AddThemeConstantOverride("separation", 8);
        bigPanel.AddChild(bigColumn);
        var head = new HBoxContainer();
        head.AddChild(UiTheme.Label("SCORE PER GENERATION", 20, UiTheme.Accent));
        head.AddChild(new Control { CustomMinimumSize = new Vector2(24, 0) });
        head.AddChild(GraphView.Legend(14));
        head.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        head.AddChild(UiTheme.SmallButton("Close  (G)", () => ShowBigGraph(false), 14));
        bigColumn.AddChild(head);
        bigColumn.AddChild(UiTheme.Label("Scores of 2 and more mean the car finished (2 + the share of the time limit left). Dashed orange lines: a new segment starts (best time and stagnation restart there).", 13, UiTheme.Muted));
        _bigGraph = new GraphView(1200, true) { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        _bigGraph.Clicked += () => ShowBigGraph(false);
        bigColumn.AddChild(_bigGraph);
        root.AddChild(_bigGraphLayer);

        // Training without visuals.
        _progress = new ProgressScreen { Visible = false };
        _progress.CancelPressed += _host.CancelTrainTo;
        root.AddChild(_progress);
    }

    /// <summary>
    /// Stops the simulation and saves the session one last time, waiting for the save (the owner shows "Saving the
    /// session…" first). The view stays alive afterwards, frozen, so a failed save can be reported and retried before
    /// the view is freed (<see cref="ExitSaveError"/>, <see cref="RetryExitSave"/>).
    /// </summary>
    public void StopAndSave()
    {
        if (_state == State.Progress) LeaveProgress();
        _host.Stop();
        if (OS.IsDebugBuild())
        {
            _host.ReadStats(out var s);
            GD.Print($"exit save: generation={s.Generation} branch={s.Branch} error={_host.ExitSaveError ?? "-"} saved={s.SavedAtUtc:O}");
        }
    }

    /// <summary>Why the last save on leaving failed, or null (the previous save on disk is intact either way).</summary>
    public string? ExitSaveError => _host.ExitSaveError;

    /// <summary>When the last save that worked was made, or null when none did.</summary>
    public DateTime? LastSavedLocal
    {
        get
        {
            _host.ReadStats(out var s);
            return s.SavedAtUtc == default ? null : s.SavedAtUtc.ToLocalTime();
        }
    }

    /// <summary>Tries the save on leaving again; true when it worked.</summary>
    public bool RetryExitSave() => _host.RetryExitSave();

    /// <summary>Stops the simulation threads and saves the session one last time; the view is being freed.</summary>
    public override void _ExitTree()
    {
        long start = Stopwatch.GetTimestamp();
        if (_state == State.Progress) LeaveProgress();
        _replay?.QueueFree();
        _host?.Dispose();
        if (OS.IsDebugBuild()) GD.Print($"training view closed: simulation stopped and session saved in {(Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency:0.0} ms");
    }

    public override void _Process(double delta)
    {
        _hud.Tick(delta);
        bool wait = _host.Frame(delta);
        if (wait && _state == State.Live) _host.WaitForFrame(SlowSpeedWait);
        _host.ReadStats(out _stats);

        if (_state == State.Progress && (_stats.TrainToRuns > _trainToRunsAtStart && !_stats.Headless || _stats.Error is not null)) LeaveProgress();

        if (_stats.Ready && !_started)
        {
            _started = true;
            _alloc.ResetCollections(); // count collections from the first generation on, not the set-up
        }
        if (_state == State.Live)
        {
            if (_stats.Ready) DrawCars(delta);
            _camera.PanWithKeys(delta);
            _summary.Visible = _stats.WaitingForNext && _stats.Error is null;
            _busy.Visible = _stats.Busy is not null;
        }
        if (_stats.Ready) RefreshGraphs();

        _hudTimer += delta;
        if (_state == State.Progress || _hudTimer >= HudInterval)
        {
            _hudTimer = 0;
            long before = GC.GetAllocatedBytesForCurrentThread();
            RefreshText(delta);
            _hudTextBytes = GC.GetAllocatedBytesForCurrentThread() - before;
            _alloc.Exclude(_hudTextBytes);
        }
        _alloc.EndFrame();
    }

    /// <summary>The 10-per-second text refresh (HUD, banners, summary, busy box, panel, progress screen). It allocates strings; the debug row shows how much.</summary>
    private void RefreshText(double delta)
    {
        if (_stats.NoticeCount != _lastNotice)
        {
            _lastNotice = _stats.NoticeCount;
            if (_stats.Notice is { } notice) _hud.ShowBanner(notice, UiTheme.Text);
        }
        if (_stats.SaveError != _lastSaveError)
        {
            _lastSaveError = _stats.SaveError;
            // A failed save is shown at once and kept visible in the corner; training goes on and the next save tries again (PLAN §5.1).
            if (_lastSaveError is { } why)
                _hud.ShowBanner($"Saving the session failed: {(why.Length > 140 ? why[..140] + "…" : why)}\nTraining goes on; the next save tries again.", UiTheme.Bad, 12);
        }
        if (_stats.BoostStartedAt != _lastBoost)
        {
            _lastBoost = _stats.BoostStartedAt;
            if (_lastBoost >= 0)
                _hud.ShowBanner($"Stagnation: mutation boosted ×2 for {EvolutionSettings.BoostGenerations} generations (after generation {_lastBoost})", UiTheme.Accent, 8);
        }
        switch (_state)
        {
            case State.Live:
                bool hint = _host.Training?.Config.Mode is RacingSettings { DirectionHint: true };
                _hud.Refresh(_stats, Engine.GetFramesPerSecond(), _renderMs, CameraText(), _alloc, _hudTextBytes, hint);
                if (_summary.Visible) _summary.Show(_stats, _host.Training?.Track.StartDistance ?? 0f, MutationText(_host.Training?.Runner.PopulationParams));
                if (_busy.Visible)
                {
                    _busyText.Text = _stats.Busy ?? "";
                    _busyBar.Value = _stats.BusyProgress;
                }
                if (_panel is { Visible: true } panel && _stats.Branch is { } branch) panel.Refresh(HudInterval, branch, _stats.Generation);
                break;
            case State.Progress:
                _progress.Refresh(_stats, delta);
                break;
        }
    }

    private static string MutationText(EvolutionParams? p) => p is { } q
        ? $"σ {q.MutationSigma:0.00##} · {q.MutationChance * 100:0.#} %{(q.Boost ? " · boost" : "")}"
        : "—";

    /// <summary>Copies the graph data from the host when it changed (allocation-free) into the graphs that are shown.</summary>
    private void RefreshGraphs()
    {
        GraphView? target = _state switch
        {
            State.Progress => _progress.Graph,
            State.Live when _bigGraphLayer.Visible => _bigGraph,
            State.Live when _hud.GraphVisible => _hud.Graph,
            _ => null,
        };
        if (target is null || target.Version == _stats.GraphVersion) return;
        target.Version = _stats.GraphVersion;
        int count = _host.CopyGraph(target.Best, target.Average, target.Dividers, out int dividers, out int generations, out float low, out float high);
        long before = GC.GetAllocatedBytesForCurrentThread();
        target.SetData(count, dividers, generations, low, high);
        // The large graph's axis labels are text made when the data changes (like the HUD text): not the render path.
        if (target.Large) _alloc.Exclude(GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>The render path: snapshot → interpolation → MultiMesh → route and rays. Allocates nothing.</summary>
    private void DrawCars(double delta)
    {
        long start = Stopwatch.GetTimestamp();

        var snapshots = _host.Snapshots;
        if (snapshots is null || snapshots.Capacity != _curr.Capacity) return;
        snapshots.CopyTo(_prev, _curr);
        int n = _curr.Capacity;
        Array.Copy(_x, _lastX, n);
        Array.Copy(_y, _lastY, n);
        SnapshotInterpolation.Interpolate(_prev, _curr, n, _stats.Alpha, _stats.Interpolates, _x, _y, _angle);

        // A clicked car belongs to its generation; when that ends, the camera goes back to the best car.
        if (_selected >= 0 && _curr.GenerationId != _selectedGeneration)
        {
            _selected = -1;
            if (_camera.Mode == ViewCamera.CameraMode.FollowSelected) _camera.Mode = ViewCamera.CameraMode.FollowBest;
        }
        int leader = _stats.Leader;
        _focus = _camera.Mode == ViewCamera.CameraMode.FollowBest ? leader : _selected;
        _host.Watch(_focus);

        _cars.Render(_x, _y, _angle, _curr.Status, leader, _selected);
        if (_focus >= 0) _camera.Follow(new Vector2(_x[_focus], _y[_focus]), _focus, _curr.GenerationId, delta,
            new Vector2(_x[_focus] - _lastX[_focus], _y[_focus] - _lastY[_focus]));
        DrawRoute(_focus);

        _renderMs = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
    }

    /// <summary>The followed car's route this generation (ending at its drawn position) and, while it runs, its rays.</summary>
    private void DrawRoute(int car)
    {
        if (car < 0)
        {
            _route.ClearRays();
            _route.Show(0, default, _camera.WorldPerPixel);
            return;
        }
        int count = _host.CopyTrail(_route.TrailX, _route.TrailY, out int agent, out int generation);
        if (agent != car || generation != _curr.GenerationId) count = 0; // the host has not caught up with the click yet
        else if (count < _route.TrailX.Length)
        {
            _route.TrailX[count] = _x[car];
            _route.TrailY[count] = _y[car];
            count++;
        }
        Sensors? sensors = _host.Training?.Mode.Sensors;
        if (sensors is not null && _curr.Status[car] == AgentStatus.Running)
            _route.SetRays(sensors, _x[car], _y[car], (int)MathF.Round(_angle[car] / FastMath.RadiansPerUnit) & FastMath.AngleUnitsMask);
        else
            _route.ClearRays();
        _route.Show(count, _cars.CarColor(car), _camera.WorldPerPixel);
    }

    /// <summary>Selects the car nearest to a click (within its size, or 12 px when zoomed far out) and follows it.</summary>
    private void PickCar(Vector2 screen)
    {
        if (!_stats.Ready || _state != State.Live) return;
        Vector2 at = _camera.ScreenToWorld(screen);
        float radius = MathF.Max(CarSize.HalfLength + 0.5f, 12f * _camera.WorldPerPixel);
        float best = radius * radius;
        int picked = -1;
        for (int i = 0; i < _curr.Capacity; i++)
        {
            AgentStatus s = _curr.Status[i];
            if (s is not (AgentStatus.Running or AgentStatus.Finished)) continue;
            float dx = _x[i] - at.X, dy = _y[i] - at.Y, d = dx * dx + dy * dy;
            if (d < best)
            {
                best = d;
                picked = i;
            }
        }
        if (picked >= 0) SelectCar(picked);
    }

    /// <summary>Follows car <paramref name="car"/> of the running generation (also the debug <c>--select</c>).</summary>
    public void SelectCar(int car)
    {
        if (car < 0 || car >= _population) return;
        _selected = car;
        _selectedGeneration = _curr.GenerationId;
        _camera.Mode = ViewCamera.CameraMode.FollowSelected;
        _hudTimer = HudInterval;
    }

    /// <summary>Where car <paramref name="car"/> was drawn last frame, in screen pixels (debug click injection).</summary>
    public Vector2 CarScreenPosition(int car) => _camera.WorldToScreen(new Vector2(_x[car], _y[car]));

    private string CameraText()
    {
        string Car(int i) => _curr.Status[i] switch
        {
            AgentStatus.Running => $"car {i}",
            AgentStatus.Finished => $"car {i} (finished)",
            var s => $"car {i} ({s.ToString().ToLowerInvariant()})",
        };
        return _camera.Mode switch
        {
            ViewCamera.CameraMode.FollowBest => _focus >= 0 ? $"best · {Car(_focus)}" : "best",
            ViewCamera.CameraMode.FollowSelected when _selected >= 0 => Car(_selected),
            _ => _selected >= 0 ? $"free · {Car(_selected)}" : "free",
        };
    }

    // ---------------------------------------------------------------- training control

    private void ToggleMode() => _host.StepMode = !_stats.StepMode;

    private void SetMutation(float factor)
    {
        var session = _host.Session;
        if (session is null) return;
        var d = new EvolutionSettings();
        _host.SetEvolution(session.Evolution with
        {
            MutationSigma = Math.Clamp(d.MutationSigma * factor, EvolutionSettings.MinMutationSigma, EvolutionSettings.MaxMutationSigma),
            MutationChance = Math.Min(1f, d.MutationChance * factor),
        });
    }

    private bool CanControl => _stats.Ready && _stats.Error is null && _stats.Busy is null && _state == State.Live;

    /// <summary>"Train to…" (PLAN §4): to generation N, until a car finishes, or until the best time is below X s.</summary>
    private void OpenTrainToDialog()
    {
        if (!CanControl || Modal.OpenCount > 0) return;
        var group = new ButtonGroup();
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 10);
        box.AddChild(Modal.Text("The map is not drawn meanwhile, so it runs at full speed with all threads. Cancel any time."));

        CheckBox Option(string text, bool on)
        {
            var c = new CheckBox { Text = text, ButtonGroup = group, ButtonPressed = on };
            c.AddThemeFontSizeOverride("font_size", 16);
            return c;
        }
        var toGen = Option("To generation", true);
        var gen = new SpinBox { MinValue = _stats.Generation + 1, MaxValue = 100_000_000, Step = 1, Value = _stats.Generation + 100, CustomMinimumSize = new Vector2(140, 0) };
        var row1 = new HBoxContainer();
        row1.AddThemeConstantOverride("separation", 10);
        row1.AddChild(toGen);
        row1.AddChild(gen);
        box.AddChild(row1);
        var untilFinish = Option("Until a car finishes", false);
        box.AddChild(untilFinish);
        var untilTime = Option("Until the best time is below", false);
        float best = float.IsFinite(_stats.BestTimeTicks) ? _stats.BestTimeTicks / RacingSettings.TicksPerSecond : 0f;
        var time = new SpinBox
        {
            MinValue = 0.5, MaxValue = RacingSettings.MaxTimeLimitSeconds, Step = 0.1, Suffix = "s", CustomMinimumSize = new Vector2(140, 0),
            Value = best > 0 ? Math.Max(0.5, Math.Round(best - 0.5, 1)) : Math.Round(_stats.TimeLimitTicks / (double)RacingSettings.TicksPerSecond * 0.5, 1),
        };
        var row3 = new HBoxContainer();
        row3.AddThemeConstantOverride("separation", 10);
        row3.AddChild(untilTime);
        row3.AddChild(time);
        box.AddChild(row3);
        gen.ValueChanged += _ => toGen.ButtonPressed = true;
        time.ValueChanged += _ => untilTime.ButtonPressed = true;
        box.AddChild(UiTheme.Label(best > 0 ? $"Best time so far (this segment): {best:0.00} s" : "No car has finished yet in this segment.", 14, UiTheme.Muted));

        Modal.Open(_ui, "Train to…", box,
            new Modal.Choice("Cancel"),
            new Modal.Choice("Start", () =>
            {
                var condition = untilFinish.ButtonPressed ? TrainToCondition.UntilFinish()
                    : untilTime.ButtonPressed ? TrainToCondition.UntilBestTimeBelow((float)time.Value)
                    : TrainToCondition.ToGeneration((int)gen.Value);
                StartTrainTo(condition);
            }, Primary: true));
    }

    /// <summary>Starts training without visuals and shows the progress screen until it ends.</summary>
    public void StartTrainTo(TrainToCondition condition)
    {
        if (!CanControl) return;
        _trainToRunsAtStart = _stats.TrainToRuns;
        _host.TrainTo(condition);
        _state = State.Progress;
        ShowWorld(false);
        _progress.Visible = true;
        int threads = _options.HeadlessThreads > 0 ? _options.HeadlessThreads : _options.Threads;
        bool lowered = CpuPriority.Lower();
        _progress.Begin(condition, _stats.Generation,
            $"{threads} simulation threads · process priority {(lowered ? "below normal" : "unchanged (" + CpuPriority.Why + ")")} · " +
            (_saves ? "the session keeps saving every 2 minutes" : "this training is not saved"));
        _progress.Graph.Version = -1;
        _savedMaxFps = Engine.MaxFps;
        Engine.MaxFps = 10; // PLAN §4: the progress screen runs at about 10 FPS
    }

    private void LeaveProgress()
    {
        _state = State.Live;
        _progress.Visible = false;
        ShowWorld(true);
        Engine.MaxFps = _savedMaxFps;
        CpuPriority.Restore();
        _hud.Graph.Version = -1;
        _hudTimer = HudInterval;
    }

    /// <summary>Hides the map, cars and HUD for a replay or the progress screen, and shows them again after (with the Training panel if it was open).</summary>
    private void ShowWorld(bool on)
    {
        _map.Visible = on;
        _cars.Visible = on;
        _route.Visible = on;
        _hud.Visible = on;
        if (!on)
        {
            _summary.Visible = false;
            _busy.Visible = false;
            _bigGraphLayer.Visible = false;
            _panelBeforeHiding = _panel is { Visible: true };
            if (_panel is not null) _panel.Visible = false;
        }
        else if (_panelBeforeHiding && _panel is not null)
        {
            _panelBeforeHiding = false;
            _panel.Visible = true;
            if (_stats.Branch is { } branch) _panel.Refresh(0, branch, _stats.Generation, force: true);
        }
    }

    private void TogglePanel()
    {
        if (!_stats.Ready || _state != State.Live || _host.Session is not { } session) return;
        if (_panel is null)
        {
            _panel = TrainingPanel.Create(session, readOnly: false);
            _panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.RightWide);
            _panel.OffsetLeft = -580;
            _panel.OffsetRight = -12;
            _panel.OffsetTop = 128;
            _panel.OffsetBottom = -12;
            _panel.WatchBest += WatchBest;
            _panel.WatchGeneration += WatchGeneration;
            _panel.ForkRequested += OpenForkDialog;
            _panel.SwitchRequested += SwitchTo;
            _panel.TimeLimitRequested += ConfirmTimeLimit;
            _panel.CloseRequested += TogglePanel;
            var panelParent = (Control)_ui.GetChild(0);
            panelParent.AddChild(_panel);
            panelParent.MoveChild(_panel, _busy.GetParent().GetIndex()); // under the rebuild box and the enlarged graph, which must stay on top
            _panel.Visible = false;
        }
        _panel.Visible = !_panel.Visible;
        _hud.GraphVisible = !_panel.Visible;
        _hud.Graph.Version = -1;
        if (_panel.Visible && _stats.Branch is { } branch) _panel.Refresh(0, branch, _stats.Generation, force: true);
    }

    /// <summary>Shows tab <paramref name="tab"/> of the Training panel (opening it).</summary>
    public void ShowPanelTab(int tab)
    {
        if (_panel is not { Visible: true }) TogglePanel();
        _panel?.ShowTab(tab);
    }

    private void ShowBigGraph(bool on)
    {
        if (on && (_state != State.Live || !_stats.Ready)) return;
        _bigGraphLayer.Visible = on;
        _bigGraph.Version = -1;
        _hud.Graph.Version = -1;
    }

    /// <summary>Why a fork or branch switch cannot start now (null when it can): a message instead of silence.</summary>
    private string? WhyNotNow()
    {
        if (_stats.Error is not null) return "Training stopped because of an error.";
        if (_stats.Busy is not null) return "A rebuild is already running; wait for it or cancel it first.";
        if (_stats.Headless || _backgroundTraining || _state == State.Progress) return "Training without visuals is running; cancel it first.";
        if (!_stats.Ready || _state != State.Live) return "Training is not ready yet.";
        return null;
    }

    /// <summary>The Switch button of a branch in the Training panel (also the debug <c>switch:NAME</c>).</summary>
    private void SwitchTo(string branch)
    {
        if (WhyNotNow() is { } why)
        {
            _hud.ShowBanner(why, UiTheme.Bad);
            return;
        }
        if (_host.Session is { } session && branch == session.ActiveBranch)
        {
            _hud.ShowBanner($"'{branch}' is already the active branch.", UiTheme.Text);
            return;
        }
        _host.SwitchBranch(branch);
    }

    private void StartFork(string branch, int generation, ForkKind kind)
    {
        if (WhyNotNow() is { } why) _hud.ShowBanner(why, UiTheme.Bad);
        else _host.Fork(branch, generation, kind);
    }

    private void OpenForkDialog(int generation)
    {
        if (Modal.OpenCount > 0 || _host.Session is not { } session) return;
        if (WhyNotNow() is { } why)
        {
            _hud.ShowBanner(why, UiTheme.Bad);
            return;
        }
        string branch = _stats.Branch ?? session.ActiveBranch;
        int steps;
        try
        {
            steps = Rebuilder.StepsNeeded(session, branch, generation);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException)
        {
            Modal.Message(_ui, "Fork", e.Message);
            return;
        }
        string text = $"A new branch starts from generation {generation} and becomes the active one; '{branch}' stays in the list.\n\n" +
                      "Whole population: continue with all of its cars.\nBest car only: a new population of mutated copies of its best car.";
        if (steps > 0)
            text += $"\n\nThe full population of generation {generation} was thinned to save memory, so \"Whole population\" must rebuild it first: {steps} generations to re-run " +
                    "(a progress bar with Cancel). \"Best car only\" starts at once.";
        Modal.Open(_ui, $"Fork from generation {generation}", Modal.Text(text),
            new Modal.Choice("Cancel"),
            new Modal.Choice("Best car only", () => StartFork(branch, generation, ForkKind.BestOnly)),
            new Modal.Choice("Whole population", () => StartFork(branch, generation, ForkKind.Full), Primary: true));
    }

    private void ConfirmTimeLimit(float seconds)
    {
        if (!CanControl || _host.Training?.Config.Mode is not RacingSettings settings || Modal.OpenCount > 0) return;
        string what = seconds > 0 ? $"{seconds:0.#} s" : "auto (reference time × 2.5)";
        Modal.Open(_ui, "New segment", Modal.Text($"From the next generation the time limit is {what}. That starts a new segment: the best time, the stagnation check and the graph restart there. The population carries on."),
            new Modal.Choice("Cancel"),
            new Modal.Choice("Start new segment", () => _host.ChangeSettings(settings with { TimeLimitSeconds = seconds }), Primary: true));
    }

    // ---------------------------------------------------------------- replays

    /// <summary>"Replay best": the best car of the generation before the running one, in the active lineage (also right after a fork or Continue).</summary>
    private void ReplayLastBest()
    {
        if (CanControl && _stats.Generation > 0) WatchBest(_stats.Generation - 1);
    }

    /// <summary>"Watch best": plays generation <paramref name="generation"/>'s best-car recording.</summary>
    public void WatchBest(int generation)
    {
        if (!CanControl || _host.Session is not { } session) return;
        HistoryEntry? entry;
        Sim.Io.SegmentEntry segment;
        string branch = _stats.Branch ?? session.ActiveBranch;
        lock (session.SyncRoot)
        {
            entry = session.EntryAt(branch, generation);
            segment = session.SegmentAt(branch, generation);
        }
        if (entry is null || entry.RecordingBytes.Length == 0)
        {
            _hud.ShowBanner($"Generation {generation} has no recording.", UiTheme.Bad);
            return;
        }
        int best = entry.Stats.BestIndex;
        OpenReplay(ReplayView.ForBest(segment.Track, entry, segment.Config.TimeLimitTicks, best < _population ? _cars.CarColor(best) : Colors.White,
            $"generation {generation}", showKeepTraining: true, s_keepTrainingWhileWatching));
    }

    /// <summary>"Watch whole generation": simulates every car of generation <paramref name="generation"/> again.</summary>
    public void WatchGeneration(int generation)
    {
        if (!CanControl || _host.Session is not { } session) return;
        string branch = _stats.Branch ?? session.ActiveBranch;
        Track track;
        lock (session.SyncRoot) track = session.SegmentAt(branch, generation).Track;
        int headless = _options.HeadlessThreads > 0 ? _options.HeadlessThreads : _options.Threads;
        var replay = new GenerationReplay(session, branch, generation, _options.Threads, s_keepTrainingWhileWatching ? _options.Threads : headless);
        OpenReplay(ReplayView.ForGeneration(track, replay, _colorSeed, $"generation {generation}", showKeepTraining: true, s_keepTrainingWhileWatching));
    }

    private void OpenReplay(ReplayView view)
    {
        _pausedBeforeReplay = _stats.Paused;
        _host.SetPaused(true);
        _state = State.Replay;
        ShowWorld(false);
        _replay = view;
        view.TrainingStatus = TrainingStatusText;
        view.BackRequested += CloseReplay;
        view.KeepTrainingChanged += on =>
        {
            s_keepTrainingWhileWatching = on;
            SetBackgroundTraining(on);
        };
        AddChild(view);
        if (view.KeepTraining) SetBackgroundTraining(true);
    }

    /// <summary>Trains on without visuals while a replay is watched (with the visual thread count, so the replay keeps its share).</summary>
    private void SetBackgroundTraining(bool on)
    {
        if (on == _backgroundTraining) return;
        _backgroundTraining = on;
        if (on) _host.TrainTo(TrainToCondition.ToGeneration(int.MaxValue), _options.Threads);
        else _host.CancelTrainTo();
    }

    private string TrainingStatusText()
    {
        var s = _stats;
        if (s.Error is not null) return "Training stopped: " + s.Error;
        if (_backgroundTraining && s.Headless)
            return $"Training goes on: generation {s.Generation}" + (s.GenerationsPerSecond > 0 ? $" · {s.GenerationsPerSecond:0.#} generations/s" : "");
        return $"Training waits at generation {s.Generation} until you go back.";
    }

    private void CloseReplay()
    {
        if (_replay is null) return;
        bool trainedMeanwhile = _backgroundTraining;
        SetBackgroundTraining(false);
        _replay.QueueFree();
        _replay = null;
        _state = State.Live;
        ShowWorld(true);
        RenderingServer.SetDefaultClearColor(MapTexture.Outside);
        _camera.MakeCurrent();
        // Background training in Step mode left the generation half run: let it finish, then it waits at the summary.
        _host.SetPaused(trainedMeanwhile && _stats.StepMode ? false : _pausedBeforeReplay);
        _hud.Graph.Version = -1;
        _hudTimer = HudInterval;
    }

    private void Back()
    {
        if (_backRequested) return;
        _backRequested = true;
        BackRequested?.Invoke();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (Modal.OpenCount > 0 || _state == State.Replay) return;
        if (_state == State.Progress)
        {
            if (e is InputEventKey { Pressed: true, Keycode: Key.Escape })
            {
                _host.CancelTrainTo();
                GetViewport().SetInputAsHandled();
            }
            return;
        }
        if (_bigGraphLayer.Visible && e is InputEventKey { Pressed: true, Keycode: Key.Escape or Key.G })
        {
            ShowBigGraph(false);
            GetViewport().SetInputAsHandled();
            return;
        }
        if (_camera.HandleInput(e))
        {
            GetViewport().SetInputAsHandled();
            return;
        }
        if (e is InputEventJoypadButton { Pressed: true } pad)
        {
            Action? action = pad.ButtonIndex switch
            {
                JoyButton.Back => Back,
                JoyButton.A => _host.TogglePause,
                JoyButton.X => _host.StepTick,
                JoyButton.LeftShoulder => _host.Slower,
                JoyButton.RightShoulder => _host.Faster,
                _ => null,
            };
            if (action is null) return;
            Changed(action);
            GetViewport().SetInputAsHandled();
            return;
        }
        if (e is not InputEventKey { Pressed: true, Echo: false } key) return;
        bool used = true;
        switch (key.Keycode)
        {
            case Key.Minus or Key.KpSubtract:
                _host.Slower();
                break;
            case Key.Equal or Key.Plus or Key.KpAdd:
                _host.Faster();
                break;
            case Key.Space:
                _host.TogglePause();
                break;
            case Key.Period or Key.KpPeriod:
                _host.StepTick();
                break;
            case Key.Enter or Key.KpEnter when _stats.WaitingForNext:
                _host.Next();
                break;
            case Key.N:
                if (CanControl) _host.EndGenerationNow();
                break;
            case Key.M:
                ToggleMode();
                break;
            case Key.T:
                OpenTrainToDialog();
                break;
            case Key.R:
                ReplayLastBest();
                break;
            case Key.H:
                TogglePanel();
                break;
            case Key.G:
                ShowBigGraph(true);
                break;
            case Key.F:
                _camera.Mode = ViewCamera.CameraMode.FollowBest;
                _selected = -1;
                break;
            case Key.F3:
                _hud.DebugVisible = !_hud.DebugVisible;
                break;
            case Key.Escape:
                if (_panel is { Visible: true }) TogglePanel();
                else Back();
                break;
            default:
                used = key.Unicode switch
                {
                    '+' => Do(_host.Faster),
                    '-' => Do(_host.Slower),
                    _ => false,
                };
                break;
        }
        if (used)
        {
            _hudTimer = HudInterval; // show the change next frame
            GetViewport().SetInputAsHandled();
        }
    }

    /// <summary>Runs a control action and shows its effect in the HUD next frame.</summary>
    private void Changed(Action action)
    {
        action();
        _hudTimer = HudInterval;
    }

    private static bool Do(Action action)
    {
        action();
        return true;
    }

    /// <summary>Shows or hides the debug rows (also from the command line).</summary>
    public void ShowTimings(bool on) => _hud.DebugVisible = on;

    /// <summary>A message box over the view.</summary>
    public void ShowMessage(string title, string text) => Modal.Message(_ui, title, text);

    // ---------------------------------------------------------------- debug script (Main's --do)

    /// <summary>Nothing is in progress: the next scripted step can run.</summary>
    public bool IsIdle
    {
        get
        {
            if (!_stats.Ready || _stats.Busy is not null || _state == State.Progress || (_replay is not null && !_replay.IsReady)) return false;
            if (_debugWait is null) return true;
            if (!_debugWait()) return false;
            _debugWait = null; // one-shot: a later fork/switch can move the generation number back
            return true;
        }
    }

    /// <summary>A scripted <c>until:…</c> condition the next step waits for (debug).</summary>
    private Func<bool>? _debugWait;

    /// <summary>Prints the stats a check needs and the last stored generations of the active branch (debug <c>state</c> step).</summary>
    private void PrintState()
    {
        var s = _stats;
        string saveError = s.SaveError ?? "-";
        GD.Print($"state gen={s.Generation} last={s.LastGeneration} stepMode={s.StepMode} waiting={s.WaitingForNext} paused={s.Paused} elapsed={s.ElapsedTicks}/{s.TimeLimitTicks} " +
                 $"boost={s.BoostActive} boostAt={s.BoostStartedAt} saved={s.SavedAtUtc:O} saving={s.Saving} saveError={saveError} bigGraph={_bigGraphLayer.Visible}");
        if (_host.Session is not { } session) return;
        string branch = _stats.Branch ?? session.ActiveBranch;
        lock (session.SyncRoot)
        {
            foreach (var b in session.Branches)
                GD.Print($"state branch {b.Name} parent={b.Parent ?? "-"} first={b.FirstGeneration} end={session.HistoryEnd(b.Name)} kind={b.Kind}{(b.Name == session.ActiveBranch ? " ACTIVE" : "")}");
            int last = session.Current.Generation;
            for (int g = Math.Max(0, last - 4); g < last; g++)
                if (session.EntryAt(branch, g) is { } e)
                    GD.Print($"state history gen={e.Generation} ticks={e.Ticks} sigma={e.Params.MutationSigma:0.0000} chance={e.Params.MutationChance:0.0000} boost={e.Params.Boost} best={e.Stats.BestScore:0.000} avg={e.Stats.AverageScore:0.000} finished={e.Stats.FinishedCount}");
        }
    }

    /// <summary>Pushes a real left click (press and release) at the centre of the small graph, or of the big one, through the viewport.</summary>
    private void ClickGraph(bool small)
    {
        Control target = small ? _hud.Graph : _bigGraph;
        Vector2 at = target.GetGlobalRect().GetCenter();
        foreach (bool down in new[] { true, false })
            GetViewport().PushInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = down, Position = at, GlobalPosition = at, ButtonMask = down ? MouseButtonMask.Left : 0 }, true);
    }

    /// <summary>Presses a HUD or summary button by its label through a real click at its centre (debug).</summary>
    private string? ClickButton(string label)
    {
        foreach (Node root in new Node[] { _hud, _ui })
        {
            var button = FindButton(root, label);
            if (button is null || !button.IsVisibleInTree()) continue;
            Vector2 at = button.GetGlobalRect().GetCenter();
            foreach (bool down in new[] { true, false })
                GetViewport().PushInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = down, Position = at, GlobalPosition = at, ButtonMask = down ? MouseButtonMask.Left : 0 }, true);
            return null;
        }
        return $"no visible button '{label}'";
    }

    private static Button? FindButton(Node node, string label)
    {
        if (node is Button b && b.Text.StartsWith(label, StringComparison.Ordinal)) return b;
        foreach (Node child in node.GetChildren())
            if (FindButton(child, label) is { } found) return found;
        return null;
    }

    /// <summary>Runs one scripted step (debug builds); returns an error text or null.</summary>
    public string? DebugDo(string verb, string arg)
    {
        int Int() => int.Parse(arg, Inv);
        switch (verb)
        {
            case "trainto": StartTrainTo(TrainToCondition.ToGeneration(Int())); break;
            case "trainto-finish": StartTrainTo(TrainToCondition.UntilFinish()); break;
            case "trainto-time": StartTrainTo(TrainToCondition.UntilBestTimeBelow(float.Parse(arg, Inv))); break;
            case "cancel": _host.CancelTrainTo(); break;
            case "fork": StartFork(_stats.Branch!, Int(), ForkKind.Full); break;
            case "forkbest": StartFork(_stats.Branch!, Int(), ForkKind.BestOnly); break;
            case "fork-raw": _host.Fork(_stats.Branch!, Int(), ForkKind.Full); break; // straight to the host (its own refusals)
            case "switch-raw": _host.SwitchBranch(arg); break;
            case "forkdialog": OpenForkDialog(Int()); break;
            case "switch": SwitchTo(arg); break;
            case "later": // later:SECONDS:VERB[:ARG] runs a verb after a delay without waiting for idle (to hit a running rebuild or train-to)
            {
                string[] parts = arg.Split(':', 3);
                double seconds = double.Parse(parts[0], Inv);
                GetTree().CreateTimer(seconds).Timeout += () => GD.Print($"later {parts[1]}: " + (DebugDo(parts[1], parts.Length > 2 ? parts[2] : "") ?? "ok") + $" busy={_stats.Busy is not null} headless={_stats.Headless}");
                break;
            }
            case "rebuild-cancel": _host.CancelBusy(); break;
            case "cancel-after": // presses the rebuild's Cancel button with a real click after S seconds (the script cannot wait for it: it is busy)
                GetTree().CreateTimer(double.Parse(arg, Inv)).Timeout += () => GD.Print("cancel-after: " + (ClickButton("Cancel") ?? "clicked"));
                break;
            case "panel-scroll": _panel?.ScrollRows(Int()); break;
            case "panel-wheel": // N real mouse wheel notches over the history list (positive = down)
                if (_panel is { Visible: true } wheelPanel)
                    for (int i = 0, n = Math.Abs(Int()); i < n; i++)
                        GetViewport().PushInput(new InputEventMouseButton { ButtonIndex = Int() > 0 ? MouseButton.WheelDown : MouseButton.WheelUp, Pressed = true, Position = wheelPanel.ListCenter, GlobalPosition = wheelPanel.ListCenter }, true);
                break;
            case "panel-first": GD.Print($"panel first generation shown: {_panel?.FirstShownGeneration}"); break;
            case "panel": ShowPanelTab(arg switch { "branches" => 1, "segments" => 2, _ => 0 }); break;
            case "panel-close": if (_panel is { Visible: true }) TogglePanel(); break;
            case "graph": ShowBigGraph(!_bigGraphLayer.Visible); break;
            case "traintodialog": OpenTrainToDialog(); break;
            case "timelimit": ConfirmTimeLimit(float.Parse(arg, Inv)); break;
            case "replay-best": WatchBest(Int()); break;
            case "replay-gen": WatchGeneration(Int()); break;
            case "replay-speed": _replay?.SetSpeedFromScript(double.Parse(arg, Inv)); break;
            case "keep-training": _replay?.SetKeepTraining(arg == "on"); break;
            case "back": if (_replay is not null) _replay.Back(); break;
            case "mode": _host.StepMode = arg == "step"; break;
            case "next": _host.Next(); break;
            case "nextgen": _host.EndGenerationNow(); break;
            case "pause": _host.SetPaused(true); break;
            case "resume": _host.SetPaused(false); break;
            case "speed": _host.SetSpeed(arg == "max" ? double.PositiveInfinity : double.Parse(arg, Inv)); break;
            case "mutation": _hud.SetMutationFromScript(float.Parse(arg, Inv)); break;
            case "until-waiting": _debugWait = () => _stats.WaitingForNext; break;
            case "until-gen": { int g = Int(); _debugWait = () => _stats.Generation >= g; } break;
            case "until-tick": { int t = Int(); _debugWait = () => _stats.ElapsedTicks >= t; } break;
            case "state": PrintState(); break;
            case "click-graph": ClickGraph(arg != "big"); break;
            case "key": { var k = OS.FindKeycodeFromString(arg); Input.ParseInputEvent(new InputEventKey { Keycode = k, PhysicalKeycode = k, Pressed = true }); } break;
            case "click-button": return ClickButton(arg);
            default: return $"unknown step '{verb}'";
        }
        return null;
    }
}
