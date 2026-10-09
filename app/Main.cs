using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using Nitrogenesis.Sim.Map;
using Nitrogenesis.Sim.Racing;
using Nitrogenesis.Ui;
using Nitrogenesis.View;

namespace Nitrogenesis;

/// <summary>
/// The app root: switches between the main menu, the track picker, the training view and the drive view.
/// </summary>
/// <remarks>
/// Command line (after <c>--</c>), every build (measuring on the target machine): <c>--check-tracks</c> lists the
/// bundled tracks and exits (the export check), <c>--track NAME</c> (skip the menu and watch training),
/// <c>--drive NAME</c> (skip the menu and drive), <c>--picker</c> (open the track picker), <c>--speed X|max</c>,
/// <c>--threads N</c>, <c>--pop N</c>, <c>--seed S</c>, <c>--zoom Z</c>, <c>--timings</c> (show the debug rows),
/// <c>--stats</c> (print timings every second), <c>--select N</c> (follow car N), <c>--quit-after SECONDS</c>.
/// Debug builds only (they inject input or write files): <c>--click-car N@T</c> (a real mouse click on car N at T
/// seconds), <c>--press KEY@A-B,…</c> (hold KEY from A to B seconds, through Godot's input), <c>--autopilot</c> (the
/// reference driver drives), <c>--screenshot PATH --after SECONDS</c> (save the frame and quit; several:
/// PATH@T,PATH@T…).
/// </remarks>
public partial class Main : Node
{
    /// <summary>Flags that inject input or write files: dropped in release builds.</summary>
    private static readonly string[] DebugOnlyArgs = ["click-car", "press", "autopilot", "screenshot", "after"];

    private Node? _screen;
    private Dictionary<string, string> _args = new();
    private double _clock, _statsTimer;
    private int _statsFrames;
    private double _statsRenderMs, _statsSimMs, _statsMaxFrame;
    private long _statsAlloc, _statsAllocMax;
    private bool _clicked;
    private readonly List<(Key Key, double From, double To, bool Down, bool Up)> _presses = new();
    private readonly List<(string Path, double At, bool Done)> _shots = new();

    public override void _Ready()
    {
        var version = (string)ProjectSettings.GetSetting("application/config/version");
        GetWindow().Title = $"Nitrogenesis {version}";
        _args = ParseArgs(OS.GetCmdlineUserArgs());

        if (_args.ContainsKey("check-tracks"))
        {
            var tracks = BundledTracks.LoadAll();
            foreach (var t in tracks) GD.Print($"track: {t.Name} {t.Grid.Width}x{t.Grid.Height} {TrackHash.Compute(t)}");
            GD.Print($"bundled tracks: {tracks.Count}");
            GetTree().Quit(tracks.Count > 0 ? 0 : 1);
            return;
        }
        if (!OS.IsDebugBuild())
            foreach (string debugOnly in DebugOnlyArgs) _args.Remove(debugOnly);
        ParseDebugInput();

        if (_args.TryGetValue("track", out string? name) || _args.TryGetValue("drive", out name))
        {
            var track = BundledTracks.LoadAll().Find(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(t.Name.Replace(' ', '_').Replace('-', '_'), name, StringComparison.OrdinalIgnoreCase));
            if (track is null)
            {
                GD.PushError($"--track/--drive: no bundled track '{name}'");
                ShowMenu();
            }
            else if (_args.ContainsKey("drive"))
            {
                StartDrive(track);
            }
            else
            {
                StartTraining(track);
            }
        }
        else if (_args.ContainsKey("picker"))
        {
            ShowPicker("Watch training", StartTraining);
        }
        else
        {
            ShowMenu();
        }
    }

    private void Switch(Node screen)
    {
        _screen?.QueueFree();
        _screen = screen;
        AddChild(screen);
    }

    private void ShowMenu()
    {
        RenderingServer.SetDefaultClearColor(UiTheme.Background);
        var menu = new MainMenu();
        menu.WatchTraining += () => ShowPicker("Watch training", StartTraining);
        menu.Drive += () => ShowPicker("Drive", StartDrive);
        Switch(menu);
    }

    private void ShowPicker(string heading, Action<Track> start)
    {
        var picker = TrackPicker.Create(heading);
        picker.Picked += start;
        picker.Back += ShowMenu;
        Switch(picker);
    }

    private void StartTraining(Track track)
    {
        var options = new TrainingHostOptions
        {
            Threads = Int("threads", CpuInfo.VisualThreads()),
            PopulationSize = Int("pop", 200),
            Seed = _args.TryGetValue("seed", out string? s) && ulong.TryParse(s, out ulong seed) ? seed : (ulong)Random.Shared.NextInt64(),
            Speed = _args.TryGetValue("speed", out string? sp) && sp.Equals("max", StringComparison.OrdinalIgnoreCase) ? double.PositiveInfinity : Double("speed", 1),
        };
        var view = TrainingView.Create(track, options);
        view.BackRequested += ShowMenu;
        Switch(view);
        if (_args.ContainsKey("zoom")) view.Camera.UserZoom = (float)Double("zoom", 1);
        if (_args.ContainsKey("timings") || _args.ContainsKey("screenshot")) view.ShowTimings(true);
        if (_args.ContainsKey("select")) view.CallDeferred(TrainingView.MethodName.SelectCar, Int("select", 0));
    }

    private void StartDrive(Track track)
    {
        var view = DriveView.Create(track);
        view.BackRequested += ShowMenu;
        Switch(view);
        if (_args.ContainsKey("zoom")) view.Camera.UserZoom = (float)Double("zoom", 2);
        if (_args.ContainsKey("autopilot")) view.EnableAutopilot();
        if (_args.ContainsKey("timings") || _args.ContainsKey("screenshot")) view.ShowDebug(true);
    }

    public override void _Process(double delta)
    {
        if (_args.Count == 0) return;
        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        _clock += delta;
        if (_args.ContainsKey("stats")) PrintStats(delta);
        RunDebugInput();
        if (_shots.Count > 0)
        {
            for (int i = 0; i < _shots.Count; i++)
            {
                if (_shots[i].Done || _clock < _shots[i].At) continue;
                _shots[i] = _shots[i] with { Done = true };
                TakeScreenshot(_shots[i].Path, quit: _shots.TrueForAll(s => s.Done));
                break;
            }
        }
        if (_args.ContainsKey("quit-after") && _clock >= Double("quit-after", 10)) GetTree().Quit();
        // The debug command line's own work is not the view's: keep it out of the allocation check.
        Alloc?.Exclude(GC.GetAllocatedBytesForCurrentThread() - allocBefore);
    }

    private FrameAllocMeter? Alloc => _screen switch
    {
        TrainingView t => t.Alloc,
        DriveView d => d.Alloc,
        _ => null,
    };

    /// <summary>Reads --press, --click-car and --screenshot (debug).</summary>
    private void ParseDebugInput()
    {
        if (_args.TryGetValue("press", out string? presses))
        {
            foreach (string spec in presses.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] keyAndTime = spec.Split('@');
                string[] range = keyAndTime[1].Split('-');
                var key = OS.FindKeycodeFromString(keyAndTime[0]);
                _presses.Add((key, Parse(range[0]), Parse(range[1]), false, false));
            }
        }
        if (_args.TryGetValue("screenshot", out string? shots))
        {
            foreach (string spec in shots.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                int at = spec.LastIndexOf('@');
                _shots.Add(at < 0 ? (spec, Double("after", 3), false) : (spec[..at], Parse(spec[(at + 1)..]), false));
            }
        }
        static double Parse(string v) => double.Parse(v, CultureInfo.InvariantCulture);
    }

    /// <summary>Feeds the --press keys and the --click-car click through Godot's input, like a user would.</summary>
    private void RunDebugInput()
    {
        for (int i = 0; i < _presses.Count; i++)
        {
            var p = _presses[i];
            if (!p.Down && _clock >= p.From)
            {
                Input.ParseInputEvent(new InputEventKey { Keycode = p.Key, PhysicalKeycode = p.Key, Pressed = true });
                _presses[i] = p with { Down = true };
            }
            else if (p.Down && !p.Up && _clock >= p.To)
            {
                Input.ParseInputEvent(new InputEventKey { Keycode = p.Key, PhysicalKeycode = p.Key, Pressed = false });
                _presses[i] = p with { Up = true };
            }
        }
        if (!_clicked && _args.TryGetValue("click-car", out string? click) && _screen is TrainingView view)
        {
            string[] parts = click.Split('@');
            if (_clock >= double.Parse(parts[1], CultureInfo.InvariantCulture))
            {
                _clicked = true;
                int car = int.Parse(parts[0], CultureInfo.InvariantCulture);
                Vector2 at = view.CarScreenPosition(car);
                GD.Print($"debug click on car {car} at {at}");
                foreach (bool pressed in new[] { true, false })
                    Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = pressed, Position = at, GlobalPosition = at });
            }
        }
    }

    private void PrintStats(double delta)
    {
        _statsFrames++;
        _statsTimer += delta;
        _statsMaxFrame = Math.Max(_statsMaxFrame, delta * 1000);
        if (_screen is TrainingView view)
        {
            _statsRenderMs += view.RenderMs;
            _statsSimMs += view.Stats.SimSecondsLastFrame * 1000;
        }
        if (Alloc is { } alloc)
        {
            _statsAlloc += alloc.LastFrame;
            _statsAllocMax = Math.Max(_statsAllocMax, alloc.LastFrame);
        }
        if (_statsTimer < 1) return;
        if (_screen is TrainingView v)
        {
            var s = v.Stats;
            GD.Print(string.Create(CultureInfo.InvariantCulture,
                $"stats t={_clock:0.0}s fps={_statsFrames / _statsTimer:0.0} maxFrame={_statsMaxFrame:0.0}ms gen={s.Generation} alive={s.Alive}/{s.PopulationSize} " +
                $"target={SpeedController(s.TargetSpeed)} actual={s.ActualSpeed:0.0} sim={_statsSimMs / _statsFrames:0.00}ms/frame " +
                $"render={_statsRenderMs / _statsFrames:0.000}ms/frame alloc={_statsAlloc}B (max {_statsAllocMax}B/frame) gc0={v.Alloc.Gen0} " +
                $"threads={s.Threads} camera={v.Camera.Mode} selected={v.Selected}"));
        }
        else if (_screen is DriveView d)
        {
            var r = d.Run;
            GD.Print(string.Create(CultureInfo.InvariantCulture,
                $"stats t={_clock:0.0}s fps={_statsFrames / _statsTimer:0.0} phase={r.Phase} time={r.TimeSeconds():0.00}s progress={r.Progress * 100:0}% " +
                $"speed={r.Car.ForwardSpeed:0.0} throttle={r.Controls.Throttle:0.00} steer={r.Controls.Steer:0.00} attempts={r.Attempts} " +
                $"alloc={_statsAlloc}B (max {_statsAllocMax}B/frame) gc0={d.Alloc.Gen0}"));
        }
        _statsFrames = 0;
        _statsTimer = _statsRenderMs = _statsSimMs = _statsMaxFrame = 0;
        _statsAlloc = _statsAllocMax = 0;
    }

    private static string SpeedController(double speed) => Nitrogenesis.Sim.Core.SpeedController.Label(speed);

    private async void TakeScreenshot(string path, bool quit)
    {
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var image = GetViewport().GetTexture().GetImage();
        string full = System.IO.Path.GetFullPath(path);
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        Error err = image.SavePng(full);
        GD.Print($"screenshot {full}: {err}");
        if (quit && !_args.ContainsKey("quit-after")) GetTree().Quit();
    }

    private int Int(string key, int fallback) =>
        _args.TryGetValue(key, out string? v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : fallback;

    private double Double(string key, double fallback) =>
        _args.TryGetValue(key, out string? v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : fallback;

    /// <summary>"--key value" pairs and bare "--flag"s.</summary>
    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var result = new Dictionary<string, string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) continue;
            string key = args[i][2..];
            string value = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "";
            result[key] = value;
        }
        return result;
    }
}
