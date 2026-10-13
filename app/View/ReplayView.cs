using System;
using System.Globalization;
using Godot;
using Nitrogenesis.Sim.Brain;
using Nitrogenesis.Sim.Core;
using Nitrogenesis.Sim.History;
using Nitrogenesis.Sim.Map;
using Nitrogenesis.Sim.Racing;
using Nitrogenesis.Ui;

namespace Nitrogenesis.View;

/// <summary>
/// The replay view (PLAN §5 "Replay"): "Watch best" plays a generation's best-car recording; "Watch whole generation"
/// simulates every car of it again (<see cref="GenerationReplay"/>, same SimVersion only). It has its own map,
/// camera, speed control (Pause, Step, Restart, the speed slider) and a Back button. The training it came from pauses,
/// or keeps running without visuals when "Keep training while watching" is ticked (the owner does that; this view
/// raises <see cref="KeepTrainingChanged"/> and shows <see cref="TrainingStatus"/>).
/// </summary>
public partial class ReplayView : Node2D
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly TimeSpan SlowSpeedWait = TimeSpan.FromMilliseconds(4);
    private const string Hints = "−/+ speed   Space pause   . step   Home restart   click a car: follow it   F follow best   wheel zoom   drag / WASD pan   Esc back";

    private Track _track = null!;
    private string _title = "";
    private RecordingPlayback? _playback;
    private HistoryEntry? _entry;
    private int _timeLimitTicks;
    private Color _bestColor;
    private GenerationReplay? _replay;
    private ulong _colorSeed;
    private bool _showKeepTraining;

    private CarRenderer _cars = null!;
    private RouteOverlay _route = null!;
    private ViewCamera _camera = null!;
    private CanvasLayer _hud = null!;
    private Label _status = null!, _time = null!, _alive = null!, _result = null!, _speed = null!, _trainingLine = null!, _sliderLabel = null!;
    private Button _pause = null!, _step = null!;
    private HSlider _slider = null!;
    private CheckBox _keep = null!;
    private PanelContainer _busy = null!;
    private Label _busyText = null!;
    private ProgressBar _busyBar = null!;

    private AgentSnapshot _prev = null!, _curr = null!;
    private float[] _x = null!, _y = null!, _angle = null!;
    private AgentStatus[] _status1 = [AgentStatus.Running];
    private ReplayStats _stats;
    private int _selected = -1, _focus = -1, _trailStride = 1;
    private double _hudTimer;
    private bool _backRequested;

    /// <summary>The user leaves (Back or Esc). The owner frees the view.</summary>
    public event Action? BackRequested;
    /// <summary>"Keep training while watching" was ticked or unticked.</summary>
    public event Action<bool>? KeepTrainingChanged;
    /// <summary>A line about the training it came from ("paused at generation 31", …), asked a few times per second.</summary>
    public Func<string>? TrainingStatus { get; set; }

    public bool KeepTraining { get; private set; }
    /// <summary>The replay can be watched (a whole generation: after its rebuild, or it failed).</summary>
    public bool IsReady => _replay is null || _stats.Ready || _stats.Error is not null;
    public ViewCamera Camera => _camera;
    public bool IsWholeGeneration => _replay is not null;
    public bool Ended => _replay is null ? _playback!.Ended : _stats.Ended;

    /// <summary>Watch best: the recording of <paramref name="entry"/>'s best car.</summary>
    public static ReplayView ForBest(Track track, HistoryEntry entry, int timeLimitTicks, Color carColor, string title, bool showKeepTraining, bool keepTraining)
    {
        var recording = entry.DecodeRecording() ?? throw new InvalidOperationException($"Generation {entry.Generation} has no recording.");
        return new ReplayView
        {
            _track = track, _entry = entry, _playback = new RecordingPlayback(recording), _timeLimitTicks = timeLimitTicks, _bestColor = carColor,
            _title = title, _showKeepTraining = showKeepTraining, KeepTraining = keepTraining,
        };
    }

    /// <summary>Watch whole generation: <paramref name="replay"/> (the view owns and disposes it).</summary>
    public static ReplayView ForGeneration(Track track, GenerationReplay replay, ulong colorSeed, string title, bool showKeepTraining, bool keepTraining) =>
        new()
        {
            _track = track, _replay = replay, _colorSeed = colorSeed, _title = title, _showKeepTraining = showKeepTraining, KeepTraining = keepTraining,
        };

    public override void _Ready()
    {
        RenderingServer.SetDefaultClearColor(MapTexture.Outside);
        AddChild(new Sprite2D { Texture = MapTexture.Create(_track.Grid, _track.Start), Centered = false, TextureFilter = TextureFilterEnum.Nearest });

        int n;
        if (_playback is not null)
        {
            n = 1;
            int samples = _playback.Recording.Count;
            while ((samples + _trailStride - 1) / _trailStride > RouteTrails.MaxSamplesPerCar) _trailStride *= 2;
            _route = new RouteOverlay(RouteTrails.MaxSamplesPerCar + 2);
        }
        else
        {
            // The population size is the generation's segment's (known once the replay is set up); the session keeps one size in M3.
            n = Math.Max(1, _replay!.Training?.Mode.AgentCount ?? 0);
            _route = new RouteOverlay();
        }
        AddChild(_route);
        _cars = new CarRenderer();
        AddChild(_cars);
        Setup(n);

        _camera = new ViewCamera { Position = new Vector2(_track.Start.X, _track.Start.Y), UserZoom = _playback is not null ? 2f : 1f };
        AddChild(_camera);
        _camera.MakeCurrent();
        _camera.Clicked += PickCar;
        BuildHud();
    }

    /// <summary>(Re)allocates the per-car buffers for <paramref name="n"/> cars.</summary>
    private void Setup(int n)
    {
        if (_x is not null && _x.Length == n) return;
        if (_playback is not null) _cars.Setup(1, 0, new Vector2(_track.Grid.Width, _track.Grid.Height), _bestColor);
        else _cars.Setup(n, _colorSeed, new Vector2(_track.Grid.Width, _track.Grid.Height));
        _prev = new AgentSnapshot(n);
        _curr = new AgentSnapshot(n);
        _x = new float[n];
        _y = new float[n];
        _angle = new float[n];
    }

    private void BuildHud()
    {
        _hud = new CanvasLayer { Layer = 5 };
        AddChild(_hud);
        var root = new Control { Theme = UiTheme.Theme, MouseFilter = Control.MouseFilterEnum.Ignore };
        root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _hud.AddChild(root);

        var panel = new PanelContainer { Position = new Vector2(12, 12), MouseFilter = Control.MouseFilterEnum.Ignore };
        root.AddChild(panel);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 6);
        panel.AddChild(box);
        box.AddChild(UiTheme.Label("REPLAY · " + _title.ToUpperInvariant(), 20, UiTheme.Accent));
        box.AddChild(UiTheme.Label(_playback is not null ? "best car of the generation (its recording)" : "whole generation, simulated again", 14, UiTheme.Muted));
        _status = UiTheme.Label("", 15, UiTheme.Muted);
        box.AddChild(_status);
        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 18);
        grid.AddThemeConstantOverride("v_separation", 2);
        box.AddChild(grid);
        _time = Row(grid, "Time");
        _alive = Row(grid, _playback is not null ? "Car" : "Alive");
        _result = Row(grid, _playback is not null ? "Result" : "Best time");
        _speed = Row(grid, "Speed");

        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 8);
        _pause = UiTheme.SmallButton("Pause", TogglePause);
        _pause.CustomMinimumSize = new Vector2(96, 0);
        _step = UiTheme.SmallButton("Step", StepTick);
        _step.Disabled = true;
        buttons.AddChild(_pause);
        buttons.AddChild(_step);
        buttons.AddChild(UiTheme.SmallButton("Restart", Restart));
        box.AddChild(buttons);

        var speed = new HBoxContainer();
        speed.AddThemeConstantOverride("separation", 10);
        int max = _playback is not null ? RecordingPlayback.MaxSpeedIndex : SpeedController.MaxIndex;
        _slider = new HSlider
        {
            MinValue = 0, MaxValue = max, Step = 1, Value = SpeedController.DefaultIndex, TickCount = max + 1, TicksOnBorders = true, Scrollable = false,
            FocusMode = Control.FocusModeEnum.None, CustomMinimumSize = new Vector2(190, 0), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, TooltipText = "Replay speed",
        };
        _slider.ValueChanged += v => SetSpeed((int)Math.Round(v));
        speed.AddChild(_slider);
        _sliderLabel = UiTheme.Label("1×", 15, UiTheme.Text);
        _sliderLabel.CustomMinimumSize = new Vector2(48, 0);
        speed.AddChild(_sliderLabel);
        box.AddChild(speed);

        if (_showKeepTraining)
        {
            _keep = new CheckBox { Text = "Keep training while watching", ButtonPressed = KeepTraining, FocusMode = Control.FocusModeEnum.None };
            _keep.AddThemeFontSizeOverride("font_size", 15);
            _keep.TooltipText = "Off: the training waits until you go back. On: it goes on without visuals meanwhile.";
            _keep.Toggled += on =>
            {
                KeepTraining = on;
                KeepTrainingChanged?.Invoke(on);
                _hudTimer = 1;
            };
            box.AddChild(_keep);
            _trainingLine = UiTheme.Label("", 14, UiTheme.Muted);
            box.AddChild(_trainingLine);
        }

        var back = UiTheme.SmallButton("Back  (Esc)", Back);
        back.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopRight, Control.LayoutPresetMode.Minsize, 12);
        back.GrowHorizontal = Control.GrowDirection.Begin;
        root.AddChild(back);

        var hints = UiTheme.Label(Hints, 13, UiTheme.Muted);
        hints.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomLeft, Control.LayoutPresetMode.Minsize, 14);
        hints.GrowVertical = Control.GrowDirection.Begin;
        root.AddChild(hints);

        // The rebuild of a thinned generation, with its progress and Cancel.
        var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        center.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        root.AddChild(center);
        _busy = new PanelContainer { Visible = false };
        center.AddChild(_busy);
        var busyBox = new VBoxContainer();
        busyBox.AddThemeConstantOverride("separation", 10);
        _busy.AddChild(busyBox);
        _busyText = UiTheme.Label("Preparing…", 17, UiTheme.Text);
        busyBox.AddChild(_busyText);
        _busyBar = new ProgressBar { MinValue = 0, MaxValue = 1, CustomMinimumSize = new Vector2(420, 20) };
        busyBox.AddChild(_busyBar);
        var cancel = UiTheme.SmallButton("Cancel", () =>
        {
            _replay?.Cancel();
            Back();
        });
        cancel.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;
        busyBox.AddChild(cancel);
    }

    private static Label Row(GridContainer grid, string name)
    {
        grid.AddChild(UiTheme.Label(name, 15, UiTheme.Muted));
        var value = UiTheme.Label("—", 15, UiTheme.Text);
        value.CustomMinimumSize = new Vector2(170, 0);
        grid.AddChild(value);
        return value;
    }

    public override void _ExitTree() => _replay?.Dispose();

    public override void _Process(double delta)
    {
        if (_playback is not null) ProcessBest(delta);
        else ProcessGeneration(delta);
        _camera.PanWithKeys(delta);
        _hudTimer += delta;
        if (_hudTimer >= 0.1)
        {
            _hudTimer = 0;
            RefreshHud();
        }
    }

    private void ProcessBest(double delta)
    {
        var p = _playback!;
        p.Advance(delta);
        p.Pose(out float x, out float y, out float angle);
        _x[0] = x;
        _y[0] = y;
        _angle[0] = angle;
        _cars.Render(_x, _y, _angle, _status1, 0);
        if (_camera.Mode != ViewCamera.CameraMode.Free) _camera.Follow(new Vector2(x, y), 0, 0, delta);
        // The route so far: every stride-th sample, ending at the drawn position.
        var rec = p.Recording;
        int count = 0;
        for (int t = 0; t <= p.Tick && count < _route.TrailX.Length - 1; t += _trailStride)
        {
            _route.TrailX[count] = rec.X(t);
            _route.TrailY[count] = rec.Y(t);
            count++;
        }
        _route.TrailX[count] = x;
        _route.TrailY[count] = y;
        _route.ClearRays();
        _route.Show(count + 1, _bestColor, _camera.WorldPerPixel);
    }

    private void ProcessGeneration(double delta)
    {
        var r = _replay!;
        bool wait = r.Frame(delta);
        if (wait) r.WaitForFrame(SlowSpeedWait);
        r.ReadStats(out _stats);
        _busy.Visible = !_stats.Ready && _stats.Error is null;
        if (_busy.Visible)
        {
            _busyText.Text = _stats.Busy ?? $"Setting up generation {r.Generation}…";
            _busyBar.Value = _stats.BusyProgress;
        }
        if (!_stats.Ready || r.Snapshots is not { } snapshots) return;
        Setup(snapshots.Capacity);
        snapshots.CopyTo(_prev, _curr);
        int n = _curr.Capacity;
        SnapshotInterpolation.Interpolate(_prev, _curr, n, _stats.Alpha, _stats.Interpolates, _x, _y, _angle);
        if (_selected >= n) _selected = -1;
        int leader = _stats.Leader;
        _focus = _camera.Mode == ViewCamera.CameraMode.FollowBest ? leader : _selected;
        _cars.Render(_x, _y, _angle, _curr.Status, leader, _selected);
        if (_focus >= 0 && _focus < n) _camera.Follow(new Vector2(_x[_focus], _y[_focus]), _focus, 0, delta);
        Sensors? sensors = r.Training?.Mode.Sensors;
        if (_focus >= 0 && _focus < n && sensors is not null && _curr.Status[_focus] == AgentStatus.Running)
            _route.SetRays(sensors, _x[_focus], _y[_focus], (int)MathF.Round(_angle[_focus] / FastMath.RadiansPerUnit) & FastMath.AngleUnitsMask);
        else
            _route.ClearRays();
        _route.Show(0, default, _camera.WorldPerPixel);
    }

    private void RefreshHud()
    {
        bool paused;
        int speedIndex;
        double target, actual;
        if (_playback is not null)
        {
            var p = _playback;
            paused = p.Paused;
            speedIndex = p.SpeedIndex;
            target = p.TargetSpeed;
            actual = p.ActualSpeed;
            var s = _entry!.Stats;
            _status.Text = p.Ended ? "End of the run  ( Home = again )" : paused ? "Paused" : "Playing";
            _time.Text = $"{p.Tick / (double)RacingSettings.TicksPerSecond:0.0} / {_timeLimitTicks / (double)RacingSettings.TicksPerSecond:0.0} s";
            _alive.Text = $"car {s.BestIndex} (best score {s.BestScore:0.000})";
            _result.Text = s.FinishedCount > 0 ? $"finished in {s.BestFinishTicks / RacingSettings.TicksPerSecond:0.00} s" : $"did not finish · {p.LastTick / (double)RacingSettings.TicksPerSecond:0.0} s";
        }
        else
        {
            var s = _stats;
            paused = s.Paused;
            speedIndex = s.SpeedIndex;
            target = s.TargetSpeed;
            actual = s.ActualSpeed;
            if (s.Error is not null)
            {
                _status.Text = s.Error;
                _status.AddThemeColorOverride("font_color", UiTheme.Bad);
            }
            else
            {
                _status.Text = !s.Ready ? "Setting up…" : s.Ended ? (s.MatchesRecord ? "End of the generation: same result as recorded ✓" : "End of the generation") : paused ? "Paused" : "Playing";
                _status.AddThemeColorOverride("font_color", s.Ended && s.MatchesRecord ? UiTheme.Good : UiTheme.Muted);
            }
            int end = s.EndTicks >= 0 ? s.EndTicks : s.TimeLimitTicks;
            _time.Text = $"{s.ElapsedTicks / (double)RacingSettings.TicksPerSecond:0.0} / {end / (double)RacingSettings.TicksPerSecond:0.0} s";
            _alive.Text = $"{s.Alive} / {s.PopulationSize} · {s.Finished} finished";
            _result.Text = float.IsFinite(s.BestTimeTicks) ? $"{s.BestTimeTicks / RacingSettings.TicksPerSecond:0.00} s" : "—";
        }
        _speed.Text = paused ? $"paused ({SpeedController.Label(target)})"
            : double.IsPositiveInfinity(target) && actual > 0 ? $"MAX (actual {SpeedController.Label(Math.Round(actual))})" : SpeedController.Label(target);
        _pause.Text = paused ? "Resume" : "Pause";
        _step.Disabled = !paused;
        _slider.SetValueNoSignal(speedIndex);
        _sliderLabel.Text = SpeedController.Label(target);
        if (_trainingLine is not null && TrainingStatus is not null) _trainingLine.Text = TrainingStatus();
    }

    // ---------------------------------------------------------------- controls

    private void TogglePause()
    {
        if (_playback is not null) _playback.Paused = !_playback.Paused;
        else _replay!.TogglePause();
        _hudTimer = 1;
    }

    private void StepTick()
    {
        if (_playback is not null) _playback.StepOnce();
        else _replay!.StepTick();
    }

    public void Restart()
    {
        if (_playback is not null) _playback.Restart();
        else _replay!.Restart();
        _hudTimer = 1;
    }

    private void SetSpeed(int index)
    {
        if (_playback is not null) _playback.SpeedIndex = index;
        else _replay!.SetSpeed(SpeedController.StepValue(index));
        _hudTimer = 1;
    }

    /// <summary>Debug script: the replay speed (×, ∞ = MAX).</summary>
    public void SetSpeedFromScript(double speed) => SetSpeed(SpeedController.IndexFor(speed));

    /// <summary>Debug script: ticks or unticks "Keep training while watching" (as a click would).</summary>
    public void SetKeepTraining(bool on)
    {
        if (_keep is not null) _keep.ButtonPressed = on;
    }

    private void Faster() => SetSpeed((_playback?.SpeedIndex ?? _stats.SpeedIndex) + 1);
    private void Slower() => SetSpeed(Math.Max(0, (_playback?.SpeedIndex ?? _stats.SpeedIndex) - 1));

    private void PickCar(Vector2 screen)
    {
        if (_replay is null || !_stats.Ready) return;
        Vector2 at = _camera.ScreenToWorld(screen);
        float radius = MathF.Max(CarSize.HalfLength + 0.5f, 12f * _camera.WorldPerPixel);
        float best = radius * radius;
        int picked = -1;
        for (int i = 0; i < _curr.Capacity; i++)
        {
            if (_curr.Status[i] is not (AgentStatus.Running or AgentStatus.Finished)) continue;
            float dx = _x[i] - at.X, dy = _y[i] - at.Y, d = dx * dx + dy * dy;
            if (d < best)
            {
                best = d;
                picked = i;
            }
        }
        if (picked < 0) return;
        _selected = picked;
        _camera.Mode = ViewCamera.CameraMode.FollowSelected;
    }

    public void Back()
    {
        if (_backRequested) return;
        _backRequested = true;
        BackRequested?.Invoke();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (Modal.OpenCount > 0) return;
        if (_camera.HandleInput(e))
        {
            GetViewport().SetInputAsHandled();
            return;
        }
        if (e is not InputEventKey { Pressed: true } key) return;
        bool used = true;
        switch (key.Keycode)
        {
            case Key.Minus or Key.KpSubtract:
                Slower();
                break;
            case Key.Equal or Key.Plus or Key.KpAdd:
                Faster();
                break;
            case Key.Space:
                TogglePause();
                break;
            case Key.Period or Key.KpPeriod:
                StepTick();
                break;
            case Key.Home:
                Restart();
                break;
            case Key.F:
                _camera.Mode = ViewCamera.CameraMode.FollowBest;
                _selected = -1;
                break;
            case Key.Escape:
                Back();
                break;
            default:
                used = false;
                break;
        }
        if (used)
        {
            _hudTimer = 1;
            GetViewport().SetInputAsHandled();
        }
    }
}
