using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Nitrogenesis.Sim.Io;
using Nitrogenesis.Sim.Map;
using Nitrogenesis.Sim.Racing;
using Nitrogenesis.Ui;

namespace Nitrogenesis.Editor;

/// <summary>
/// The track editor screen (PLAN §6.3): toolbar on the left, the map canvas, a slot for the status bar at the bottom.
/// Owns the tool state and the keyboard shortcuts (B L R F E S, 1–5, [ ], G, Ctrl+Z / Ctrl+Y / Ctrl+Shift+Z, Esc).
/// </summary>
public partial class EditorView : Control
{
    private EditDocument _doc = null!;
    private EditorCanvas _canvas = null!;
    private EditorToolbar _toolbar = null!;
    private EditorTool _tool = EditorTool.Brush;
    private CellType _paint = CellType.Road;
    private bool _grid = true, _toolbarDirty = true;
    private EditorChecks _checks = null!;
    private TestRouteLayer _routeLayer = null!;
    private PanelContainer _badge = null!;
    private Label _badgeTitle = null!, _badgeDetail = null!;
    private TestJob? _job;
    private TestOutcome? _tested;
    private EditorFileDialogs _files = null!;
    private string _shownTitle = "";

    /// <summary>The document being edited. Replaced by <see cref="ReplaceDocument"/> (then <see cref="DocumentReplaced"/> is raised).</summary>
    public EditDocument Document => _doc;

    /// <summary>Raised after <see cref="Document"/> was swapped for a new one.</summary>
    public event Action? DocumentReplaced;

    /// <summary>Raised when the user leaves the editor (Esc or the Back button).</summary>
    public event Action? BackRequested;

    /// <summary>Raised by the Drive button (and D): the map as it is now, as a snapshot. The owner shows the drive view and brings this editor back afterwards.</summary>
    public event Action<Track>? DriveRequested;

    /// <summary>The Test button's result while it is current (null after any edit), for debug and tests.</summary>
    public TestOutcome? TestResult => _tested;

    /// <summary>True while a Test run is in progress (the debug script waits).</summary>
    public bool TestRunning => _job is { Task.IsCompleted: false };

    /// <summary>Map space (1 unit = 1 cell, origin at the map's top-left): overlays added here draw above the map and the grid, below the brush preview and the start arrow.</summary>
    public Node2D MapLayer => _canvas.MapLayer;

    /// <summary>An empty strip (about 30 px tall, full width) at the bottom of the screen for the status bar.</summary>
    public Control StatusBarSlot { get; private set; } = null!;

    public EditorTool Tool => _tool;
    public CellType Paint => _paint;
    public EditorCanvas Canvas => _canvas;

    /// <summary>The file the document was opened from or last saved to; null for a new map or a copy of a bundled track (Save then asks for a name).</summary>
    public string? FilePath { get; set; }

    /// <summary>The New / Open / Save flows.</summary>
    public EditorFileDialogs Files => _files;

    public static EditorView Create(EditDocument doc, string? filePath = null) => new() { _doc = doc, FilePath = filePath };

    public override void _Ready()
    {
        Theme = UiTheme.Theme;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var bg = new ColorRect { Color = UiTheme.Background };
        bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(bg);

        var row = new HBoxContainer();
        row.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        row.AddThemeConstantOverride("separation", 0);
        AddChild(row);

        _toolbar = new EditorToolbar();
        row.AddChild(_toolbar);
        _toolbar.ToolChosen += SetTool;
        _toolbar.PaintChosen += SetPaint;
        _toolbar.SizeChanged += SetBrushSize;
        _toolbar.ShapeChosen += SetBrushShape;
        _toolbar.UndoPressed += Undo;
        _toolbar.RedoPressed += Redo;
        _toolbar.GridPressed += () => SetGrid(!_grid);
        _toolbar.BackPressed += Back;
        _files = new EditorFileDialogs(this);
        _toolbar.NewPressed += _files.AskNew;
        _toolbar.OpenPressed += _files.AskOpen;
        _toolbar.SavePressed += () => _files.Save();
        _toolbar.SaveAsPressed += () => _files.SaveAs();

        var right = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        right.AddThemeConstantOverride("separation", 0);
        row.AddChild(right);
        _canvas = new EditorCanvas { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        right.AddChild(_canvas);
        StatusBarSlot = new PanelContainer { CustomMinimumSize = new Vector2(0, 30), Name = "StatusBarSlot" };
        var slotStyle = new StyleBoxFlat { BgColor = UiTheme.Panel, ContentMarginLeft = 10, ContentMarginRight = 10, ContentMarginTop = 3, ContentMarginBottom = 3 };
        slotStyle.BorderColor = UiTheme.Border;
        slotStyle.BorderWidthTop = 1;
        StatusBarSlot.AddThemeStyleboxOverride("panel", slotStyle);
        right.AddChild(StatusBarSlot);
        StatusBarSlot.AddChild(UiTheme.Label("", 14, UiTheme.Muted)); // placeholder (ticket 03 removes it)

        _canvas.SetDocument(_doc);
        _doc.Changed += OnDocChanged;
        ApplyPaint();
        _canvas.SetGrid(_grid);
        _routeLayer = new TestRouteLayer();
        MapLayer.AddChild(_routeLayer);
        BuildBadge();
        _toolbar.TestPressed += Test;
        _toolbar.DrivePressed += Drive;
        _checks = new EditorChecks(this);
        AddChild(_checks);
        MapLayer.MoveChild(_routeLayer, -1); // the Test route draws above the live-check overlay (added in EditorChecks._Ready)
    }

    // Main parks this view (RemoveChild, no free) while the drive view is up: _Ready runs once, _EnterTree/_ExitTree
    // every time, so the document hook is made there. Children with threads (EditorChecks) do the same.
    public override void _EnterTree()
    {
        if (_doc is null) return;
        _doc.Changed -= OnDocChanged;
        _doc.Changed += OnDocChanged;
        _toolbarDirty = true;
    }

    public override void _ExitTree()
    {
        if (_doc is not null) _doc.Changed -= OnDocChanged;
    }

    private void OnDocChanged(EditResult _)
    {
        _toolbarDirty = true;
        ClearTest(); // any edit makes the Test result (and a run in progress) out of date
    }

    // ---------------------------------------------------------------- Test and Drive

    /// <summary>What the Test button found.</summary>
    public enum TestKind { Finished, Tight, NotFinishable }

    /// <summary>A Test result for one revision of the map. <paramref name="Seconds"/> is the finish time, or when the driver stopped.</summary>
    public sealed record TestOutcome(long Revision, TestKind Kind, float Seconds, string EndStatus, IReadOnlyList<(float X, float Y)> Path);

    private sealed record TestJob(long Revision, Task<TestOutcome> Task);

    private void BuildBadge()
    {
        _badge = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore, Visible = false, Name = "TestBadge" };
        var style = UiTheme.SolidPanel();
        style.BgColor = new Color(UiTheme.Panel.R, UiTheme.Panel.G, UiTheme.Panel.B, 0.94f);
        _badge.AddThemeStyleboxOverride("panel", style);
        var box = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        box.AddThemeConstantOverride("separation", 2);
        _badge.AddChild(box);
        _badgeTitle = UiTheme.Label("", 22, UiTheme.Text, HorizontalAlignment.Center);
        _badgeDetail = UiTheme.Label("", 14, UiTheme.Muted, HorizontalAlignment.Center);
        box.AddChild(_badgeTitle);
        box.AddChild(_badgeDetail);
        _canvas.AddChild(_badge);
        _badge.SetAnchorsAndOffsetsPreset(LayoutPreset.CenterTop, LayoutPresetMode.Minsize, 14);
        _badge.GrowHorizontal = GrowDirection.Both;
    }

    private void ShowBadge(string title, string detail, Color color)
    {
        _badgeTitle.Text = title;
        _badgeTitle.AddThemeColorOverride("font_color", color);
        _badgeDetail.Text = detail;
        _badgeDetail.Visible = detail.Length > 0;
        _badge.Visible = true;
        _badge.ResetSize(); // shrink to the new text; the top-centre anchor keeps it centred
    }

    /// <summary>
    /// Runs the reference driver on a copy of the map off the main thread. The run cannot be cancelled, so the job carries the
    /// document's revision and a result for an older revision is dropped. A map the live check calls not finishable is not driven.
    /// </summary>
    public void Test()
    {
        if (TestRunning) return;
        ClearTest();
        var snapshot = _doc.Snapshot();
        long revision = _doc.Revision;
        _job = new TestJob(revision, Task.Run(() => RunTest(snapshot, revision)));
        _toolbar.SetTesting(true);
        _routeLayer.Clear();
        ShowBadge("Testing…", "", UiTheme.Accent);
    }

    private static TestOutcome RunTest(Track snapshot, long revision)
    {
        var settings = new RacingSettings();
        var analysis = EditorAnalysis.Run(snapshot, settings, CancellationToken.None);
        if (!analysis.IsFinishable) return new TestOutcome(revision, TestKind.NotFinishable, 0f, "", []);
        var result = ReferenceDriver.Run(new RacingTrack(snapshot, settings));
        return new TestOutcome(revision, result.Finished ? TestKind.Finished : TestKind.Tight, result.TimeSeconds, result.EndStatus.ToString(), result.Path);
    }

    private void ClearTest()
    {
        if (_job is null && _tested is null) return;
        _job = null; // a run in progress finishes on its own thread and is ignored
        _tested = null;
        _routeLayer.Clear();
        _badge.Visible = false;
        _toolbar.SetTesting(false);
        _checks?.SetReferenceTime(null);
    }

    private void PollTest()
    {
        if (_job is not { Task.IsCompleted: true } job) return;
        _job = null;
        _toolbar.SetTesting(false);
        if (job.Task.IsFaulted)
        {
            _badge.Visible = false;
            GD.PushError($"Test failed: {job.Task.Exception?.GetBaseException().Message}");
            return;
        }
        var outcome = job.Task.Result;
        if (outcome.Revision != _doc.Revision) { _badge.Visible = false; return; } // edited meanwhile (ClearTest normally got there first)
        _tested = outcome;
        var inv = CultureInfo.InvariantCulture;
        switch (outcome.Kind)
        {
            case TestKind.Finished:
                _routeLayer.SetRoute(outcome.Path, true);
                ShowBadge(string.Create(inv, $"Finished in {outcome.Seconds:0.0} s"), "reference driver", UiTheme.Good);
                _checks?.SetReferenceTime(outcome.Seconds);
                break;
            case TestKind.Tight:
                _routeLayer.SetRoute(outcome.Path, false);
                ShowBadge("Finishable, but very tight", string.Create(inv, $"the reference driver {outcome.EndStatus.ToLowerInvariant()} after {outcome.Seconds:0.0} s (red mark)"), UiTheme.Accent);
                break;
            default:
                _routeLayer.Clear();
                ShowBadge("Not finishable", "", UiTheme.Bad);
                break;
        }
        GD.Print(string.Create(inv, $"editor-test: rev {outcome.Revision} {outcome.Kind} {outcome.Seconds:0.0} s {outcome.EndStatus} route={outcome.Path.Count} points"));
    }

    /// <summary>Opens the drive view on the map as it is now (the owner brings this editor back when the drive ends).</summary>
    public void Drive() => DriveRequested?.Invoke(_doc.Snapshot());

    public override void _Process(double delta)
    {
        if (_bench is not null) RunBench(delta);
        PollTest();
        string title = _doc.IsDirty ? _doc.Name + "\u0001" : _doc.Name;
        if (title != _shownTitle)
        {
            _shownTitle = title;
            _toolbar.SetFile(_doc.Name, _doc.IsDirty);
        }
        if (_toolbarDirty)
        {
            _toolbarDirty = false;
            _toolbar.Refresh(_tool, _paint, _doc.BrushSize, _doc.BrushShape, _grid, _doc.CanUndo, _doc.CanRedo);
        }
    }

    public override void _Notification(int what)
    {
        if (what is (int)NotificationWMWindowFocusOut or (int)NotificationApplicationFocusOut) _canvas?.CancelDrag();
    }

    // ---------------------------------------------------------------- state

    /// <summary>Swaps the document (New / Open): the canvas is rebuilt, the brush settings carry over.</summary>
    public void ReplaceDocument(EditDocument doc)
    {
        _canvas.CancelDrag();
        _doc.Changed -= OnDocChanged;
        ClearTest(); // revisions restart at 0 with the new document
        doc.BrushSize = _doc.BrushSize;
        doc.BrushShape = _doc.BrushShape;
        _doc = doc;
        _doc.Changed += OnDocChanged;
        _canvas.SetDocument(doc);
        ApplyPaint();
        _toolbarDirty = true;
        DocumentReplaced?.Invoke();
    }

    /// <summary>New / Open: swaps the document and sets the file it belongs to (null = none yet).</summary>
    public void ReplaceFile(EditDocument doc, string? path)
    {
        ReplaceDocument(doc);
        FilePath = path;
    }

    /// <summary>Runs <paramref name="proceed"/> (leave, quit…) now, or after the "Save changes?" question when there are unsaved changes.</summary>
    public void ConfirmLeave(Action proceed) => _files.Guard(proceed);

    private void ApplyPaint()
    {
        _doc.PaintType = _tool == EditorTool.Eraser ? CellType.Wall : _paint;
        _canvas.Tool = _tool;
        _canvas.RefreshOverlay();
        _toolbarDirty = true;
    }

    public void SetTool(EditorTool tool)
    {
        _canvas.CancelDrag();
        _tool = tool;
        ApplyPaint();
    }

    /// <summary>Chooses a paint type; the eraser and the start flag switch to the brush (they have no paint of their own).</summary>
    public void SetPaint(CellType type)
    {
        _paint = type;
        if (_tool is EditorTool.Eraser or EditorTool.Start) _tool = EditorTool.Brush;
        ApplyPaint();
    }

    public void SetBrushSize(int size)
    {
        _doc.BrushSize = size;
        _canvas.RefreshOverlay();
        _toolbarDirty = true;
    }

    public void SetBrushShape(BrushShape shape)
    {
        _doc.BrushShape = shape;
        _canvas.RefreshOverlay();
        _toolbarDirty = true;
    }

    public void SetGrid(bool on)
    {
        _grid = on;
        _canvas.SetGrid(on);
        _toolbarDirty = true;
    }

    public void Undo() => _doc.Undo();

    public void Redo() => _doc.Redo();

    private void Back() => _files.Guard(() => BackRequested?.Invoke());

    public override void _UnhandledKeyInput(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true } key) return;
        if (Modal.OpenCount > 0) return;
        bool ctrl = key.IsCommandOrControlPressed();
        if (ctrl)
        {
            if (key.Echo) return;
            if (key.Keycode == Key.N) _files.AskNew();
            else if (key.Keycode == Key.O) _files.AskOpen();
            else if (key.Keycode == Key.S) { if (key.ShiftPressed) _files.SaveAs(); else _files.Save(); }
            else if (key.Keycode == Key.Z) { if (key.ShiftPressed) Redo(); else Undo(); }
            else if (key.Keycode == Key.Y) Redo();
            else return;
            GetViewport().SetInputAsHandled();
            return;
        }
        if (key.Keycode is Key.Bracketleft or Key.Bracketright)
        {
            SetBrushSize(_doc.BrushSize + (key.Keycode == Key.Bracketright ? 1 : -1));
            GetViewport().SetInputAsHandled();
            return;
        }
        if (key.Echo) return;
        switch (key.Keycode)
        {
            case Key.B: SetTool(EditorTool.Brush); break;
            case Key.L: SetTool(EditorTool.Line); break;
            case Key.R: SetTool(EditorTool.Rect); break;
            case Key.F: SetTool(EditorTool.Fill); break;
            case Key.E: SetTool(EditorTool.Eraser); break;
            case Key.S: SetTool(EditorTool.Start); break;
            case Key.Key1: SetPaint(CellType.Road); break;
            case Key.Key2: SetPaint(CellType.Grass); break;
            case Key.Key3: SetPaint(CellType.Wall); break;
            case Key.Key4: SetPaint(CellType.Danger); break;
            case Key.Key5: SetPaint(CellType.Finish); break;
            case Key.G: SetGrid(!_grid); break;
            case Key.T: Test(); break;
            case Key.D: Drive(); break;
            case Key.Escape:
                if (!_canvas.CancelDrag()) Back();
                break;
            default: return;
        }
        GetViewport().SetInputAsHandled();
    }

    // ---------------------------------------------------------------- debug: --do verbs

    /// <summary>True while a debug benchmark stroke is running (the script waits).</summary>
    public bool DebugBusy => _bench is not null;

    /// <summary>
    /// A --do step for the editor (Main handles wait, shot, quit, menu first). Verbs: <c>tool:brush|line|rect|fill|eraser|start</c>,
    /// <c>paint:road|grass|wall|danger|finish</c>, <c>size:N</c>, <c>shape:round|square</c>, <c>stroke:x,y/x,y/…</c> (cells),
    /// <c>line:x0,y0,x1,y1</c>, <c>rect:…</c>, <c>fill:x,y</c>, <c>start:x,y,angle</c>, <c>undo</c>, <c>redo</c>, <c>grid:on|off</c>,
    /// <c>zoom:PIXELS_PER_CELL</c>, <c>center:x,y</c>, <c>fit</c>, <c>hover:x,y</c>, <c>drag-preview:tool,x0,y0,x1,y1</c>, <c>newmap:S|M|L</c> (no question), <c>new:S|M|L</c>, <c>new-dialog</c>, <c>open:USERTRACK</c>, <c>open-bundled:NAME</c>, <c>open-dialog</c>, <c>save</c>, <c>save-as:NAME</c>, <c>save-as-dialog[:TEXT]</c> (these go through the same code as the buttons, including the unsaved-changes and overwrite questions; Main's <c>modal-button:LABEL</c> answers them),
    /// <c>mouse:down|move|up|wheelup|wheeldown,x,y[,left|right|middle]</c> (a real injected mouse event at the centre of cell x,y),
    /// <c>bench[:CELLS_PER_FRAME]</c> (a long serpentine stroke with injected mouse moves; prints frame and flush times), <c>test</c> (the Test button; later shots wait for the result), <c>drive</c> (the Drive button), <c>state</c>.
    /// </summary>
    public string? DebugDo(string verb, string arg)
    {
        var inv = CultureInfo.InvariantCulture;
        try
        {
            switch (verb)
            {
                case "tool":
                    if (!Enum.TryParse<EditorTool>(arg == "rect" ? "Rect" : arg, true, out var tool)) return $"unknown tool '{arg}'";
                    SetTool(tool);
                    return null;
                case "paint":
                    if (!Enum.TryParse<CellType>(arg, true, out var type)) return $"unknown paint '{arg}'";
                    SetPaint(type);
                    return null;
                case "size":
                    SetBrushSize(int.Parse(arg, inv));
                    return null;
                case "shape":
                    SetBrushShape(arg.Equals("square", StringComparison.OrdinalIgnoreCase) ? BrushShape.Square : BrushShape.Round);
                    return null;
                case "stroke":
                {
                    var pts = Cells(arg, '/', 2);
                    _canvas.CancelDrag();
                    _doc.BeginStroke(pts[0][0], pts[0][1]);
                    for (int i = 1; i < pts.Count; i++) _doc.MoveStroke(pts[i][0], pts[i][1]);
                    _doc.EndStroke();
                    return null;
                }
                case "line": { var n = Nums(arg, 4); _doc.Line(n[0], n[1], n[2], n[3]); return null; }
                case "rect": { var n = Nums(arg, 4); _doc.Rect(n[0], n[1], n[2], n[3]); return null; }
                case "fill": { var n = Nums(arg, 2); _doc.Fill(n[0], n[1]); return null; }
                case "start":
                {
                    string[] p = arg.Split(',');
                    _doc.SetStart(int.Parse(p[0], inv), int.Parse(p[1], inv), float.Parse(p[2], inv));
                    return null;
                }
                case "undo": Undo(); return null;
                case "redo": Redo(); return null;
                case "grid": SetGrid(arg.Equals("on", StringComparison.OrdinalIgnoreCase)); return null;
                case "zoom": _canvas.SetView(float.Parse(arg, inv), _canvas.ViewCenter); return null;
                case "center": { var n = Nums(arg, 2); _canvas.SetView(_canvas.PixelsPerCell, new Vector2(n[0] + 0.5f, n[1] + 0.5f)); return null; }
                case "fit": _canvas.Fit(); return null;
                case "hover": { var n = Nums(arg, 2); _canvas.DebugHover(new Vector2I(n[0], n[1])); return null; }
                case "drag-preview":
                {
                    string[] p = arg.Split(',', 2);
                    var n = Nums(p[1], 4);
                    if (!Enum.TryParse<EditorTool>(p[0], true, out var t)) return $"unknown tool '{p[0]}'";
                    _canvas.DebugDragPreview(t, new Vector2I(n[0], n[1]), new Vector2I(n[2], n[3]));
                    return null;
                }
                case "newmap":
                    ReplaceDocument(EditDocument.New(Enum.Parse<MapSize>(arg, true)));
                    FilePath = null;
                    return null;
                case "new": _files.NewGuarded(Enum.Parse<MapSize>(arg, true)); return null;
                case "new-dialog": _files.AskNew(); return null;
                case "open-dialog": _files.AskOpen(); return null;
                case "open": return _files.OpenUserByName(arg);
                case "open-bundled": return _files.OpenBundledByName(arg);
                case "save": _files.Save(); return null;
                case "save-as": _files.DoSaveAs(arg); return null;
                case "save-as-dialog": _files.SaveAs(null, arg.Length == 0 ? null : arg); return null;
                case "mouse": return DebugMouse(arg);
                case "bench": StartBench(arg.Length == 0 ? 16 : int.Parse(arg, inv)); return null;
                case "test": Test(); return null;
                case "drive": Drive(); return null;
                case "state": GD.Print(State()); return null;
            }
        }
        catch (Exception e) when (e is FormatException or IndexOutOfRangeException or OverflowException or ArgumentException)
        {
            return $"bad argument '{arg}' for '{verb}': {e.Message}";
        }
        return $"unknown editor step '{verb}'";

        static int[] Nums(string s, int count)
        {
            string[] p = s.Split(',');
            if (p.Length != count) throw new FormatException($"needs {count} numbers");
            return Array.ConvertAll(p, v => int.Parse(v.Trim(), CultureInfo.InvariantCulture));
        }

        static List<int[]> Cells(string s, char sep, int count)
        {
            var list = new List<int[]>();
            foreach (string part in s.Split(sep, StringSplitOptions.RemoveEmptyEntries)) list.Add(Nums(part, count));
            if (list.Count == 0) throw new FormatException("no points");
            return list;
        }
    }

    private string State()
    {
        var g = _doc.Grid;
        var s = _doc.Start;
        return string.Create(CultureInfo.InvariantCulture,
            $"editor state: {g.Width}x{g.Height} rev={_doc.Revision} undo={_doc.UndoCount} redo={_doc.RedoCount} dirty={_doc.IsDirty} " +
            $"name='{_doc.Name}' file={(FilePath is null ? "-" : System.IO.Path.GetFileName(FilePath))} start=({s.X},{s.Y},{s.AngleDeg}) tool={_tool} paint={_doc.PaintType} size={_doc.BrushSize} shape={_doc.BrushShape} " +
            $"zoom={_canvas.PixelsPerCell:0.00}px/cell cells={Crc32.Compute(g.Cells):X8} test={(_tested is null ? (TestRunning ? "running" : "none") : _tested.Kind)}");
    }

    private Vector2 _mouseLast;

    private string? DebugMouse(string arg)
    {
        string[] p = arg.Split(',');
        string action = p[0];
        float x = p.Length > 1 ? float.Parse(p[1], CultureInfo.InvariantCulture) : 0, y = p.Length > 2 ? float.Parse(p[2], CultureInfo.InvariantCulture) : 0;
        var button = p.Length > 3 ? p[3] switch { "right" => MouseButton.Right, "middle" => MouseButton.Middle, _ => MouseButton.Left } : MouseButton.Left;
        Vector2 at = _canvas.CellToScreen(new Vector2(x + 0.5f, y + 0.5f));
        switch (action)
        {
            case "down":
            case "up":
                Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = button, Pressed = action == "down", Position = at, GlobalPosition = at, ButtonMask = action == "down" ? ButtonMask(button) : 0 });
                break;
            case "move":
                Input.ParseInputEvent(new InputEventMouseMotion { Position = at, GlobalPosition = at, Relative = at - _mouseLast, ButtonMask = _mouseHeld ? MouseButtonMask.Left : 0 });
                break;
            case "wheelup":
            case "wheeldown":
                Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = action == "wheelup" ? MouseButton.WheelUp : MouseButton.WheelDown, Pressed = true, Position = at, GlobalPosition = at });
                break;
            default:
                return $"unknown mouse action '{action}'";
        }
        _mouseHeld = action == "down" || (_mouseHeld && action != "up");
        _mouseLast = at;
        return null;

        static MouseButtonMask ButtonMask(MouseButton b) => b switch { MouseButton.Right => MouseButtonMask.Right, MouseButton.Middle => MouseButtonMask.Middle, _ => MouseButtonMask.Left };
    }

    private bool _mouseHeld;

    // ---------------------------------------------------------------- debug: smoothness benchmark

    private sealed class Bench
    {
        public readonly List<Vector2> Path = new();
        public int Next;
        public int Frames;
        public double SumMs, MaxMs, FlushSum, FlushMax;
        public int Flushes, SeenFlushes, IdleFrames;
        public double IdleSum;
    }

    private Bench? _bench;

    private void StartBench(int cellsPerFrame)
    {
        var path = new List<Vector2>();
        int w = _doc.Grid.Width, h = _doc.Grid.Height;
        bool right = true;
        for (int y = 8; y < h - 8; y += 12)
        {
            int from = right ? 8 : w - 9, to = right ? w - 9 : 8;
            int dir = right ? 1 : -1;
            for (int x = from; right ? x <= to : x >= to; x += dir * cellsPerFrame) path.Add(new Vector2(x, y));
            path.Add(new Vector2(to, y));
            right = !right;
        }
        _bench = new Bench();
        _bench.Path.AddRange(path);
        _canvas.CancelDrag();
        SetTool(EditorTool.Brush);
        SetPaint(CellType.Road);
        GD.Print($"bench: {path.Count} mouse moves of up to {cellsPerFrame} cells, brush {_doc.BrushSize} {_doc.BrushShape}, map {w}x{h}");
    }

    private void RunBench(double delta)
    {
        var b = _bench!;
        if (b.Next == 0 && b.IdleFrames < 60) // idle baseline first
        {
            if (b.IdleFrames++ >= 10) b.IdleSum += delta * 1000;
            return;
        }
        if (b.Next > 0)
        {
            double ms = delta * 1000;
            b.Frames++;
            b.SumMs += ms;
            b.MaxMs = Math.Max(b.MaxMs, ms);
            if (_canvas.FlushCount != b.SeenFlushes)
            {
                b.SeenFlushes = _canvas.FlushCount;
                b.Flushes++;
                b.FlushSum += _canvas.LastFlushMs;
                b.FlushMax = Math.Max(b.FlushMax, _canvas.LastFlushMs);
            }
        }
        if (b.Next < b.Path.Count)
        {
            var p = b.Path[b.Next];
            DebugMouse(string.Create(CultureInfo.InvariantCulture, $"{(b.Next == 0 ? "down" : "move")},{p.X},{p.Y}"));
            b.Next++;
            return;
        }
        var last = b.Path[^1];
        DebugMouse(string.Create(CultureInfo.InvariantCulture, $"up,{last.X},{last.Y}"));
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"bench done: idleFrame={b.IdleSum / 50:0.00}ms frames={b.Frames} avgFrame={b.SumMs / Math.Max(1, b.Frames):0.00}ms maxFrame={b.MaxMs:0.00}ms " +
            $"texture updates={b.Flushes} avgFlush={b.FlushSum / Math.Max(1, b.Flushes):0.00}ms maxFlush={b.FlushMax:0.00}ms undoSteps={_doc.UndoCount}"));
        _bench = null;
    }
}
