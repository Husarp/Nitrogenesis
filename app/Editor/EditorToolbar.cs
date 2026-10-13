using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using Nitrogenesis.Sim.Map;
using Nitrogenesis.Ui;

namespace Nitrogenesis.Editor;

public enum EditorTool { Brush, Line, Rect, Fill, Eraser, Start }

/// <summary>
/// The editor's left panel (PLAN §6.3): tools, paint types, brush size and shape, undo/redo, grid, back. It only
/// raises events; <see cref="EditorView"/> owns the state and calls <see cref="Refresh"/>. Its buttons take no focus,
/// so the shortcut keys keep reaching the view.
/// </summary>
public partial class EditorToolbar : PanelContainer
{
    public const int Width = 188;

    private static readonly (EditorTool Tool, string Name, string Key)[] Tools =
    [
        (EditorTool.Brush, "Brush", "B"), (EditorTool.Line, "Line", "L"), (EditorTool.Rect, "Rectangle", "R"),
        (EditorTool.Fill, "Fill", "F"), (EditorTool.Eraser, "Eraser", "E"), (EditorTool.Start, "Start flag", "S"),
    ];

    private static readonly CellType[] PaintTypes = [CellType.Road, CellType.Grass, CellType.Wall, CellType.Danger, CellType.Finish];

    private readonly Dictionary<EditorTool, Button> _toolButtons = new();
    private readonly Dictionary<CellType, PaintSwatch> _swatches = new();
    private HSlider _size = null!;
    private Label _sizeLabel = null!, _paintLabel = null!, _fileLabel = null!;
    private Button _round = null!, _square = null!, _undo = null!, _redo = null!, _grid = null!, _test = null!;

    public event Action<EditorTool>? ToolChosen;
    public event Action<CellType>? PaintChosen;
    public event Action<int>? SizeChanged;
    public event Action<BrushShape>? ShapeChosen;
    public event Action? UndoPressed, RedoPressed, GridPressed, BackPressed, TestPressed, DrivePressed;
    public event Action? NewPressed, OpenPressed, SavePressed, SaveAsPressed;

    public override void _Ready()
    {
        Theme = UiTheme.Theme;
        CustomMinimumSize = new Vector2(Width, 0);
        var panel = UiTheme.SolidPanel();
        panel.SetCornerRadiusAll(0);
        AddThemeStyleboxOverride("panel", panel);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 6);
        AddChild(box);

        _fileLabel = UiTheme.Label("", 16, UiTheme.Accent); // the track's name (with a bullet when unsaved) is the screen's heading
        _fileLabel.ClipText = true;
        _fileLabel.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        box.AddChild(_fileLabel);
        var file = new GridContainer { Columns = 2 };
        file.AddThemeConstantOverride("h_separation", 6);
        file.AddThemeConstantOverride("v_separation", 6);
        box.AddChild(file);
        file.AddChild(Small("New", "New map (Ctrl+N)", () => NewPressed?.Invoke()));
        file.AddChild(Small("Open", "Open a track (Ctrl+O)", () => OpenPressed?.Invoke()));
        file.AddChild(Small("Save", "Save (Ctrl+S)", () => SavePressed?.Invoke()));
        file.AddChild(Small("Save as", "Save under a new name (Ctrl+Shift+S)", () => SaveAsPressed?.Invoke()));

        box.AddChild(Heading("Tools"));
        var tools = new GridContainer { Columns = 2 };
        tools.AddThemeConstantOverride("h_separation", 6);
        tools.AddThemeConstantOverride("v_separation", 6);
        box.AddChild(tools);
        var group = new ButtonGroup();
        foreach (var (tool, name, key) in Tools)
        {
            var b = new Button { Text = name, ToggleMode = true, ButtonGroup = group, FocusMode = FocusModeEnum.None, TooltipText = $"{name} ({key})", SizeFlagsHorizontal = SizeFlags.ExpandFill };
            b.AddThemeFontSizeOverride("font_size", 15);
            var t = tool;
            b.Pressed += () => ToolChosen?.Invoke(t);
            tools.AddChild(b);
            _toolButtons[tool] = b;
        }

        box.AddChild(Heading("Paint"));
        var swatches = new HBoxContainer();
        swatches.AddThemeConstantOverride("separation", 4);
        box.AddChild(swatches);
        var paintGroup = new ButtonGroup();
        for (int i = 0; i < PaintTypes.Length; i++)
        {
            var type = PaintTypes[i];
            var s = new PaintSwatch { Type = type, ButtonGroup = paintGroup, TooltipText = $"{type} ({i + 1})" };
            s.Pressed += () => PaintChosen?.Invoke(type);
            swatches.AddChild(s);
            _swatches[type] = s;
        }
        _paintLabel = UiTheme.Label("", 14, UiTheme.Muted);
        box.AddChild(_paintLabel);

        box.AddChild(Heading("Brush"));
        _sizeLabel = UiTheme.Label("", 15, UiTheme.Text);
        box.AddChild(_sizeLabel);
        _size = new HSlider { MinValue = EditDocument.MinBrush, MaxValue = EditDocument.MaxBrush, Step = 1, Value = 5, FocusMode = FocusModeEnum.None, TooltipText = "Brush size ([ and ])", CustomMinimumSize = new Vector2(0, 22) };
        _size.ValueChanged += v => SizeChanged?.Invoke((int)v);
        box.AddChild(_size);
        var shapes = new HBoxContainer();
        shapes.AddThemeConstantOverride("separation", 6);
        box.AddChild(shapes);
        var shapeGroup = new ButtonGroup();
        _round = ShapeButton("Round", BrushShape.Round, shapeGroup);
        _square = ShapeButton("Square", BrushShape.Square, shapeGroup);
        shapes.AddChild(_round);
        shapes.AddChild(_square);

        box.AddChild(Heading("Edit and view"));
        var history = new HBoxContainer();
        history.AddThemeConstantOverride("separation", 6);
        box.AddChild(history);
        _undo = Small("Undo", "Undo (Ctrl+Z)", () => UndoPressed?.Invoke());
        _redo = Small("Redo", "Redo (Ctrl+Y, Ctrl+Shift+Z)", () => RedoPressed?.Invoke());
        history.AddChild(_undo);
        history.AddChild(_redo);
        _grid = Small("Grid", "Show the cell grid when zoomed in (G)", () => GridPressed?.Invoke());
        _grid.ToggleMode = true;
        history.AddChild(_grid);

        box.AddChild(Heading("Try it"));
        var tryIt = new HBoxContainer();
        tryIt.AddThemeConstantOverride("separation", 6);
        box.AddChild(tryIt);
        _test = Small("Test", "Run the reference driver on this map (T)", () => TestPressed?.Invoke());
        tryIt.AddChild(_test);
        tryIt.AddChild(Small("Drive", "Drive this map yourself; Esc comes back here (D)", () => DrivePressed?.Invoke()));

        box.AddChild(new Control { SizeFlagsVertical = SizeFlags.ExpandFill });
        var back = Small("Back", "Back to the menu (Esc)", () => BackPressed?.Invoke());
        back.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        box.AddChild(back);
    }

    private static Label Heading(string text) => UiTheme.Label(text.ToUpperInvariant(), 12, UiTheme.Muted);

    private static Button Small(string text, string tip, Action pressed)
    {
        var b = UiTheme.SmallButton(text, pressed);
        b.TooltipText = tip;
        b.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        return b;
    }

    private Button ShapeButton(string text, BrushShape shape, ButtonGroup group)
    {
        var b = new Button { Text = text, ToggleMode = true, ButtonGroup = group, FocusMode = FocusModeEnum.None, SizeFlagsHorizontal = SizeFlags.ExpandFill, TooltipText = $"{text} brush" };
        b.AddThemeFontSizeOverride("font_size", 15);
        b.Pressed += () => ShapeChosen?.Invoke(shape);
        return b;
    }

    /// <summary>The track's name with a bullet when it has unsaved changes.</summary>
    public void SetFile(string name, bool dirty)
    {
        _fileLabel.Text = dirty ? name + " •" : name;
        _fileLabel.TooltipText = "Track editor: " + (dirty ? name + " (unsaved changes)" : name);
    }

    /// <summary>Test is greyed out while a run is in progress.</summary>
    public void SetTesting(bool testing) => _test.Disabled = testing;

    /// <summary>Shows the view's state (no events are raised).</summary>
    public void Refresh(EditorTool tool, CellType paint, int size, BrushShape shape, bool grid, bool canUndo, bool canRedo)
    {
        foreach (var (t, b) in _toolButtons) b.SetPressedNoSignal(t == tool);
        foreach (var (t, b) in _swatches) b.SetPressedNoSignal(t == paint);
        _paintLabel.Text = tool == EditorTool.Eraser ? "Eraser paints wall" : paint.ToString();
        _size.SetValueNoSignal(size);
        _sizeLabel.Text = "Size " + size.ToString(CultureInfo.InvariantCulture);
        _round.SetPressedNoSignal(shape == BrushShape.Round);
        _square.SetPressedNoSignal(shape == BrushShape.Square);
        _grid.SetPressedNoSignal(grid);
        _undo.Disabled = !canUndo;
        _redo.Disabled = !canRedo;
    }
}
