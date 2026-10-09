using System;
using Godot;
using Nitrogenesis.Sim.Brain;
using Nitrogenesis.Sim.Core;
using Nitrogenesis.Sim.Map;
using Nitrogenesis.Sim.Racing;
using Nitrogenesis.Ui;

namespace Nitrogenesis.View;

/// <summary>
/// Drive a track yourself (PLAN §6.5, M2 part): keyboard or gamepad → <see cref="PlayerControls"/> →
/// <see cref="CarPhysics"/> through a <see cref="PlayerRun"/>, at 1× with the training view's interpolation.
/// Countdown, live timer, finish time, R = restart, Esc = back to the menu. The car is highlighted and draws its
/// route.
/// </summary>
/// <remarks>
/// Per frame: read the devices once → <see cref="SpeedController"/> at 1× gives the due ticks → that many
/// <see cref="PlayerRun.Tick"/>s → draw the car between the last two ticks (alpha = the tick fraction). The input
/// of a frame applies to all of its ticks. Physics: the default racing settings (ghosts and their physics come
/// with M6).
/// </remarks>
public partial class DriveView : Node2D
{
    private const float DriveZoom = 2f;
    private static readonly Color PlayerColor = new(0.96f, 0.96f, 0.98f);

    private Track _track = null!;
    private PlayerRun _run = null!;
    private readonly SpeedController _clock = new();
    private CarRenderer _car = null!;
    private RouteOverlay _route = null!;
    private ViewCamera _camera = null!;
    private DriveHud _hud = null!;
    private readonly FrameAllocMeter _alloc = new();
    private readonly float[] _x = new float[1], _y = new float[1], _angle = new float[1];
    private readonly AgentStatus[] _status = [AgentStatus.Running];
    private bool _backRequested, _started;

    // Debug autopilot: the reference driver's control law drives (screenshots, checks).
    private RacingMode? _mirror;
    private IAgentPolicy? _pilot;
    private readonly float[] _pilotOut = new float[RacingSettings.OutputCount];

    /// <summary>Raised when the user leaves (Esc, the Menu button or the gamepad's Back).</summary>
    public event Action? BackRequested;

    public PlayerRun Run => _run;
    public FrameAllocMeter Alloc => _alloc;
    public ViewCamera Camera => _camera;

    public static DriveView Create(Track track) => new() { _track = track };

    public override void _Ready()
    {
        RenderingServer.SetDefaultClearColor(MapTexture.Outside);
        var racing = new RacingTrack(_track, new RacingSettings());
        _run = new PlayerRun(racing);

        AddChild(new Sprite2D
        {
            Texture = MapTexture.Create(_track.Grid, _track.Start),
            Centered = false,
            TextureFilter = TextureFilterEnum.Nearest,
        });
        _route = new RouteOverlay(PlayerRun.MaxTrail + 1);
        AddChild(_route);
        _car = new CarRenderer { SelectedColor = UiTheme.Accent };
        _car.Setup(1, 0, new Vector2(_track.Grid.Width, _track.Grid.Height), PlayerColor);
        AddChild(_car);

        _camera = new ViewCamera { Position = new Vector2(_track.Start.X, _track.Start.Y), UserZoom = DriveZoom };
        AddChild(_camera);
        _camera.MakeCurrent();

        _hud = new DriveHud { TrackName = _track.Name };
        _hud.BackPressed += Back;
        AddChild(_hud);
    }

    /// <summary>Shows or hides the debug rows (also from the command line).</summary>
    public void ShowDebug(bool on) => _hud.DebugVisible = on;

    /// <summary>Debug: let the reference driver drive (its control law, through the same PlayerRun).</summary>
    public void EnableAutopilot()
    {
        _mirror = new RacingMode(_run.Track, 1, ReferenceDriver.MaxSeconds * RacingSettings.TicksPerSecond);
        _pilot = ReferenceDriver.CreatePilot(_mirror);
    }

    public override void _Process(double delta)
    {
        if (!_started)
        {
            _started = true;
            _alloc.ResetCollections(); // count collections while driving, not the set-up
        }
        ReadDevices(out int keyThrottle, out int keySteer, out float stickX, out float rightTrigger, out float leftTrigger);
        _clock.Advance(delta);
        int ticks = _clock.WholeTicks;
        for (int t = 0; t < ticks; t++)
        {
            if (_pilot is not null && _run.Phase == PlayerPhase.Driving)
            {
                _mirror!.SetCar(0, _run.Car);
                _pilot.Act(0, ReadOnlySpan<float>.Empty, _pilotOut);
                _run.Drive(_pilotOut[0], _pilotOut[1]);
            }
            else
            {
                _run.Tick(keyThrottle, keySteer, stickX, rightTrigger, leftTrigger);
            }
        }
        _clock.Consume(ticks);
        float alpha = _clock.Alpha;

        CarState prev = _run.PreviousCar, curr = _run.Car;
        _x[0] = prev.X + (curr.X - prev.X) * alpha;
        _y[0] = prev.Y + (curr.Y - prev.Y) * alpha;
        _angle[0] = SnapshotInterpolation.LerpAngle(prev.Heading * FastMath.RadiansPerUnit, curr.Heading * FastMath.RadiansPerUnit, alpha);
        _car.Render(_x, _y, _angle, _status, -1, 0);
        _camera.Follow(new Vector2(_x[0], _y[0]), 0, _run.Attempts, delta);

        int count = Math.Min(_run.TrailCount, _route.TrailX.Length - 1);
        Array.Copy(_run.TrailX, _route.TrailX, count);
        Array.Copy(_run.TrailY, _route.TrailY, count);
        _route.TrailX[count] = _x[0];
        _route.TrailY[count] = _y[0];
        _route.Show(count + 1, UiTheme.Accent, _camera.WorldPerPixel);

        long before = GC.GetAllocatedBytesForCurrentThread();
        _hud.Refresh(_run, alpha, _alloc);
        _alloc.Exclude(GC.GetAllocatedBytesForCurrentThread() - before);
        _alloc.EndFrame();
    }

    /// <summary>
    /// Keyboard (physical WASD positions and the arrows) and the first gamepad (left stick, triggers). Reading
    /// device 0 directly gives zeros when no pad is connected, and allocates nothing.
    /// </summary>
    private static void ReadDevices(out int keyThrottle, out int keySteer, out float stickX, out float rightTrigger, out float leftTrigger)
    {
        keyThrottle = (Held(Key.W, Key.Up) ? 1 : 0) - (Held(Key.S, Key.Down) ? 1 : 0);
        keySteer = (Held(Key.D, Key.Right) ? 1 : 0) - (Held(Key.A, Key.Left) ? 1 : 0);
        stickX = Input.GetJoyAxis(0, JoyAxis.LeftX);
        rightTrigger = Input.GetJoyAxis(0, JoyAxis.TriggerRight);
        leftTrigger = Input.GetJoyAxis(0, JoyAxis.TriggerLeft);
    }

    private static bool Held(Key letter, Key arrow) => Input.IsPhysicalKeyPressed(letter) || Input.IsKeyPressed(arrow);

    private void Restart()
    {
        _run.Restart(PlayerRun.RestartCountdownTicks);
        _clock.Consume(int.MaxValue >> 1); // drop the partial tick: the restart starts on a tick boundary
    }

    private void Back()
    {
        if (_backRequested) return;
        _backRequested = true;
        BackRequested?.Invoke();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        bool used = true;
        switch (e)
        {
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown }:
                _camera.HandleInput(e);
                break;
            case InputEventKey { Pressed: true, Echo: false, PhysicalKeycode: Key.R }:
                Restart();
                break;
            case InputEventKey { Pressed: true, Keycode: Key.Escape }:
                Back();
                break;
            case InputEventKey { Pressed: true, Keycode: Key.F3 }:
                _hud.DebugVisible = !_hud.DebugVisible;
                break;
            case InputEventJoypadButton { Pressed: true, ButtonIndex: JoyButton.Y or JoyButton.Start }:
                Restart();
                break;
            case InputEventJoypadButton { Pressed: true, ButtonIndex: JoyButton.Back }:
                Back();
                break;
            default:
                used = false;
                break;
        }
        if (used) GetViewport().SetInputAsHandled();
    }
}
