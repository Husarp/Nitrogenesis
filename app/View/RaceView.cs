using System;
using Godot;
using Nitrogenesis.Sim.Brain;
using Nitrogenesis.Sim.Core;
using Nitrogenesis.Sim.Map;
using Nitrogenesis.Sim.Racing;
using Nitrogenesis.Ui;

namespace Nitrogenesis.View;

/// <summary>
/// Race yourself (PLAN §6.5): you drive (keyboard or gamepad, as in <see cref="DriveView"/>) against ghosts, the
/// recorded best cars of saved generations. Countdown, live timer, splits against each ghost, a route line for every
/// car, R restarts at any time; at the finish the results screen shows the times and the route of everyone on the map.
/// </summary>
/// <remarks>
/// All the rules live in <see cref="Race"/>; this draws and reads the devices. Per frame: devices once → the 1×
/// <see cref="SpeedController"/> gives the due ticks → that many <see cref="Race.Tick"/>s → every car is drawn between
/// the last two ticks (ghosts too, see <see cref="Race.GhostPose"/>). Ghosts are the player's colours-apart, drawn a
/// little transparent; you are white with an orange outline and route.
/// </remarks>
public partial class RaceView : Node2D
{
    private const float DriveZoom = 2f;
    private static readonly Color PlayerColor = new(0.96f, 0.96f, 0.98f);
    private static readonly Color[] GhostColors =
    [
        new(0.35f, 0.80f, 1.00f), new(0.90f, 0.40f, 0.90f), new(0.60f, 0.90f, 0.30f), new(1.00f, 0.85f, 0.30f),
        new(1.00f, 0.45f, 0.40f), new(0.60f, 0.50f, 1.00f), new(0.30f, 0.90f, 0.70f), new(1.00f, 0.65f, 0.80f),
    ];

    private Track _track = null!;
    private Race _race = null!;
    private readonly SpeedController _clock = new();
    private CarRenderer _car = null!;
    private EngineVoice _engine = null!;
    private CarRenderer[] _ghostCars = [];
    private RouteOverlay _route = null!;
    private RouteOverlay[] _ghostRoutes = [];
    private ViewCamera _camera = null!;
    private RaceHud _hud = null!;
    private RaceResults? _results;
    private readonly float[] _x = new float[1], _y = new float[1], _angle = new float[1];
    private readonly AgentStatus[] _status = [AgentStatus.Running];
    private float[][] _gx = [], _gy = [], _ga = [];
    private AgentStatus[][] _gs = [];
    private bool _backRequested;

    // Debug autopilot: the reference driver's control law drives (screenshots, checks).
    private RacingMode? _mirror;
    private IAgentPolicy? _pilot;
    private readonly float[] _pilotOut = new float[RacingSettings.OutputCount];

    /// <summary>Raised when the user leaves (Esc, the Back button or the gamepad's Back).</summary>
    public event Action? BackRequested;

    public Race Race => _race;
    public ViewCamera Camera => _camera;

    /// <summary>The colour of ghost <paramref name="i"/> (its car, route, swatch in the HUD and results).</summary>
    public static Color GhostColor(int i) => GhostColors[i % GhostColors.Length];

    public static RaceView Create(Race race, Track track) => new() { _race = race, _track = track };

    /// <summary>Debug: the same as pressing Esc.</summary>
    public void DebugBack() => Back();

    /// <summary>Debug: let the reference driver drive (its control law, through the same race).</summary>
    public void EnableAutopilot()
    {
        _mirror = new RacingMode(_race.Track, 1, ReferenceDriver.MaxSeconds * RacingSettings.TicksPerSecond);
        _pilot = ReferenceDriver.CreatePilot(_mirror);
    }

    public override void _Ready()
    {
        RenderingServer.SetDefaultClearColor(MapTexture.Outside);
        var world = new Vector2(_track.Grid.Width, _track.Grid.Height);
        AddChild(new Sprite2D { Texture = MapTexture.Create(_track.Grid, _track.Start), Centered = false, TextureFilter = TextureFilterEnum.Nearest });

        int n = _race.Ghosts.Count;
        _ghostRoutes = new RouteOverlay[n];
        _ghostCars = new CarRenderer[n];
        _gx = new float[n][];
        _gy = new float[n][];
        _ga = new float[n][];
        _gs = new AgentStatus[n][];
        for (int i = 0; i < n; i++)
        {
            _ghostRoutes[i] = new RouteOverlay(RaceGhost.MaxRoutePoints + 2);
            AddChild(_ghostRoutes[i]);
            _gx[i] = new float[1];
            _gy[i] = new float[1];
            _ga[i] = new float[1];
            _gs[i] = [AgentStatus.Running];
        }
        _route = new RouteOverlay(PlayerRun.MaxTrail + 1);
        AddChild(_route);
        for (int i = 0; i < n; i++)
        {
            _ghostCars[i] = new CarRenderer { Modulate = new Color(1f, 1f, 1f, 0.8f) };
            _ghostCars[i].Setup(1, 0, world, GhostColor(i));
            AddChild(_ghostCars[i]);
        }
        _car = new CarRenderer { SelectedColor = UiTheme.Accent };
        _car.Setup(1, 0, world, PlayerColor);
        AddChild(_car);
        _engine = new EngineVoice();
        AddChild(_engine);

        _camera = new ViewCamera { Position = new Vector2(_track.Start.X, _track.Start.Y), UserZoom = DriveZoom };
        AddChild(_camera);
        _camera.MakeCurrent();

        _hud = RaceHud.Create(_race, _track.Name);
        _hud.BackPressed += Back;
        AddChild(_hud);
    }

    public override void _Process(double delta)
    {
        DriveView.ReadDevices(out int keyThrottle, out int keySteer, out float stickX, out float rightTrigger, out float leftTrigger);
        _clock.Advance(delta);
        int ticks = _clock.WholeTicks;
        for (int t = 0; t < ticks; t++)
        {
            if (_pilot is not null && _race.Run.Phase == PlayerPhase.Driving)
            {
                _mirror!.SetCar(0, _race.Run.Car);
                _pilot.Act(0, ReadOnlySpan<float>.Empty, _pilotOut);
                _race.Drive(_pilotOut[0], _pilotOut[1]);
            }
            else
            {
                _race.Tick(keyThrottle, keySteer, stickX, rightTrigger, leftTrigger);
            }
        }
        _clock.Consume(ticks);
        float alpha = _clock.Alpha;
        if (_race.Run.Phase == PlayerPhase.Finished && _results is null) ShowResults();
        bool results = _results is not null;

        var run = _race.Run;
        CarState prev = run.PreviousCar, curr = run.Car;
        _x[0] = prev.X + (curr.X - prev.X) * alpha;
        _y[0] = prev.Y + (curr.Y - prev.Y) * alpha;
        _angle[0] = SnapshotInterpolation.LerpAngle(prev.Heading * FastMath.RadiansPerUnit, curr.Heading * FastMath.RadiansPerUnit, alpha);
        _car.Visible = !results;
        if (!results) _car.Render(_x, _y, _angle, _status, -1, 0);
        _engine.Update(!results && run.Phase == PlayerPhase.Driving, curr.ForwardSpeed, _race.Track.Settings.MaxSpeed, 1); // PLAN §6.6: the player's engine
        if (!results) _camera.Follow(new Vector2(_x[0], _y[0]), 0, run.Attempts, delta);

        float pixel = _camera.WorldPerPixel;
        int count = Math.Min(run.TrailCount, _route.TrailX.Length - 1);
        Array.Copy(run.TrailX, _route.TrailX, count);
        Array.Copy(run.TrailY, _route.TrailY, count);
        _route.TrailX[count] = _x[0];
        _route.TrailY[count] = _y[0];
        _route.Show(count + 1, UiTheme.Accent, pixel);

        for (int i = 0; i < _ghostCars.Length; i++)
        {
            var ghost = _race.Ghosts[i];
            _race.GhostPose(i, alpha, out _gx[i][0], out _gy[i][0], out _ga[i][0]);
            _gs[i][0] = _race.GhostStatus(i);
            _ghostCars[i].Visible = !results;
            if (!results) _ghostCars[i].Render(_gx[i], _gy[i], _ga[i], _gs[i], -1, -1);
            // The route up to the tick drawn; on the results screen the whole of it.
            var route = _ghostRoutes[i];
            int points = ghost.CopyRoute(results ? ghost.LastTick : _race.Ticks, route.TrailX, route.TrailY);
            if (results) ghost.Pose(ghost.LastTick, 0f, out route.TrailX[points], out route.TrailY[points], out _);
            else (route.TrailX[points], route.TrailY[points]) = (_gx[i][0], _gy[i][0]); // the line ends at the car
            route.Show(points + 1, GhostColor(i), pixel);
        }

        _hud.Visible = !results; // the results panel takes its place
        _hud.Refresh(alpha);
    }

    /// <summary>The player finished: the results over the map, which is zoomed to show every route beside the panel.</summary>
    private void ShowResults()
    {
        _results = RaceResults.Create(_race.Standings());
        _results.RestartPressed += Restart;
        _results.BackPressed += Back;
        AddChild(_results);
        var view = GetViewportRect().Size;
        float zoom = Math.Min((view.X - RaceResults.PanelWidth - 60) / (_track.Grid.Width * ViewCamera.PixelsPerCell), (view.Y - 40) / (_track.Grid.Height * ViewCamera.PixelsPerCell));
        _camera.Mode = ViewCamera.CameraMode.Free;
        _camera.UserZoom = zoom;
        // The map's centre goes to the middle of the space right of the panel.
        _camera.Position = new Vector2(_track.Grid.Width, _track.Grid.Height) / 2 - new Vector2(RaceResults.PanelWidth / 2 / _camera.Zoom.X, 0);
    }

    private void Restart()
    {
        if (_results is not null)
        {
            _results.QueueFree();
            _results = null;
            _camera.Mode = ViewCamera.CameraMode.FollowBest;
            _camera.UserZoom = DriveZoom;
        }
        _race.Restart();
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
            case InputEventMouseButton or InputEventMouseMotion when _results is not null:
                _camera.HandleInput(e); // the results map can be dragged and zoomed
                break;
            case InputEventKey { Pressed: true, Echo: false, PhysicalKeycode: Key.R }:
                Restart();
                break;
            case InputEventKey { Pressed: true, Keycode: Key.Escape }:
                Back();
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
