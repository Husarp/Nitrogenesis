namespace Nitrogenesis.Sim.Map;

/// <summary>Map sizes offered by the editor (PLAN §3.1).</summary>
public enum MapSize { S, M, L }

public enum BrushShape { Round, Square }

/// <summary>
/// The track editor's document (PLAN §6.3): a grid + start with edit tools and undo/redo. Pure logic, no Godot.
/// Used from the UI thread only; hand <see cref="Snapshot"/> to other threads.
/// </summary>
/// <remarks>
/// Conventions:
/// <list type="bullet">
/// <item>Brush of size n at cell (x, y) covers the square lo..lo+n-1 with lo = x - (n-1)/2 (integer division): odd
/// sizes are centred on the cell; even sizes extend one cell more towards +x/+y (size 2 = x..x+1).</item>
/// <item>Round brush: a stamped cell is included when its centre lies within (n/2 - 0.1) of the brush centre, so
/// size 1 and 2 are a point and a 2×2 block, 3 is a plus, 15 a disc. Square: the full n×n block.</item>
/// <item>Every call that paints uses the document's current <see cref="PaintType"/>, <see cref="BrushSize"/> and
/// <see cref="BrushShape"/> (a stroke keeps the values from its BeginStroke). The eraser is PaintType = Wall.</item>
/// <item>Every user action = exactly one undo step; actions that change nothing add none. Everything is clipped
/// to the grid.</item>
/// </list>
/// </remarks>
public sealed class EditDocument
{
    public const int MinBrush = 1, MaxBrush = 15;
    /// <summary>Most undo steps kept; the oldest is dropped beyond this.</summary>
    public const int MaxUndoSteps = 200;
    public const float AngleStepDeg = 15f;

    private sealed class Step
    {
        public long Id;
        public int[] Indices = [];
        public byte[] Old = [];
        public byte New;
        public DirtyRect Rect;
        public bool StartChanged;
        public TrackStart OldStart, NewStart;
    }

    private readonly List<Step> _undo = [];
    private readonly List<Step> _redo = [];
    private long _nextId = 1;
    private long _baseId;       // id of the (dropped) step the oldest kept step builds on; 0 at the start
    private long _savedId;      // top-of-stack id at the last MarkSaved
    private readonly Dictionary<(int, BrushShape), (int Dx, int Dy)[]> _masks = [];

    // pending change accumulation (one stroke / one tool action)
    private readonly List<int> _pIdx = [];
    private readonly List<byte> _pOld = [];
    private int _pMinX, _pMinY, _pMaxX, _pMaxY;
    private bool _stroking;
    private CellType _strokeType;
    private int _strokeSize, _lastX, _lastY;
    private BrushShape _strokeShape;

    /// <summary>The editable grid. Read it; change it only through the tools.</summary>
    public Grid Grid { get; }
    public TrackStart Start { get; private set; }
    public string Name { get; set; }
    public TrackMeta Meta { get; set; }

    public CellType PaintType { get; set; } = CellType.Road;
    public BrushShape BrushShape { get; set; } = BrushShape.Round;
    private int _brushSize = 3;
    /// <summary>Brush size in cells; values outside 1–15 are clamped.</summary>
    public int BrushSize { get => _brushSize; set => _brushSize = Math.Clamp(value, MinBrush, MaxBrush); }

    /// <summary>Bumps by one on every real change (paint, fill, start, undo, redo). Starts at 0.</summary>
    public long Revision { get; private set; }

    /// <summary>Raised after every real change.</summary>
    public event Action<EditResult>? Changed;

    public bool CanUndo => _undo.Count > 0 || _stroking && _pIdx.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public int UndoCount => _undo.Count + (_stroking && _pIdx.Count > 0 ? 1 : 0);
    public int RedoCount => _redo.Count;
    public bool StrokeActive => _stroking;

    /// <summary>True when the document differs from the state at the last <see cref="MarkSaved"/> (or creation).</summary>
    public bool IsDirty => (_stroking && _pIdx.Count > 0) || TopId != _savedId;
    private long TopId => _undo.Count > 0 ? _undo[^1].Id : _baseId;

    /// <summary>Edits a deep copy of <paramref name="track"/> (the source is never touched).</summary>
    public EditDocument(Track track)
    {
        ArgumentNullException.ThrowIfNull(track);
        Grid = track.Grid.Clone();
        Start = track.Start;
        Name = track.Name;
        Meta = CloneMeta(track.Meta);
    }

    private EditDocument(string name, Grid grid, TrackStart start)
    {
        Name = name;
        Grid = grid;
        Start = start;
        Meta = new TrackMeta();
    }

    public static (int Width, int Height) Dimensions(MapSize size) => size switch
    {
        MapSize.S => (192, 108),
        MapSize.M => (256, 144),
        MapSize.L => (384, 216),
        _ => throw new ArgumentOutOfRangeException(nameof(size)),
    };

    /// <summary>
    /// A new all-Wall map. The start is the centre cell's centre (width/2 + 0.5, height/2 + 0.5), angle 0 (facing +x);
    /// it sits in wall until the user paints road there.
    /// </summary>
    public static EditDocument New(MapSize size, string name = "New track")
    {
        var (w, h) = Dimensions(size);
        return new EditDocument(name, new Grid(w, h), new TrackStart(w / 2 + 0.5f, h / 2 + 0.5f, 0f));
    }

    /// <summary>A new Track with a cloned grid: safe to analyse or save from another thread.</summary>
    public Track Snapshot() => new(Name, Grid.Clone(), Start, CloneMeta(Meta));

    private static TrackMeta CloneMeta(TrackMeta m) => m with { Params = m.Params?.DeepClone().AsObject() };

    /// <summary>Call after a successful save: the current state counts as clean.</summary>
    public void MarkSaved()
    {
        EndStroke();
        _savedId = TopId;
    }

    // ---------------------------------------------------------------- strokes

    /// <summary>Starts a stroke and stamps the brush at (x, y). An unfinished stroke is ended first.</summary>
    public EditResult BeginStroke(int x, int y)
    {
        EndStroke();
        _stroking = true;
        _strokeType = PaintType;
        _strokeSize = _brushSize;
        _strokeShape = BrushShape;
        _lastX = x;
        _lastY = y;
        ResetPending();
        Stamp(x, y, _strokeSize, _strokeShape, _strokeType);
        return Flush();
    }

    /// <summary>Continues the stroke to (x, y), stamping every cell step in between (no gaps).</summary>
    public EditResult MoveStroke(int x, int y)
    {
        if (!_stroking) return EditResult.None;
        StampLine(_lastX, _lastY, x, y, _strokeSize, _strokeShape, _strokeType, skipFirst: true);
        _lastX = x;
        _lastY = y;
        return Flush();
    }

    /// <summary>Ends the stroke and commits it as one undo step (none when nothing changed).</summary>
    public void EndStroke()
    {
        if (!_stroking) return;
        _stroking = false;
        Commit(_strokeType);
    }

    /// <summary>Paints a brush line from a to b as one action.</summary>
    public EditResult Line(int ax, int ay, int bx, int by)
    {
        EndStroke();
        ResetPending();
        StampLine(ax, ay, bx, by, _brushSize, BrushShape, PaintType, skipFirst: false);
        return FinishAction(PaintType);
    }

    /// <summary>Paints the filled rectangle between two corners (inclusive, any order) as one action.</summary>
    public EditResult Rect(int x0, int y0, int x1, int y1)
    {
        EndStroke();
        ResetPending();
        int minX = Math.Max(Math.Min(x0, x1), 0), maxX = Math.Min(Math.Max(x0, x1), Grid.Width - 1);
        int minY = Math.Max(Math.Min(y0, y1), 0), maxY = Math.Min(Math.Max(y0, y1), Grid.Height - 1);
        for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
                Set(Grid.Index(x, y), x, y, PaintType);
        return FinishAction(PaintType);
    }

    /// <summary>
    /// Fills the 4-connected region of cells with the same type as (x, y) with <see cref="PaintType"/>.
    /// No-op when outside the grid or the cell already has that type.
    /// </summary>
    public EditResult Fill(int x, int y)
    {
        EndStroke();
        if (!Grid.InBounds(x, y)) return EditResult.None;
        var target = Grid.Cells[Grid.Index(x, y)];
        var type = PaintType;
        if (target == (byte)type) return EditResult.None;
        ResetPending();
        int w = Grid.Width, h = Grid.Height;
        var stack = new Stack<int>();
        // A cell is repainted when pushed, so it is never pushed twice (painted cells no longer match target).
        Set(Grid.Index(x, y), x, y, type);
        stack.Push(Grid.Index(x, y));
        while (stack.Count > 0)
        {
            int i = stack.Pop();
            int cx = i % w, cy = i / w;
            if (cx > 0) Visit(i - 1, cx - 1, cy);
            if (cx < w - 1) Visit(i + 1, cx + 1, cy);
            if (cy > 0) Visit(i - w, cx, cy - 1);
            if (cy < h - 1) Visit(i + w, cx, cy + 1);
        }
        return FinishAction(PaintType);

        void Visit(int ni, int nx, int ny)
        {
            if (Grid.Cells[ni] != target) return;
            Set(ni, nx, ny, type);
            stack.Push(ni);
        }
    }

    // ---------------------------------------------------------------- start

    /// <summary>Snaps an angle to the nearest 15° and normalises it to [0, 360).</summary>
    public static float SnapAngle(float angleDeg)
    {
        if (!float.IsFinite(angleDeg)) throw new ArgumentOutOfRangeException(nameof(angleDeg));
        double snapped = Math.Floor(angleDeg / AngleStepDeg + 0.5) * AngleStepDeg;
        snapped %= 360.0;
        if (snapped < 0) snapped += 360.0;
        return (float)snapped;
    }

    /// <summary>
    /// Puts the start at the centre of cell (x, y) (clamped into the grid) with the angle snapped to 15°, in [0, 360).
    /// No-op when that equals the current start.
    /// </summary>
    public EditResult SetStart(int x, int y, float angleDeg)
    {
        EndStroke();
        x = Math.Clamp(x, 0, Grid.Width - 1);
        y = Math.Clamp(y, 0, Grid.Height - 1);
        var next = new TrackStart(x + 0.5f, y + 0.5f, SnapAngle(angleDeg));
        if (next == Start) return EditResult.None;
        var step = new Step { Id = _nextId++, StartChanged = true, OldStart = Start, NewStart = next };
        Start = next;
        PushUndo(step);
        return Report(new EditResult(DirtyRect.Empty, true));
    }

    // ---------------------------------------------------------------- undo / redo

    /// <summary>Undoes the last action (an unfinished stroke is ended first and is then what gets undone).</summary>
    public EditResult Undo()
    {
        EndStroke();
        if (_undo.Count == 0) return EditResult.None;
        var s = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(s);
        for (int i = 0; i < s.Indices.Length; i++) Grid.Cells[s.Indices[i]] = s.Old[i];
        if (s.StartChanged) Start = s.OldStart;
        return Report(new EditResult(s.Rect, s.StartChanged));
    }

    public EditResult Redo()
    {
        EndStroke();
        if (_redo.Count == 0) return EditResult.None;
        var s = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(s);
        for (int i = 0; i < s.Indices.Length; i++) Grid.Cells[s.Indices[i]] = s.New;
        if (s.StartChanged) Start = s.NewStart;
        return Report(new EditResult(s.Rect, s.StartChanged));
    }

    // ---------------------------------------------------------------- internals

    private EditResult Report(EditResult r)
    {
        Revision++;
        Changed?.Invoke(r);
        return r;
    }

    private void PushUndo(Step s)
    {
        _redo.Clear();
        _undo.Add(s);
        if (_undo.Count > MaxUndoSteps)
        {
            _baseId = _undo[0].Id;
            _undo.RemoveAt(0);
        }
    }

    private void ResetPending()
    {
        _pIdx.Clear();
        _pOld.Clear();
        _flushedCount = 0;
        _pMinX = int.MaxValue; _pMinY = int.MaxValue; _pMaxX = int.MinValue; _pMaxY = int.MinValue;
    }

    private int _flushedCount;   // pending cells already reported by Flush

    /// <summary>Records and applies one cell change (only when the value differs).</summary>
    private void Set(int index, int x, int y, CellType type)
    {
        if (Grid.Cells[index] == (byte)type) return;
        _pIdx.Add(index);
        _pOld.Add(Grid.Cells[index]);
        Grid.Cells[index] = (byte)type;
        if (x < _pMinX) _pMinX = x;
        if (x > _pMaxX) _pMaxX = x;
        if (y < _pMinY) _pMinY = y;
        if (y > _pMaxY) _pMaxY = y;
    }

    /// <summary>Reports the cells added to the pending change since the previous flush (one stroke point).</summary>
    private EditResult Flush()
    {
        int w = Grid.Width;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        for (int i = _flushedCount; i < _pIdx.Count; i++)
        {
            int x = _pIdx[i] % w, y = _pIdx[i] / w;
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }
        bool any = _pIdx.Count > _flushedCount;
        _flushedCount = _pIdx.Count;
        return any ? Report(new EditResult(DirtyRect.FromBounds(minX, minY, maxX, maxY), false)) : EditResult.None;
    }

    private EditResult FinishAction(CellType type)
    {
        var rect = _pIdx.Count == 0 ? DirtyRect.Empty : DirtyRect.FromBounds(_pMinX, _pMinY, _pMaxX, _pMaxY);
        Commit(type);
        return rect.IsEmpty ? EditResult.None : Report(new EditResult(rect, false));
    }

    /// <summary>Turns the pending cells into an undo step (called when a stroke or action ends).</summary>
    private void Commit(CellType type)
    {
        _flushedCount = 0;
        if (_pIdx.Count == 0) return;
        var step = new Step
        {
            Id = _nextId++,
            Indices = _pIdx.ToArray(),
            Old = _pOld.ToArray(),
            New = (byte)type,
            Rect = DirtyRect.FromBounds(_pMinX, _pMinY, _pMaxX, _pMaxY),
        };
        ResetPending();
        PushUndo(step);
    }

    private void Stamp(int x, int y, int size, BrushShape shape, CellType type)
    {
        int lo = -((size - 1) / 2);
        foreach (var (dx, dy) in Mask(size, shape))
        {
            int cx = x + lo + dx, cy = y + lo + dy;
            if (Grid.InBounds(cx, cy)) Set(Grid.Index(cx, cy), cx, cy, type);
        }
    }

    private void StampLine(int x0, int y0, int x1, int y1, int size, BrushShape shape, CellType type, bool skipFirst)
    {
        // Bresenham: every step moves one cell in x and/or y, so consecutive stamps always touch.
        int dx = Math.Abs(x1 - x0), dy = -Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
        int err = dx + dy, x = x0, y = y0;
        bool first = true;
        while (true)
        {
            if (!(first && skipFirst)) Stamp(x, y, size, shape, type);
            first = false;
            if (x == x1 && y == y1) break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x += sx; }
            if (e2 <= dx) { err += dx; y += sy; }
        }
    }

    private (int Dx, int Dy)[] Mask(int size, BrushShape shape)
    {
        if (_masks.TryGetValue((size, shape), out var m)) return m;
        var list = new List<(int, int)>();
        double r = size / 2.0 - 0.1, r2 = r * r;
        for (int j = 0; j < size; j++)
            for (int i = 0; i < size; i++)
            {
                double ox = i + 0.5 - size / 2.0, oy = j + 0.5 - size / 2.0;
                if (shape == BrushShape.Square || ox * ox + oy * oy < r2) list.Add((i, j));
            }
        return _masks[(size, shape)] = list.ToArray();
    }
}
