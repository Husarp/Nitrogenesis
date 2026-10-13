using System;
using System.Collections.Generic;
using System.Diagnostics;
using Godot;
using Nitrogenesis.Sim.Map;
using Nitrogenesis.Ui;
using Nitrogenesis.View;

namespace Nitrogenesis.Editor;

/// <summary>
/// The editor's map area: the map texture (repainted per dirty rectangle, once per frame), zoom/pan, the grid, the
/// tools' mouse handling and the overlays (brush preview, rubber band, start arrow). Layers, bottom to top: map,
/// grid, <see cref="MapLayer"/> (for other overlays), preview/start. Everything in the layers is in map space
/// (1 unit = 1 cell, origin at the map's top-left).
/// </summary>
public partial class EditorCanvas : Control
{
    public const float MaxPixelsPerCell = 64f;
    /// <summary>The grid lines show from this many screen pixels per cell.</summary>
    public const float GridMinPixelsPerCell = 8f;
    private const float WheelStep = 1.15f;
    private const float HandleDistance = 7f; // cells from the start to its rotate handle
    private const float HandleHitPixels = 12f;
    private const float KeepVisible = 60f; // px of the map that stay on screen when panning

    private static readonly Dictionary<(int, BrushShape), Vector2[]> s_outlines = new();
    private static readonly Dictionary<(int, BrushShape), (int Dx, int Dy)[]> s_masks = new();

    private EditDocument _doc = null!;
    private Image _image = null!;
    private ImageTexture _texture = null!;
    private Sprite2D _sprite = null!;
    private Node2D _world = null!;
    private DrawLayer _gridLayer = null!, _overlay = null!;
    private DirtyRect _dirty = DirtyRect.Empty;
    private float _minPx = 1f;
    private bool _userZoomed;

    // mouse state
    private bool _panning, _panByLeft, _hoverValid;
    private Vector2 _hoverLocal;
    private Vector2I _hover;
    private EditorTool? _drag;           // line / rect / start drag in progress (or a debug preview)
    private bool _stroking;
    private Vector2I _anchor, _current;  // line / rect corners
    private Vector2I _pendCell;          // start placement in progress
    private float _pendAngle;
    private bool _handleHot;

    public Node2D MapLayer { get; private set; } = null!;
    public EditorTool Tool { get; set; } = EditorTool.Brush;
    public bool ShowGrid { get; set; } = true;
    public float PixelsPerCell => _world.Scale.X;
    public double LastFlushMs { get; private set; }
    public int FlushCount { get; private set; }
    public EditDocument Document => _doc;

    public EditorCanvas()
    {
        ClipContents = true;
        MouseFilter = MouseFilterEnum.Stop;
        MouseDefaultCursorShape = CursorShape.Cross;
        FocusMode = FocusModeEnum.None;
    }

    public override void _Ready()
    {
        _world = new Node2D();
        AddChild(_world);
        _sprite = new Sprite2D { Centered = false, TextureFilter = TextureFilterEnum.Nearest };
        _world.AddChild(_sprite);
        _gridLayer = new DrawLayer { OnDraw = DrawGrid };
        _world.AddChild(_gridLayer);
        MapLayer = new Node2D { Name = "MapLayer" };
        _world.AddChild(MapLayer);
        _overlay = new DrawLayer { OnDraw = DrawOverlay };
        _world.AddChild(_overlay);
    }

    /// <summary>Shows another document (the first one too): rebuilds the texture and fits the view.</summary>
    public void SetDocument(EditDocument doc)
    {
        if (_doc is not null) _doc.Changed -= OnChanged;
        _doc = doc;
        _doc.Changed += OnChanged;
        _image = MapTexture.CreateImage(doc.Grid);
        _texture = ImageTexture.CreateFromImage(_image);
        _sprite.Texture = _texture;
        _dirty = DirtyRect.Empty;
        _stroking = false;
        _drag = null;
        _userZoomed = false;
        Fit();
    }

    /// <summary>Back in the tree after Drive (the editor was parked, not freed): hook the document again.</summary>
    public override void _EnterTree()
    {
        if (_doc is null) return;
        _doc.Changed -= OnChanged;
        _doc.Changed += OnChanged;
    }

    public override void _ExitTree()
    {
        if (_doc is not null) _doc.Changed -= OnChanged;
    }

    private void OnChanged(EditResult r)
    {
        _dirty = _dirty.Union(r.Rect);
        if (r.StartChanged) _overlay.QueueRedraw();
    }

    public override void _Process(double delta) => Flush();

    /// <summary>Pushes the pending changes to the texture (once per frame; only the dirty rectangle's pixels are rewritten).</summary>
    public void Flush()
    {
        if (_dirty.IsEmpty) return;
        long t0 = Stopwatch.GetTimestamp();
        MapTexture.Repaint(_image, _doc.Grid, _dirty);
        _texture.Update(_image);
        _dirty = DirtyRect.Empty;
        FlushCount++;
        LastFlushMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    }

    public override void _Notification(int what)
    {
        if (what == NotificationResized && _doc is not null)
        {
            if (!_userZoomed) Fit();
            else ApplyView();
        }
        else if (what == NotificationMouseExit && !_panning)
        {
            _hoverValid = false;
            _overlay?.QueueRedraw();
        }
        else if (what == NotificationFocusExit) CancelDrag();
    }

    // ---------------------------------------------------------------- view

    /// <summary>Zoom and position so the whole map is visible and centred.</summary>
    public void Fit()
    {
        if (Size.X < 8 || Size.Y < 8) return;
        float s = Mathf.Min((Size.X - 24f) / _doc.Grid.Width, (Size.Y - 24f) / _doc.Grid.Height);
        _minPx = Mathf.Max(0.5f, s * 0.5f);
        SetView(s, new Vector2(_doc.Grid.Width, _doc.Grid.Height) / 2f);
        _userZoomed = false;
    }

    /// <summary>Sets pixels per cell and puts map cell position <paramref name="center"/> in the middle of the canvas.</summary>
    public void SetView(float pixelsPerCell, Vector2 center)
    {
        float s = Mathf.Clamp(pixelsPerCell, _minPx, MaxPixelsPerCell);
        _world.Scale = Vector2.One * s;
        _world.Position = Size / 2f - center * s;
        ApplyView();
    }

    /// <summary>The cell position (fractional) at the centre of the canvas.</summary>
    public Vector2 ViewCenter => (Size / 2f - _world.Position) / PixelsPerCell;

    private void ApplyView()
    {
        float s = PixelsPerCell;
        var pos = _world.Position;
        pos.X = Mathf.Clamp(pos.X, KeepVisible - _doc.Grid.Width * s, Size.X - KeepVisible);
        pos.Y = Mathf.Clamp(pos.Y, KeepVisible - _doc.Grid.Height * s, Size.Y - KeepVisible);
        _world.Position = pos;
        _gridLayer.QueueRedraw();
        _overlay.QueueRedraw();
    }

    private void ZoomAt(Vector2 local, float factor)
    {
        float before = PixelsPerCell, after = Mathf.Clamp(before * factor, _minPx, MaxPixelsPerCell);
        if (Mathf.IsEqualApprox(before, after)) return;
        Vector2 cell = (local - _world.Position) / before;
        _world.Scale = Vector2.One * after;
        _world.Position = local - cell * after;
        _userZoomed = true;
        ApplyView();
    }

    public void SetGrid(bool on)
    {
        ShowGrid = on;
        _gridLayer.QueueRedraw();
    }

    /// <summary>Redraws the tool-dependent overlay (after a tool or brush setting changed).</summary>
    public void RefreshOverlay() => _overlay.QueueRedraw();

    public Vector2 CellF(Vector2 local) => (local - _world.Position) / PixelsPerCell;

    public Vector2I CellOf(Vector2 local)
    {
        var c = CellF(local);
        return new Vector2I(Mathf.FloorToInt(c.X), Mathf.FloorToInt(c.Y));
    }

    /// <summary>Viewport (screen) position of a map position in cells; for injected mouse events.</summary>
    public Vector2 CellToScreen(Vector2 cell) => GlobalPosition + _world.Position + cell * PixelsPerCell;

    private Vector2I Clamped(Vector2I c) => new(Mathf.Clamp(c.X, 0, _doc.Grid.Width - 1), Mathf.Clamp(c.Y, 0, _doc.Grid.Height - 1));

    // ---------------------------------------------------------------- input

    public override void _GuiInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown } wheel:
                ZoomAt(wheel.Position, wheel.ButtonIndex == MouseButton.WheelUp ? WheelStep : 1f / WheelStep);
                AcceptEvent();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Middle or MouseButton.Right } pan:
                _panning = pan.Pressed;
                if (pan.Pressed) CancelDrag();
                MouseDefaultCursorShape = _panning ? CursorShape.Drag : CursorShape.Cross;
                AcceptEvent();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } left:
                if (left.Pressed)
                {
                    if (Input.IsKeyPressed(Key.Space))
                    {
                        _panning = _panByLeft = true;
                        MouseDefaultCursorShape = CursorShape.Drag;
                    }
                    else if (!_panning) Press(left.Position);
                }
                else if (_panByLeft)
                {
                    _panning = _panByLeft = false;
                    MouseDefaultCursorShape = CursorShape.Cross;
                }
                else Release(left.Position);
                AcceptEvent();
                break;
            case InputEventMouseMotion motion:
                _hoverLocal = motion.Position;
                var cell = CellOf(motion.Position);
                if (_panning)
                {
                    _world.Position += motion.Relative;
                    _userZoomed = true;
                    ApplyView();
                    break;
                }
                bool moved = !_hoverValid || cell != _hover || _drag == EditorTool.Start;
                _hoverValid = true;
                _hover = cell;
                Drag(motion.Position);
                if (moved || _handleHot != HandleHit(motion.Position)) _overlay.QueueRedraw();
                break;
        }
    }

    private void Press(Vector2 local)
    {
        var c = CellOf(local);
        _hover = c;
        _hoverValid = true;
        switch (Tool)
        {
            case EditorTool.Brush:
            case EditorTool.Eraser:
                _stroking = true;
                _doc.BeginStroke(c.X, c.Y);
                break;
            case EditorTool.Line:
            case EditorTool.Rect:
                _drag = Tool;
                _anchor = _current = Clamped(c);
                break;
            case EditorTool.Fill:
                _doc.Fill(c.X, c.Y);
                break;
            case EditorTool.Start:
                var start = _doc.Start;
                bool onHandle = HandleHit(local);
                _drag = EditorTool.Start;
                if (onHandle)
                {
                    _pendCell = new Vector2I((int)start.X, (int)start.Y);
                    _pendAngle = start.AngleDeg;
                }
                else
                {
                    _pendCell = Clamped(c);
                    _pendAngle = start.AngleDeg;
                }
                break;
        }
        _overlay.QueueRedraw();
    }

    private void Drag(Vector2 local)
    {
        var c = CellOf(local);
        if (_stroking) _doc.MoveStroke(c.X, c.Y);
        else if (_drag is EditorTool.Line or EditorTool.Rect) _current = Clamped(c);
        else if (_drag == EditorTool.Start)
        {
            var from = new Vector2(_pendCell.X + 0.5f, _pendCell.Y + 0.5f);
            var d = CellF(local) - from;
            if (d.Length() > 0.75f) _pendAngle = Mathf.RadToDeg(Mathf.Atan2(d.Y, d.X));
        }
    }

    private void Release(Vector2 local)
    {
        Drag(local);
        if (_stroking)
        {
            _stroking = false;
            _doc.EndStroke();
        }
        var tool = _drag;
        _drag = null;
        switch (tool)
        {
            case EditorTool.Line: _doc.Line(_anchor.X, _anchor.Y, _current.X, _current.Y); break;
            case EditorTool.Rect: _doc.Rect(_anchor.X, _anchor.Y, _current.X, _current.Y); break;
            case EditorTool.Start: _doc.SetStart(_pendCell.X, _pendCell.Y, _pendAngle); break;
        }
        _overlay.QueueRedraw();
    }

    /// <summary>Ends a stroke / drops a rubber band or start drag in progress (Esc, focus loss). True when something was active.</summary>
    public bool CancelDrag()
    {
        bool was = _stroking || _drag is not null;
        if (_stroking) _doc.EndStroke();
        _stroking = false;
        _drag = null;
        _overlay?.QueueRedraw();
        return was;
    }

    /// <summary>Debug: shows the rubber band of a tool as if the mouse were held down from <paramref name="a"/> to <paramref name="b"/>.</summary>
    public void DebugDragPreview(EditorTool tool, Vector2I a, Vector2I b)
    {
        _anchor = Clamped(a);
        _current = Clamped(b);
        _drag = tool;
        _hover = b;
        _hoverValid = true;
        _overlay.QueueRedraw();
    }

    public void DebugHover(Vector2I cell)
    {
        _hover = cell;
        _hoverValid = true;
        _drag = null;
        _overlay.QueueRedraw();
    }

    // ---------------------------------------------------------------- drawing

    private static Color PreviewColor(CellType type) => type switch
    {
        CellType.Road => MapTexture.Road.Lightened(0.35f),
        CellType.Grass => MapTexture.Grass,
        CellType.Danger => MapTexture.Danger,
        CellType.Finish => MapTexture.FinishLight,
        _ => new Color(0.55f, 0.5f, 0.45f),
    };

    private void DrawGrid(DrawLayer l)
    {
        float s = PixelsPerCell;
        if (!ShowGrid || s < GridMinPixelsPerCell || _doc is null) return;
        int w = _doc.Grid.Width, h = _doc.Grid.Height;
        int x0 = Mathf.Max(0, Mathf.FloorToInt(-_world.Position.X / s)), x1 = Mathf.Min(w, Mathf.CeilToInt((Size.X - _world.Position.X) / s));
        int y0 = Mathf.Max(0, Mathf.FloorToInt(-_world.Position.Y / s)), y1 = Mathf.Min(h, Mathf.CeilToInt((Size.Y - _world.Position.Y) / s));
        var pts = new List<Vector2>();
        for (int x = x0; x <= x1; x++) { pts.Add(new Vector2(x, y0)); pts.Add(new Vector2(x, y1)); }
        for (int y = y0; y <= y1; y++) { pts.Add(new Vector2(x0, y)); pts.Add(new Vector2(x1, y)); }
        l.DrawMultiline(pts.ToArray(), new Color(0, 0, 0, 0.35f), 1f / s);
    }

    private void DrawOverlay(DrawLayer l)
    {
        if (_doc is null) return;
        float px = 1f / PixelsPerCell; // one screen pixel in cells
        l.DrawRect(new Rect2(0, 0, _doc.Grid.Width, _doc.Grid.Height), UiTheme.Border, false, 2f * px);

        var type = _doc.PaintType;
        if (_drag == EditorTool.Line)
        {
            int n = _doc.BrushSize;
            Vector2 a = new(_anchor.X + 0.5f, _anchor.Y + 0.5f), b = new(_current.X + 0.5f, _current.Y + 0.5f);
            var fill = PreviewColor(type) with { A = 0.5f };
            l.DrawLine(a, b, fill, n);
            if (_doc.BrushShape == BrushShape.Round)
            {
                l.DrawCircle(a, n / 2f, fill);
                l.DrawCircle(b, n / 2f, fill);
            }
            l.DrawLine(a, b, new Color(1, 1, 1, 0.9f), 1.5f * px);
            DrawBrushOutline(l, _anchor, px);
            DrawBrushOutline(l, _current, px);
        }
        else if (_drag == EditorTool.Rect)
        {
            int x0 = Math.Min(_anchor.X, _current.X), y0 = Math.Min(_anchor.Y, _current.Y);
            int x1 = Math.Max(_anchor.X, _current.X) + 1, y1 = Math.Max(_anchor.Y, _current.Y) + 1;
            var r = new Rect2(x0, y0, x1 - x0, y1 - y0);
            l.DrawRect(r, PreviewColor(type) with { A = 0.5f });
            l.DrawRect(r, UiTheme.Accent, false, 2f * px);
        }
        else if (_hoverValid && !_panning && _drag != EditorTool.Start && Tool != EditorTool.Start)
        {
            switch (Tool)
            {
                case EditorTool.Brush:
                case EditorTool.Eraser:
                case EditorTool.Line:
                    DrawBrushOutline(l, _hover, px);
                    break;
                default:
                    DrawOutline(l, [(0, 0)], _hover, px);
                    break;
            }
        }
        DrawStart(l, px);
    }

    private void DrawBrushOutline(DrawLayer l, Vector2I cell, float px)
    {
        var key = (_doc.BrushSize, _doc.BrushShape);
        if (!s_outlines.TryGetValue(key, out var edges))
        {
            var set = new HashSet<(int, int)>();
            foreach (var d in Mask(key.Item1, key.Item2)) set.Add(d);
            var list = new List<Vector2>();
            foreach (var (x, y) in set)
            {
                if (!set.Contains((x, y - 1))) { list.Add(new Vector2(x, y)); list.Add(new Vector2(x + 1, y)); }
                if (!set.Contains((x, y + 1))) { list.Add(new Vector2(x, y + 1)); list.Add(new Vector2(x + 1, y + 1)); }
                if (!set.Contains((x - 1, y))) { list.Add(new Vector2(x, y)); list.Add(new Vector2(x, y + 1)); }
                if (!set.Contains((x + 1, y))) { list.Add(new Vector2(x + 1, y)); list.Add(new Vector2(x + 1, y + 1)); }
            }
            s_outlines[key] = edges = list.ToArray();
        }
        l.DrawSetTransform(new Vector2(cell.X, cell.Y));
        l.DrawMultiline(edges, new Color(0, 0, 0, 0.8f), 3f * px);
        l.DrawMultiline(edges, new Color(1, 1, 1, 0.95f), 1.5f * px);
        l.DrawSetTransform(Vector2.Zero);
    }

    private static void DrawOutline(DrawLayer l, (int, int)[] cells, Vector2I at, float px)
    {
        foreach (var (x, y) in cells)
        {
            var r = new Rect2(at.X + x, at.Y + y, 1, 1);
            l.DrawRect(r, new Color(0, 0, 0, 0.8f), false, 3f * px);
            l.DrawRect(r, new Color(1, 1, 1, 0.95f), false, 1.5f * px);
        }
    }

    /// <summary>The brush's cells relative to the brush cell, the same as EditDocument stamps them (lo = -((n-1)/2), round = centre within n/2-0.1).</summary>
    private static (int Dx, int Dy)[] Mask(int size, BrushShape shape)
    {
        if (s_masks.TryGetValue((size, shape), out var m)) return m;
        var list = new List<(int, int)>();
        int lo = -((size - 1) / 2);
        double r = size / 2.0 - 0.1, r2 = r * r;
        for (int j = 0; j < size; j++)
            for (int i = 0; i < size; i++)
            {
                double ox = i + 0.5 - size / 2.0, oy = j + 0.5 - size / 2.0;
                if (shape == BrushShape.Square || ox * ox + oy * oy < r2) list.Add((lo + i, lo + j));
            }
        return s_masks[(size, shape)] = list.ToArray();
    }

    /// <summary>The start being shown: the one in the document, or the one being placed / rotated.</summary>
    private (Vector2 Centre, float AngleDeg) ShownStart()
    {
        if (_drag == EditorTool.Start) return (new Vector2(_pendCell.X + 0.5f, _pendCell.Y + 0.5f), EditDocument.SnapAngle(_pendAngle));
        var s = _doc.Start;
        return (new Vector2(s.X, s.Y), s.AngleDeg);
    }

    private bool HandleHit(Vector2 local)
    {
        if (_doc is null || Tool != EditorTool.Start) return false;
        var (c, deg) = ShownStart();
        var handle = c + Vector2.FromAngle(Mathf.DegToRad(deg)) * HandleDistance;
        return (_world.Position + handle * PixelsPerCell).DistanceTo(local) <= HandleHitPixels;
    }

    /// <summary>The M2 start arrow (a triangle about 4.4 cells long, 5.3 wide, accent colour) as an overlay, plus the rotate handle with the start tool.</summary>
    private void DrawStart(DrawLayer l, float px)
    {
        var (c, deg) = ShownStart();
        float a = Mathf.DegToRad(deg);
        var dir = Vector2.FromAngle(a);
        var perp = new Vector2(-dir.Y, dir.X);
        Vector2 tip = c + dir * 2.4f, b1 = c - dir * 2f + perp * 2.64f, b2 = c - dir * 2f - perp * 2.64f;
        l.DrawColoredPolygon([tip, b1, b2], MapTexture.StartMark);
        l.DrawPolyline([tip, b1, b2, tip], new Color(0, 0, 0, 0.75f), 1.5f * px);
        if (Tool != EditorTool.Start) return;
        var handle = c + dir * HandleDistance;
        _handleHot = _hoverValid && HandleHit(_hoverLocal);
        l.DrawLine(tip, handle, UiTheme.Accent, 1.5f * px);
        float r = 6f * px;
        l.DrawCircle(handle, r + 1.5f * px, new Color(0, 0, 0, 0.8f));
        l.DrawCircle(handle, r, _handleHot ? Colors.White : UiTheme.Accent);
    }
}
