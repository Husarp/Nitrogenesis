using System;
using System.Diagnostics;
using Godot;
using Nitrogenesis.Sim.Brain;
using Nitrogenesis.Sim.Core;
using Nitrogenesis.Sim.Map;
using Nitrogenesis.Sim.Racing;

namespace Nitrogenesis.View;

/// <summary>
/// Watch training (PLAN §6.2, M2 part): the map texture, all cars in one MultiMesh, the camera and the HUD over a
/// <see cref="TrainingHost"/> that runs generation after generation on its own threads. Click a car to follow it;
/// the followed car shows its sensor rays and its route this generation.
/// </summary>
/// <remarks>
/// Per frame: <see cref="TrainingHost.Frame"/> (adds game time, wakes the sim) → at 1× and slower a short wait for
/// that frame's tick → copy the latest snapshot pair → interpolate (PLAN §4) → fill and push the MultiMesh buffer →
/// the followed car's route (copied from the host) and rays (cast with the training's own <see cref="Sensors"/>).
/// None of it allocates; the debug rows (F3) show its cost and the main thread's bytes per frame.
/// </remarks>
public partial class TrainingView : Node2D
{
    /// <summary>Longest the render thread waits for the frame's tick at 1× and slower.</summary>
    private static readonly TimeSpan SlowSpeedWait = TimeSpan.FromMilliseconds(4);
    private const double HudInterval = 0.1;

    private Track _track = null!;
    private TrainingHostOptions _options = null!;
    private TrainingHost _host = null!;
    private CarRenderer _cars = null!;
    private RouteOverlay _route = null!;
    private ViewCamera _camera = null!;
    private Hud _hud = null!;
    private readonly FrameAllocMeter _alloc = new();
    private long _hudTextBytes;
    /// <summary>The clicked car (−1 = none) and the generation it belongs to.</summary>
    private int _selected = -1, _selectedGeneration = -1;
    private int _focus = -1;

    private AgentSnapshot _prev = null!, _curr = null!;
    private float[] _x = null!, _y = null!, _angle = null!;
    /// <summary>Positions drawn in the previous frame (the camera keeps pace with a new leader while it glides over).</summary>
    private float[] _lastX = null!, _lastY = null!;
    private TrainingStats _stats;
    private double _hudTimer, _renderMs;
    private bool _backRequested, _started;

    /// <summary>Raised when the user leaves the view (Esc or the Menu button). The owner frees the view, which stops the simulation.</summary>
    public event Action? BackRequested;

    /// <summary>The latest stats (for the debug command line).</summary>
    public TrainingStats Stats => _stats;
    public double RenderMs => _renderMs;
    public FrameAllocMeter Alloc => _alloc;
    public ViewCamera Camera => _camera;
    public int Selected => _selected;

    public static TrainingView Create(Track track, TrainingHostOptions options)
    {
        var view = new TrainingView { _track = track, _options = options };
        return view;
    }

    public override void _Ready()
    {
        RenderingServer.SetDefaultClearColor(MapTexture.Outside);

        AddChild(new Sprite2D
        {
            Texture = MapTexture.Create(_track.Grid, _track.Start),
            Centered = false,
            TextureFilter = TextureFilterEnum.Nearest,
        });

        _route = new RouteOverlay();
        AddChild(_route);

        int n = _options.PopulationSize;
        _cars = new CarRenderer();
        _cars.Setup(n, _options.Seed, new Vector2(_track.Grid.Width, _track.Grid.Height));
        AddChild(_cars);

        _camera = new ViewCamera { Position = new Vector2(_track.Start.X, _track.Start.Y) };
        AddChild(_camera);
        _camera.MakeCurrent();
        _camera.Clicked += PickCar;

        _hud = new Hud { TrackName = _track.Name };
        _hud.BackPressed += Back;
        _hud.PausePressed += () => Changed(_host.TogglePause);
        _hud.StepPressed += () => Changed(_host.StepTick);
        _hud.SpeedPicked += index => Changed(() => _host.SetSpeed(SpeedController.StepValue(index)));
        AddChild(_hud);

        _prev = new AgentSnapshot(n);
        _curr = new AgentSnapshot(n);
        _x = new float[n];
        _y = new float[n];
        _angle = new float[n];
        _lastX = new float[n];
        _lastY = new float[n];
        _host = new TrainingHost(_track, _options);
    }

    /// <summary>Stops the simulation threads (joins them); the view is being freed.</summary>
    public override void _ExitTree()
    {
        long start = Stopwatch.GetTimestamp();
        _host?.Dispose();
        if (OS.IsDebugBuild()) GD.Print($"training view closed: simulation stopped in {(Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency:0.0} ms");
    }

    public override void _Process(double delta)
    {
        bool wait = _host.Frame(delta);
        if (wait) _host.WaitForFrame(SlowSpeedWait);
        _host.ReadStats(out _stats);

        if (_stats.Ready)
        {
            if (!_started)
            {
                _started = true;
                _alloc.ResetCollections(); // count collections from the first generation on, not the set-up
            }
            DrawCars(delta);
        }
        _camera.PanWithKeys(delta);

        _hudTimer += delta;
        if (_hudTimer >= HudInterval)
        {
            _hudTimer = 0;
            long before = GC.GetAllocatedBytesForCurrentThread();
            _hud.Refresh(_stats, Engine.GetFramesPerSecond(), _renderMs, CameraText(), _alloc, _hudTextBytes);
            _hudTextBytes = GC.GetAllocatedBytesForCurrentThread() - before;
            _alloc.Exclude(_hudTextBytes);
        }
        _alloc.EndFrame();
    }

    /// <summary>The render path: snapshot → interpolation → MultiMesh → route and rays. Allocates nothing.</summary>
    private void DrawCars(double delta)
    {
        long start = Stopwatch.GetTimestamp();

        _host.Snapshots!.CopyTo(_prev, _curr);
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
        if (!_stats.Ready) return;
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
        if (car < 0 || car >= _options.PopulationSize) return;
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

    private void Back()
    {
        if (_backRequested) return;
        _backRequested = true;
        BackRequested?.Invoke();
    }

    public override void _UnhandledInput(InputEvent e)
    {
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
        if (e is not InputEventKey { Pressed: true } key) return;
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
            case Key.F:
                _camera.Mode = ViewCamera.CameraMode.FollowBest;
                _selected = -1;
                break;
            case Key.F3:
                _hud.DebugVisible = !_hud.DebugVisible;
                break;
            case Key.Escape:
                Back();
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
}
