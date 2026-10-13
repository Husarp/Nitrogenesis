using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Godot;
using Nitrogenesis.Sim.History;
using Nitrogenesis.Sim.Io;
using Nitrogenesis.Sim.Map;
using Nitrogenesis.Sim.Racing;
using Nitrogenesis.Editor;
using Nitrogenesis.Ui;
using Nitrogenesis.View;

namespace Nitrogenesis;

/// <summary>
/// The app root: switches between the main menu, the track picker, the Sessions list, the training view, the drive
/// view and the race (ghost picker + race view); opens sessions (PLAN §5.1) and saves the open one before the app closes.
/// </summary>
/// <remarks>
/// Command line (after <c>--</c>), every build (measuring on the target machine): <c>--check-tracks</c> lists the
/// bundled tracks and exits (the export check), <c>--track NAME</c> (skip the menu and start a new training, saved like
/// one from the menu unless <c>--no-save</c>), <c>--continue</c> (open the last session), <c>--session NAME</c>,
/// <c>--sessions</c> (the Sessions list), <c>--drive NAME</c>, <c>--race NAME [--ghosts all|none|N]</c> (race yourself on that track; the ghosts are the first N found, else the picker opens), <c>--picker [drive|race]</c>, <c>--editor [S|M|L|TRACKNAME]</c> (open the track editor at once, see <see cref="EditorView.DebugDo"/> for its --do verbs), <c>--speed X|max</c>, <c>--threads N</c>,
/// <c>--headless-threads N</c>, <c>--pop N</c>, <c>--seed S</c>, <c>--history-mb MB</c> (history memory limit, PLAN §4;
/// default: the Settings value, 1024 MB unless changed), <c>--autosave-seconds S</c> (default 120; 0 = save whenever the last save
/// finished, for crash tests), <c>--zoom Z</c>, <c>--timings</c>, <c>--stats</c>,
/// <c>--select N</c>, <c>--train-to N</c> (train without visuals to generation N once the training is ready, as the
/// Train to… button does; for timing exported builds), <c>--quit-after SECONDS</c>. The data folder is %APPDATA%\Nitrogenesis unless the environment
/// variable NITROGENESIS_DATA names another. Debug builds only (they inject input or write files):
/// <c>--monitor-hz HZ</c> (the refresh rate the FPS hint sees), <c>--click-car N@T</c>, <c>--press KEY@A-B,…</c>, <c>--autopilot</c>, <c>--screenshot PATH --after S</c> (or
/// PATH@T,…), and <c>--do "step;step;…"</c>: a script of training-control steps, each run when the previous one is done
/// (see <see cref="RunScriptStep"/>).
/// </remarks>
public partial class Main : Node
{
    /// <summary>Flags that inject input or write files: dropped in release builds.</summary>
    private static readonly string[] DebugOnlyArgs = ["click-car", "press", "autopilot", "screenshot", "after", "do", "monitor-hz"];

    private Node? _screen;
    private EditorView? _parkedEditor; // the editor while the drive view from its Drive button is up (out of the tree, not freed)
    private Dictionary<string, string> _args = new();
    private readonly SessionStore _store = SessionStore.Default();
    private double _clock, _statsTimer;
    private int _statsFrames;
    private double _statsRenderMs, _statsSimMs, _statsMaxFrame;
    private long _statsAlloc, _statsAllocMax;
    private bool _clicked;
    private readonly List<(Key Key, double From, double To, bool Down, bool Up)> _presses = new();
    private readonly List<(string Path, double At, bool Done)> _shots = new();

    // Leaving the training view (back to the menu, or quitting): a frame with "Saving the session…" is drawn first,
    // then the view is freed (it stops the simulation and waits for its last save), then _afterSave runs.
    private Action? _afterSave;
    private int _saveStage;
    private CanvasLayer? _savingLayer;
    private Control? _savingBox;
    private bool _quitting, _quitAfterSave;
    private PosixSignalRegistration[] _signals = [];
    private volatile bool _signalSeen;

    // Opening a session in the background.
    private Task<Session>? _loading;
    private string? _loadingName;

    // Looking for the ghosts of a track in the saved sessions, in the background.
    private sealed record GhostScan(Track Track, List<GhostEntry> Ghosts, List<string> Problems);
    private Task<GhostScan>? _scanning;

    // The --do script.
    private readonly Queue<string> _script = new();
    private double _scriptReadyAt;
    private bool _trainToDone;

    public override void _Ready()
    {
        var version = (string)ProjectSettings.GetSetting("application/config/version");
        GetWindow().Title = $"Nitrogenesis {version}";
        GetTree().AutoAcceptQuit = false; // the open session is saved first (_Notification)
        // A termination request from outside (kill, a service stop, Ctrl+C, the terminal closing) is handled like the
        // window's X: the session is saved first. A second one ends the app at once (the default), in case it hangs.
        _signals = new[] { PosixSignal.SIGTERM, PosixSignal.SIGHUP, PosixSignal.SIGINT }.Select(signal => PosixSignalRegistration.Create(signal, context =>
        {
            if (_signalSeen) return;
            _signalSeen = true;
            context.Cancel = true;
            Callable.From(() => RequestQuit(confirm: true)).CallDeferred();
        })).ToArray();
        _args = ParseArgs(OS.GetCmdlineUserArgs());
        if (_args.ContainsKey("monitor-hz")) FrameRate.HzOverride = Double("monitor-hz", 0);
        AddChild(new Sfx()); // the interface click, hooked to every button that enters the tree
        AppRuntime.Apply();  // FPS cap, VSync, priority and sound level from settings.json

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

        if (_args.TryGetValue("editor", out string? editorArg))
        {
            // --editor [S|M|L|TRACKNAME]: a new map of that size, or an editable copy of a bundled track
            if (Enum.TryParse<MapSize>(editorArg.Length == 0 ? "M" : editorArg, true, out var mapSize)) ShowEditor(EditDocument.New(mapSize));
            else if (FindBundled(editorArg) is { } source) ShowEditor(new EditDocument(source) { Name = source.Name + " copy" });
            else if (FindUserFile(editorArg) is { } userFile) ShowEditor(new EditDocument(TrackLibrary.Load(userFile)), userFile);
            else
            {
                GD.PushError($"--editor: no track '{editorArg}'");
                ShowMenu();
            }
        }
        else if (_args.TryGetValue("race", out string? raceName))
        {
            if (FindTrack(raceName) is { } raceTrack)
            {
                ScanForGhosts(raceTrack);
            }
            else
            {
                GD.PushError($"--race: no track '{raceName}'");
                ShowMenu();
            }
        }
        else if (_args.TryGetValue("track", out string? name) || _args.TryGetValue("drive", out name))
        {
            var track = FindTrack(name);
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
                StartNewTraining(track);
            }
        }
        else if (_args.ContainsKey("continue"))
        {
            ContinueLatest();
        }
        else if (_args.TryGetValue("session", out string? session))
        {
            OpenSession(session);
        }
        else if (_args.ContainsKey("sessions"))
        {
            ShowSessions();
        }
        else if (_args.ContainsKey("picker"))
        {
            if (_args["picker"].Equals("drive", StringComparison.OrdinalIgnoreCase)) ShowPicker("Drive", StartDrive);
            else if (_args["picker"].Equals("race", StringComparison.OrdinalIgnoreCase)) ShowPicker("Race yourself", ScanForGhosts);
            else ShowPicker("New training", StartNewTraining);
        }
        else
        {
            ShowMenu();
        }
    }

    /// <summary>A bundled track, else one of the user's saved tracks (matched by name, ignoring case).</summary>
    private static Track? FindTrack(string name)
    {
        if (FindBundled(name) is { } bundled) return bundled;
        if (FindUserFile(name) is not { } file) return null;
        try
        {
            return TrackLibrary.Load(file);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            GD.PushError($"Track {file}: {e.Message}");
            return null;
        }
    }

    private static string? FindUserFile(string name) =>
        TrackLibrary.Default().List().Find(i => i.CanOpen && string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase))?.Path;

    private static Track? FindBundled(string name) =>
        BundledTracks.LoadAll().Find(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)
                                          || string.Equals(t.Name.Replace(' ', '_').Replace('-', '_'), name, StringComparison.OrdinalIgnoreCase));

    private void Switch(Node screen)
    {
        _screen?.QueueFree();
        _parkedEditor?.QueueFree(); // leaving for another screen while the editor was parked: it is really gone now
        _parkedEditor = null;
        _screen = screen;
        AddChild(screen);
    }

    // ---------------------------------------------------------------- screens

    private void ShowMenu()
    {
        RenderingServer.SetDefaultClearColor(UiTheme.Background);
        SessionInfo? latest = null;
        try
        {
            latest = _store.Latest();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            GD.PushError($"Sessions folder: {e.Message}");
        }
        var menu = MainMenu.Create(latest);
        menu.NewTraining += () => ShowPicker("New training", StartNewTraining);
        menu.Continue += ContinueLatest;
        menu.Sessions += () => ShowSessions();
        menu.Race += () => ShowPicker("Race yourself", ScanForGhosts);
        menu.Editor += () => ShowEditor(EditDocument.New(MapSize.M));
        menu.Settings += ShowSettings;
        Switch(menu);
    }

    private void ShowSettings()
    {
        RenderingServer.SetDefaultClearColor(UiTheme.Background);
        var screen = SettingsScreen.Create();
        screen.Back += ShowMenu;
        Switch(screen);
    }

    private void ShowEditor(EditDocument doc, string? filePath = null)
    {
        RenderingServer.SetDefaultClearColor(UiTheme.Background);
        var editor = EditorView.Create(doc, filePath);
        editor.BackRequested += ShowMenu;
        editor.DriveRequested += track => DriveFromEditor(editor, track);
        Switch(editor);
    }

    /// <summary>
    /// The editor's Drive button: the editor leaves the tree but is kept (its map, undo history, camera and tool state live in
    /// the instance), the drive view takes its place, and <see cref="ReturnToEditor"/> swaps them back.
    /// </summary>
    private void DriveFromEditor(EditorView editor, Track track)
    {
        if (_screen != editor || _parkedEditor is not null) return;
        RemoveChild(editor); // _ExitTree runs (the analysis thread stops, hooks come off); _Ready will not run again
        _parkedEditor = editor;
        var view = DriveView.Create(track);
        view.BackToEditor = true;
        view.BackRequested += () => Callable.From(ReturnToEditor).CallDeferred(); // not inside the key event that asked for it
        _screen = view;
        AddChild(view);
        ConfigureDrive(view);
    }

    private void ReturnToEditor()
    {
        if (_parkedEditor is not { } editor) return;
        _parkedEditor = null;
        if (_screen is { } drive)
        {
            RemoveChild(drive); // out of the tree now, so its camera cannot outlive the swap
            drive.QueueFree();
        }
        RenderingServer.SetDefaultClearColor(UiTheme.Background); // the drive view set its own
        _screen = editor;
        AddChild(editor);
    }

    private void ShowPicker(string heading, Action<Track> start)
    {
        RenderingServer.SetDefaultClearColor(UiTheme.Background);
        var picker = TrackPicker.Create(heading);
        picker.Picked += start;
        picker.Back += ShowMenu;
        Switch(picker);
    }

    private void ShowSessions(string? message = null, string? select = null)
    {
        RenderingServer.SetDefaultClearColor(UiTheme.Background);
        var screen = SessionsScreen.Create(_store, message, select);
        screen.Open += name => OpenSession(name);
        screen.Back += ShowMenu;
        Switch(screen);
    }

    /// <summary>What a training starts with: the Settings screen's values (PLAN §6.4), unless a command-line flag says otherwise.</summary>
    private TrainingHostOptions Options(SessionFolder? folder)
    {
        var settings = AppRuntime.Settings;
        return new()
        {
            Threads = Int("threads", settings.VisualThreads),
            HeadlessThreads = Int("headless-threads", settings.HeadlessThreads),
            PopulationSize = Int("pop", settings.PopulationSize),
            Hidden1 = settings.Hidden1,
            Hidden2 = settings.Hidden2,
            Settings = settings.Racing,
            Evolution = settings.Evolution,
            Seed = _args.TryGetValue("seed", out string? s) && ulong.TryParse(s, out ulong seed) ? seed : (ulong)Random.Shared.NextInt64(),
            Speed = _args.TryGetValue("speed", out string? sp) && sp.Equals("max", StringComparison.OrdinalIgnoreCase) ? double.PositiveInfinity : Double("speed", settings.Speed),
            Folder = folder,
            HistoryMemoryLimit = _args.ContainsKey("history-mb") ? (long)(Double("history-mb", 1024) * 1048576) : settings.HistoryLimitBytes,
            AutosaveInterval = _args.ContainsKey("autosave-seconds") ? TimeSpan.FromSeconds(Double("autosave-seconds", 120)) : TimeSpan.FromMinutes(2),
        };
    }

    /// <summary>New training: a new session folder ("&lt;track&gt; date time"), saved at once and then automatically.</summary>
    private void StartNewTraining(Track track)
    {
        bool save = !_args.ContainsKey("no-save");
        SessionFolder? folder = null;
        try
        {
            if (save) folder = _store.Create(SessionStore.DefaultName(track.Name, DateTime.Now));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowMenu();
            Modal.Message(_screen!, "Could not create the session", e.Message);
            return;
        }
        var options = Options(folder);
        var host = new TrainingHost(track, options);
        ShowTraining(host, track, options, options.Seed, save);
    }

    private void ContinueLatest()
    {
        SessionInfo? latest = null;
        try
        {
            latest = _store.Latest();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            GD.PushError(e.Message);
        }
        if (latest is null) ShowMenu();
        else OpenSession(latest.Name);
    }

    /// <summary>Loads a session on a background task (a big history takes a moment) and opens it.</summary>
    private void OpenSession(string name)
    {
        RenderingServer.SetDefaultClearColor(UiTheme.Background);
        var screen = new CenterContainer { Theme = UiTheme.Theme };
        screen.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        screen.AddChild(UiTheme.Label($"Opening {name}…", 22, UiTheme.Muted));
        Switch(screen);
        SessionFolder folder;
        try
        {
            folder = _store.Folder(name);
        }
        catch (ArgumentException e)
        {
            ShowSessions(e.Message);
            return;
        }
        _loadingName = name;
        _loading = Task.Run(() => Session.Load(folder));
    }

    private void FinishOpening()
    {
        var task = _loading!;
        string name = _loadingName!;
        _loading = null;
        if (task.Exception?.GetBaseException() is { } error)
        {
            ShowSessions(error.Message, name);
            return;
        }
        var session = task.Result;
        if (session.ReadOnly)
        {
            RenderingServer.SetDefaultClearColor(UiTheme.Background);
            var history = HistoryScreen.Create(session);
            history.Back += () => ShowSessions(null, name);
            Switch(history);
            return;
        }
        var folder = _store.Folder(name);
        var options = Options(folder);
        TrainingHost host;
        try
        {
            host = new TrainingHost(session, options);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowSessions(e is SessionLockedException ? "This session is open in another Nitrogenesis window." : e.Message, name);
            return;
        }
        if (_args.ContainsKey("speed")) host.SetSpeed(options.Speed); // the flag wins over the speed the session was left at
        Track track;
        ulong colorSeed;
        lock (session.SyncRoot)
        {
            track = session.SegmentAt(session.Current.Branch, session.Current.Generation).Track;
            colorSeed = session.Branches[0].Seed;
        }
        foreach (string warning in session.Warnings) GD.PushWarning($"Session {name}: {warning}");
        ShowTraining(host, track, options, colorSeed, saves: true);
        if (session.Warnings.Count > 0) ((TrainingView)_screen!).ShowMessage("Session opened with problems", string.Join("\n", session.Warnings));
    }

    private void ShowTraining(TrainingHost host, Track track, TrainingHostOptions options, ulong colorSeed, bool saves)
    {
        var view = TrainingView.Create(host, track, options, colorSeed, saves);
        view.BackRequested += () => AfterSaving(ShowMenu);
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
        ConfigureDrive(view);
    }

    private void ConfigureDrive(DriveView view)
    {
        if (_args.ContainsKey("zoom")) view.Camera.UserZoom = (float)Double("zoom", 2);
        if (_args.ContainsKey("autopilot")) view.EnableAutopilot();
        if (_args.ContainsKey("timings") || _args.ContainsKey("screenshot")) view.ShowDebug(true);
    }

    // ---------------------------------------------------------------- race yourself

    /// <summary>Race yourself, step 2: looks in the saved sessions for ghosts of the track (a big history takes a moment) and opens the ghost picker.</summary>
    private void ScanForGhosts(Track track)
    {
        RenderingServer.SetDefaultClearColor(UiTheme.Background);
        var screen = new CenterContainer { Theme = UiTheme.Theme };
        screen.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        screen.AddChild(UiTheme.Label("Looking for ghosts…", 22, UiTheme.Muted));
        Switch(screen);
        string hash = TrackHash.Compute(track);
        _scanning = Task.Run(() =>
        {
            var problems = new List<string>();
            return new GhostScan(track, GhostCatalog.Scan(_store, hash, problems), problems);
        });
    }

    private void FinishScanning()
    {
        var task = _scanning!;
        _scanning = null;
        if (task.Exception?.GetBaseException() is { } error)
        {
            ShowMenu();
            Modal.Message(_screen!, "Could not look for ghosts", error.Message);
            return;
        }
        var scan = task.Result;
        foreach (string problem in scan.Problems) GD.PushWarning($"Ghosts: {problem}");
        if (_args.TryGetValue("ghosts", out string? count))
        {
            // --ghosts all|none|N: skip the picker (measuring and screenshots)
            int n = count.Equals("none", StringComparison.OrdinalIgnoreCase) ? 0 : count.Equals("all", StringComparison.OrdinalIgnoreCase) ? Race.MaxGhosts
                : int.TryParse(count, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : 0;
            StartRace(scan, scan.Ghosts.Take(Math.Min(n, Race.MaxGhosts)).ToList());
            return;
        }
        ShowGhostPicker(scan);
    }

    private void ShowGhostPicker(GhostScan scan)
    {
        RenderingServer.SetDefaultClearColor(UiTheme.Background);
        var picker = GhostPicker.Create(scan.Track, scan.Ghosts);
        picker.Picked += chosen => StartRace(scan, chosen);
        picker.Back += () => ShowPicker("Race yourself", ScanForGhosts);
        Switch(picker);
    }

    private void StartRace(GhostScan scan, IReadOnlyList<GhostEntry> chosen)
    {
        Race race;
        try
        {
            race = Race.Create(scan.Track, chosen);
        }
        catch (InvalidDataException e)
        {
            ShowGhostPicker(scan);
            Modal.Message(_screen!, "Could not start the race", $"A ghost's recording is damaged: {e.Message}");
            return;
        }
        var view = RaceView.Create(race, scan.Track);
        view.BackRequested += () => ShowGhostPicker(scan);
        Switch(view);
        if (_args.ContainsKey("zoom")) view.Camera.UserZoom = (float)Double("zoom", 2);
        if (_args.ContainsKey("autopilot")) view.EnableAutopilot();
    }

    // ---------------------------------------------------------------- quitting

    public override void _Notification(int what)
    {
        if (what == NotificationWMCloseRequest) RequestQuit(confirm: true);
        else if (what == NotificationApplicationFocusOut) FrameRate.SetFocused(false); // PLAN §4: 15 FPS while unfocused
        else if (what == NotificationApplicationFocusIn) FrameRate.SetFocused(true);
    }

    /// <summary>Closes the app after saving the open session (PLAN §5.1 "on exit"), see <see cref="AfterSaving"/>.</summary>
    /// <param name="confirm">The user (window X, a signal) asked: an editor with unsaved changes asks "Save changes?" first. Debug quits pass false.</param>
    private void RequestQuit(bool confirm = false)
    {
        if (_quitting) return;
        if (confirm && (_screen as EditorView ?? _parkedEditor) is { Document.IsDirty: true } editor)
        {
            if (Modal.OpenCount > 0)
            {
                if (Modal.UnsavedQuestionOpen) return; // the question is already up
                Modal.CloseAll(); // another box (Open list, name box, a message): close it, then ask
            }
            if (_parkedEditor is not null)
            {
                // While driving: back to the editor to ask. Deferred: the close notification blocks adding children to this node.
                Callable.From(() =>
                {
                    ReturnToEditor();
                    editor.ConfirmLeave(() => RequestQuit());
                }).CallDeferred();
                return;
            }
            editor.ConfirmLeave(() => RequestQuit());
            return;
        }
        if (_afterSave is not null)
        {
            _quitAfterSave = true; // leaving the view is already under way: quit right after it
            return;
        }
        _quitting = true;
        AfterSaving(() => GetTree().Quit());
    }

    /// <summary>
    /// Leaves the current screen and then runs <paramref name="next"/>. From the training view: first a frame with
    /// "Saving the session…" is drawn, then the view is freed (it stops the simulation and waits for its last save),
    /// so the next screen (the menu's Continue line, the Sessions list) already sees that save. Other screens go at once.
    /// </summary>
    private void AfterSaving(Action next)
    {
        if (_afterSave is not null) return;
        if (_screen is not TrainingView)
        {
            next();
            return;
        }
        _afterSave = next;
        _saveStage = 1;
        _savingLayer = new CanvasLayer { Layer = 100 };
        var center = new CenterContainer { Theme = UiTheme.Theme };
        center.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var panel = new PanelContainer();
        panel.AddChild(UiTheme.Label("Saving the session…", 22, UiTheme.Text));
        center.AddChild(panel);
        _savingBox = center;
        _savingLayer.AddChild(center);
        // Deferred: the window-close notification runs while this node is blocked from adding children.
        CallDeferred(Node.MethodName.AddChild, _savingLayer);
    }

    /// <summary>The frames of <see cref="AfterSaving"/>: true while it runs (the rest of the frame is skipped).</summary>
    private bool RunAfterSaving()
    {
        if (_afterSave is null) return false;
        if (_saveStage == 1)
        {
            _saveStage = 2; // let the "Saving…" frame draw first
            return true;
        }
        if (_saveStage == 3) return false; // the "could not save" box is open: the frame goes on (the box and the debug script)
        if (_screen is TrainingView view)
        {
            view.StopAndSave(); // stops the simulation and waits for the last save; the view is still there afterwards
            if (view.ExitSaveError is not null)
            {
                _saveStage = 3; // waiting for the answer to the "could not save" box
                AskAboutFailedSave(view);
                return true;
            }
        }
        FinishLeaving();
        return true;
    }

    /// <summary>
    /// The save on leaving failed (disk full, folder locked or read-only…). The training is stopped but still in memory,
    /// so the choice is: try the save again (after the user fixed the problem), or leave and lose what was trained since
    /// the last save that worked. The box has no other way out (Esc asks again), so nothing is lost by accident.
    /// </summary>
    private void AskAboutFailedSave(TrainingView view)
    {
        GD.PushError($"Saving the session on leaving failed: {view.ExitSaveError}");
        _savingBox!.Visible = false;
        string since = view.LastSavedLocal is { } at ? $"since {at:HH:mm}" : "of this session";
        var body = new VBoxContainer();
        body.AddThemeConstantOverride("separation", 10);
        body.AddChild(Modal.Text($"The session could not be saved:\n{view.ExitSaveError}"));
        body.AddChild(Modal.Text($"What was trained {since} exists only in memory. Fix the problem (free disk space, make the folder writable) and press Try again, or leave and lose it.", UiTheme.Muted));
        bool chosen = false;
        var box = Modal.Open(_savingLayer!, "Could not save the session", body,
            new Modal.Choice("Leave without saving", () =>
            {
                chosen = true;
                FinishLeaving();
            }),
            new Modal.Choice("Try again", () =>
            {
                chosen = true;
                if (view.RetryExitSave()) FinishLeaving();
                else AskAboutFailedSave(view);
            }, Primary: true));
        // Esc closes a box without an answer: ask again, so nothing is lost by accident.
        box.Closed += () => Callable.From(() =>
        {
            if (!chosen) AskAboutFailedSave(view);
        }).CallDeferred();
    }

    /// <summary>Frees the stopped training view and runs what was waiting for it (the menu, the next session, quitting).</summary>
    private void FinishLeaving()
    {
        var next = _afterSave;
        _afterSave = null;
        _saveStage = 0;
        _screen?.Free(); // _ExitTree: the host is already stopped and saved (or the user chose to leave)
        _screen = null;
        _savingLayer?.QueueFree();
        _savingLayer = null;
        _savingBox = null;
        next?.Invoke();
        if (_quitAfterSave)
        {
            _quitAfterSave = false;
            RequestQuit(confirm: true);
        }
    }

    // ---------------------------------------------------------------- per frame

    public override void _Process(double delta)
    {
        if (RunAfterSaving()) return;
        if (_loading is { IsCompleted: true }) FinishOpening();
        if (_scanning is { IsCompleted: true }) FinishScanning();
        if (_args.Count == 0) return;
        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        _clock += delta;
        if (_args.ContainsKey("stats")) PrintStats(delta);
        RunDebugInput();
        RunScript();
        if (!_trainToDone && _args.ContainsKey("train-to") && _screen is TrainingView { IsIdle: true } trainingView)
        {
            _trainToDone = true;
            trainingView.StartTrainTo(TrainToCondition.ToGeneration(Int("train-to", 100)));
        }
        if (_shots.Count > 0)
        {
            for (int i = 0; i < _shots.Count; i++)
            {
                if (_shots[i].Done || _clock < _shots[i].At) continue;
                _shots[i] = _shots[i] with { Done = true };
                TakeScreenshot(_shots[i].Path, quit: _shots.TrueForAll(s => s.Done) && _script.Count == 0);
                break;
            }
        }
        if (_args.ContainsKey("quit-after") && _clock >= Double("quit-after", 10)) RequestQuit();
        // The debug command line's own work is not the view's: keep it out of the allocation check.
        Alloc?.Exclude(GC.GetAllocatedBytesForCurrentThread() - allocBefore);
    }

    private FrameAllocMeter? Alloc => _screen switch
    {
        TrainingView t => t.Alloc,
        DriveView d => d.Alloc,
        _ => null,
    };

    // ---------------------------------------------------------------- debug: --do script

    /// <summary>Runs the next --do step once the previous step's minimum wait is over and (for most steps) the screen is idle.</summary>
    private void RunScript()
    {
        if (_script.Count == 0 || _clock < _scriptReadyAt || _loading is not null || _scanning is not null) return;
        string step = _script.Peek();
        int colon = step.IndexOf(':');
        string verb = colon < 0 ? step : step[..colon], arg = colon < 0 ? "" : step[(colon + 1)..];
        // While the training view is being left only the steps that watch or answer a box run (the "could not save" box).
        if (_afterSave is not null && verb is not ("wait" or "shot" or "modal-close" or "modal-ok" or "modal-button")) return;
        // These run at once; the others wait until the training view has nothing in progress (train-to, a rebuild…).
        bool immediate = verb is "wait" or "shot" or "fingerprint" or "fps" or "focus" or "cancel" or "quit" or "close-request" or "modal-close" or "modal-ok" or "modal-button";
        if (!immediate && _screen is TrainingView view && !view.IsIdle) return;
        if (!immediate && _screen is EditorView { DebugBusy: true }) return;
        if ((!immediate || verb == "shot") && _screen is EditorView { TestRunning: true }) return; // wait for the Test result
        _script.Dequeue();
        GD.Print($"script t={_clock:0.0}s: {step}");
        _scriptReadyAt = _clock + 0.3;
        string? error = RunScriptStep(verb, arg);
        if (error is not null) GD.PushError($"script step '{step}': {error}");
    }

    /// <summary>
    /// One --do step. Main's own: <c>wait:S</c>, <c>shot:PATH</c>, <c>fingerprint</c>, <c>quit</c>, <c>menu</c>,
    /// <c>continue</c>, <c>sessions</c>, <c>open:NAME</c>, <c>rename:NEW</c> (the newest session), <c>delete:NAME</c>,
    /// <c>pick:NAME</c> and <c>start</c> (track picker), <c>settings</c> (menu), <c>set:KEY=VALUE</c>, <c>preset:NAME</c>, <c>tab:N</c> and <c>back</c> (Settings screen), <c>focus:on|off</c>, <c>fps</c>, <c>ghosts:all|none|0,2</c>, <c>ghost-click:N</c> and <c>race</c> (ghost picker), <c>back</c> (drive and race views), <c>modal-button:LABEL</c>, <c>rename-dialog</c> and <c>delete-dialog</c> (Sessions list, selected row), <c>new:TRACK</c>. Everything else goes to <see cref="TrainingView.DebugDo"/> (trainto:N, fork:G,
    /// forkbest:G, switch:NAME, panel[:tab], graph, replay-best:G, replay-gen:G, keep-training:on, back, mode:step, next,
    /// nextgen, pause, resume, speed:X, mutation:X, timelimit:S, traintodialog, forkdialog:G, modal-close, …).
    /// </summary>
    private string? RunScriptStep(string verb, string arg)
    {
        switch (verb)
        {
            case "wait":
                _scriptReadyAt = _clock + double.Parse(arg, CultureInfo.InvariantCulture);
                return null;
            case "shot":
                TakeScreenshot(arg, quit: false);
                return null;
            case "fingerprint":
                GD.Print("fingerprint " + Fingerprint());
                return null;
            case "quit":
                RequestQuit();
                return null;
            case "close-request": // the window's X: the notification the OS sends, through the node tree
                GetTree().Root.PropagateNotification((int)NotificationWMCloseRequest);
                return null;
            case "menu":
                AfterSaving(ShowMenu);
                return null;
            case "continue":
                AfterSaving(ContinueLatest);
                return null;
            case "sessions":
                AfterSaving(() => ShowSessions());
                return null;
            case "open" when _screen is not EditorView:
                AfterSaving(() => OpenSession(arg));
                return null;
            case "new" when _screen is not EditorView:
                return FindTrack(arg) is { } track ? Do(() => AfterSaving(() => StartNewTraining(track))) : $"no track '{arg}'";
            case "rename" when _screen is SessionsScreen sessions:
                var latest = _store.List();
                return latest.Count == 0 ? "no session" : Do(() => sessions.DoRename(latest[0].Name, arg));
            case "delete" when _screen is SessionsScreen sessions:
                sessions.DoDelete(arg);
                return null;
            case "delete-dialog" when _screen is SessionsScreen sessions:
                sessions.AskDelete();
                return null;
            case "rename-dialog" when _screen is SessionsScreen sessions:
                sessions.AskRename();
                return null;
            case "replay-best" when _screen is HistoryScreen history:
                history.WatchBest(int.Parse(arg, CultureInfo.InvariantCulture));
                return null;
            case "back" when _screen is HistoryScreen history:
                history.BackFromReplay();
                return null;
            case "settings" when _screen is MainMenu:
                ShowSettings();
                return null;
            case "set" when _screen is SettingsScreen settingsScreen:
            {
                string[] kv = arg.Split('=', 2);
                return kv.Length == 2 ? settingsScreen.DebugSet(kv[0], kv[1]) : "set:KEY=VALUE";
            }
            case "preset" when _screen is SettingsScreen settingsScreen:
                settingsScreen.DebugPreset(arg);
                return null;
            case "scroll" when _screen is SettingsScreen settingsScreen:
                settingsScreen.DebugScroll(int.Parse(arg, CultureInfo.InvariantCulture));
                return null;
            case "tab" when _screen is SettingsScreen settingsScreen:
                settingsScreen.DebugTab(int.Parse(arg, CultureInfo.InvariantCulture));
                return null;
            case "back" when _screen is SettingsScreen settingsScreen:
                settingsScreen.DebugBack();
                return null;
            case "focus": // the window gains or loses the focus (on|off)
                FrameRate.SetFocused(arg != "off");
                GD.Print($"focus {arg}: Engine.MaxFps={Engine.MaxFps} vsync={DisplayServer.WindowGetVsyncMode()}");
                return null;
            case "fps":
                GD.Print($"fps: cap={Engine.MaxFps} current={Engine.GetFramesPerSecond():0} monitorHz={FrameRate.MonitorHz} vsync={DisplayServer.WindowGetVsyncMode()} priority={CpuPriority.Current()} clicks={Sfx.ClicksPlayed}");
                return null;
            case "back" when _screen is DriveView drive:
                drive.DebugBack();
                return null;
            case "back" when _screen is RaceView race:
                race.DebugBack();
                return null;
            case "ghosts" when _screen is GhostPicker ghostPicker:
                ghostPicker.DebugSelect(arg);
                return null;
            case "ghost-click" when _screen is GhostPicker ghostPicker:
                ghostPicker.DebugClick(int.Parse(arg, CultureInfo.InvariantCulture));
                return null;
            case "race" when _screen is GhostPicker ghostPicker:
                ghostPicker.DebugStart();
                return null;
            case "key": // a real key press through Godot's input: key:Escape, key:T …
            {
                // key:NAME or with modifiers: key:ctrl+z, key:ctrl+shift+z, key:ctrl+s
                string[] parts = arg.Split('+');
                var key = OS.FindKeycodeFromString(parts[^1]);
                if (key == Key.None) return $"unknown key '{arg}'";
                bool ctrl = false, shift = false, alt = false;
                foreach (string m in parts[..^1])
                {
                    switch (m.ToLowerInvariant())
                    {
                        case "ctrl": ctrl = true; break;
                        case "shift": shift = true; break;
                        case "alt": alt = true; break;
                        default: return $"unknown modifier '{m}' in '{arg}'";
                    }
                }
                foreach (bool pressed in new[] { true, false })
                    Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = pressed, CtrlPressed = ctrl, ShiftPressed = shift, AltPressed = alt });
                return null;
            }
            case "modal-close":
                GetViewport().PushInput(new InputEventKey { Keycode = Key.Escape, Pressed = true });
                return null;
            case "pick" when _screen is TrackPicker picker:
                return picker.DebugPick(arg) ? null : $"no track '{arg}' in the list";
            case "start" when _screen is TrackPicker picker:
                picker.DebugStart();
                return null;
            case "modal-button":
                return Modal.DebugPress(arg) ? null : $"no box with a button '{arg}'";
            case "modal-ok":
                GetViewport().PushInput(new InputEventKey { Keycode = Key.Enter, Pressed = true });
                return null;
        }
        if (_screen is EditorView editor) return editor.DebugDo(verb, arg);
        return _screen is TrainingView view ? view.DebugDo(verb, arg) : $"'{verb}' needs the training view";

        static string? Do(Action action)
        {
            action();
            return null;
        }
    }

    /// <summary>
    /// A short hash of the open session's state (active branch, current generation and population, branches, segments,
    /// evolution settings and every stored generation), so two runs can be compared ("Continue picks up exactly where it
    /// left off"). The current population is read under the session lock: it is only written after it stopped being current.
    /// </summary>
    private string Fingerprint()
    {
        if (_screen is not TrainingView { Host.Session: { } s }) return "(no session)";
        lock (s.SyncRoot)
        {
            var text = new System.Text.StringBuilder();
            text.Append($"active {s.ActiveBranch} current {s.Current.Branch}/{s.Current.Generation}/seg{s.Current.Segment} {s.Current.Params} {s.Current.Population.ContentHash():X16}\n");
            text.Append($"evolution {s.Evolution}\n");
            foreach (var b in s.Branches) text.Append($"branch {b}\n");
            foreach (var g in s.Segments) text.Append($"segment {g.Id} {g.Branch} {g.FirstGeneration} {g.Config.Hash} {g.PopulationSize}\n");
            int entries = 0;
            foreach (var b in s.History.Branches)
            {
                foreach (var e in b.Entries)
                {
                    entries++;
                    var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(e.BestGenome.Weights.AsSpan());
                    text.Append($"{b.Name} {e.Generation} {e.Seed:X} {e.Params} {e.Stats} {e.Ticks} {Crc32.Compute(bytes):X8} {Crc32.Compute(e.RecordingBytes):X8}\n");
                }
            }
            uint hash = Crc32.Compute(System.Text.Encoding.UTF8.GetBytes(text.ToString()));
            return $"{hash:X8} active={s.ActiveBranch} generation={s.Current.Generation} branches={s.Branches.Count} segments={s.Segments.Count} stored={entries}";
        }
    }

    // ---------------------------------------------------------------- debug input and stats

    /// <summary>Reads --press, --click-car, --screenshot and --do (debug).</summary>
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
        if (_args.TryGetValue("do", out string? script))
            foreach (string step in script.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                _script.Enqueue(step);
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
                $"target={SpeedLabel(s.TargetSpeed)} actual={s.ActualSpeed:0.0} sim={_statsSimMs / _statsFrames:0.00}ms/frame " +
                $"render={_statsRenderMs / _statsFrames:0.000}ms/frame alloc={_statsAlloc}B (max {_statsAllocMax}B/frame) gc0={v.Alloc.Gen0} " +
                $"threads={s.Threads} camera={v.Camera.Mode} selected={v.Selected} branch={s.Branch} headless={s.Headless} gens/s={s.GenerationsPerSecond:0.0} " +
                $"priority={CpuPriority.Current()} saved={(s.SavedAtUtc == default ? "-" : s.SavedAtUtc.ToString("HH:mm:ss", CultureInfo.InvariantCulture))}"));
        }
        else if (_screen is DriveView d)
        {
            var r = d.Run;
            GD.Print(string.Create(CultureInfo.InvariantCulture,
                $"stats t={_clock:0.0}s fps={_statsFrames / _statsTimer:0.0} phase={r.Phase} time={r.TimeSeconds():0.00}s progress={r.Progress * 100:0}% " +
                $"speed={r.Car.ForwardSpeed:0.0} throttle={r.Controls.Throttle:0.00} steer={r.Controls.Steer:0.00} attempts={r.Attempts} engine={d.Engine.Sounding}/{d.Engine.PitchScale:0.00} " +
                $"alloc={_statsAlloc}B (max {_statsAllocMax}B/frame) gc0={d.Alloc.Gen0}"));
        }
        _statsFrames = 0;
        _statsTimer = _statsRenderMs = _statsSimMs = _statsMaxFrame = 0;
        _statsAlloc = _statsAllocMax = 0;
    }

    private static string SpeedLabel(double speed) => Nitrogenesis.Sim.Core.SpeedController.Label(speed);

    private async void TakeScreenshot(string path, bool quit)
    {
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var image = GetViewport().GetTexture().GetImage();
        string full = System.IO.Path.GetFullPath(path);
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        Error err = image.SavePng(full);
        GD.Print($"screenshot {full}: {err}");
        if (quit && !_args.ContainsKey("quit-after")) RequestQuit();
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
