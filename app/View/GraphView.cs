using System;
using System.Globalization;
using Godot;
using Nitrogenesis.Ui;

namespace Nitrogenesis.View;

/// <summary>
/// The training graph (PLAN §6.2): best (gold) and average (blue) score per generation, with a dashed line at the
/// finish threshold (score 2: any finisher scores above it) and a divider line where a new segment run starts
/// (PLAN §5). The owner fills <see cref="Best"/>, <see cref="Average"/> and <see cref="Dividers"/> (e.g. with
/// <c>TrainingHost.CopyGraph</c>) and calls <see cref="SetData"/>; drawing then allocates nothing.
/// </summary>
/// <remarks>
/// Small (in the training view's corner): a click raises <see cref="Clicked"/> (the view opens the large one). Large:
/// axis labels and, under the mouse, the generation and its scores. The axis label strings are made in
/// <see cref="SetData"/>, not while drawing.
/// </remarks>
public partial class GraphView : Control
{
    public static readonly Color BestColor = new(1f, 0.8f, 0.2f);
    public static readonly Color AverageColor = new(0.45f, 0.75f, 1f);
    private static readonly Color GridColor = new(1f, 1f, 1f, 0.08f);
    private static readonly Color FinishColor = new(0.65f, 0.89f, 0.63f, 0.45f);
    private static readonly Color DividerColor = new(0.98f, 0.6f, 0.25f, 1f);
    private const int MaxDividers = 64;
    private const int MaxYTicks = 16;

    private readonly Vector2[] _line;
    private int _count, _dividerCount, _generations;
    private float _low, _high, _yMin, _yMax;
    private readonly string[] _yLabels = new string[MaxYTicks + 1];
    private int _yTicks = 2;
    private readonly string[] _xLabels = new string[5];
    private readonly float[] _xLabelAt = new float[5]; // 0…1 across the plot; NaN = not drawn
    private string _hover = "";
    private int _hoverBucket = -1;
    private Font? _font;

    /// <param name="buckets">Points drawn at most (one per generation, or buckets of consecutive generations).</param>
    /// <param name="large">Axis labels and hover values.</param>
    public GraphView(int buckets, bool large)
    {
        Best = new float[buckets];
        Average = new float[buckets];
        Dividers = new int[MaxDividers];
        _line = new Vector2[buckets];
        Large = large;
        for (int i = 0; i < _yLabels.Length; i++) _yLabels[i] = "";
        for (int i = 0; i < _xLabels.Length; i++)
        {
            _xLabels[i] = "";
            _xLabelAt[i] = float.NaN;
        }
        MouseFilter = MouseFilterEnum.Stop;
    }

    public GraphView() : this(300, false) { }

    public bool Large { get; }
    public float[] Best { get; }
    public float[] Average { get; }
    public int[] Dividers { get; }
    /// <summary>The data version shown (the owner compares it with the source's).</summary>
    public int Version { get; set; } = -1;

    /// <summary>A left click on the graph.</summary>
    public event Action? Clicked;

    /// <summary>The buffers hold <paramref name="count"/> points over <paramref name="generations"/> generations.</summary>
    public void SetData(int count, int dividerCount, int generations, float low, float high)
    {
        _count = count;
        _dividerCount = Math.Min(dividerCount, MaxDividers);
        _generations = generations;
        _low = low;
        _high = high;
        _yMin = float.IsNaN(low) ? 0f : MathF.Min(0f, MathF.Floor(low * 2f) / 2f);
        _yMax = float.IsNaN(high) ? 1f : MathF.Max(1f, MathF.Ceiling(high * 2f) / 2f);
        // Grid every 0.5 score (every 1 when the range is wide).
        float step = _yMax - _yMin > 6f ? 1f : 0.5f;
        _yTicks = Math.Clamp((int)MathF.Round((_yMax - _yMin) / step), 1, MaxYTicks);
        if (Large)
        {
            for (int i = 0; i <= _yTicks; i++)
                _yLabels[i] = (_yMin + (_yMax - _yMin) * i / _yTicks).ToString("0.0", CultureInfo.InvariantCulture);
            // Up to 5 whole generations, evenly spread, each drawn where that generation is.
            int last = -1;
            for (int i = 0; i < _xLabels.Length; i++)
            {
                int g = generations <= 0 ? -1 : (int)Math.Round((generations - 1) * i / (double)(_xLabels.Length - 1), MidpointRounding.AwayFromZero);
                bool show = g > last;
                _xLabels[i] = show ? g.ToString(CultureInfo.InvariantCulture) : "";
                _xLabelAt[i] = !show ? float.NaN : generations <= 1 ? 0.5f : g / (float)(generations - 1);
                if (show) last = g;
            }
            UpdateHover();
        }
        QueueRedraw();
    }

    private Rect2 PlotRect()
    {
        Vector2 size = Size;
        return Large
            ? new Rect2(56, 12, Math.Max(10, size.X - 72), Math.Max(10, size.Y - 44))
            : new Rect2(6, 6, Math.Max(10, size.X - 12), Math.Max(10, size.Y - 12));
    }

    private float X(Rect2 r, int bucket) => r.Position.X + (_count <= 1 ? 0.5f : bucket / (float)(_count - 1)) * r.Size.X;

    private float Y(Rect2 r, float score) => r.End.Y - (score - _yMin) / (_yMax - _yMin) * r.Size.Y;

    public override void _Draw()
    {
        _font ??= GetThemeDefaultFont();
        Rect2 r = PlotRect();
        DrawRect(r, new Color(0, 0, 0, 0.25f));
        // Grid at every 0.5 score in the large graph; the finish threshold (score 2) in both.
        if (Large)
        {
            for (int i = 0; i <= _yTicks; i++)
            {
                float y = r.End.Y - r.Size.Y * i / _yTicks;
                DrawLine(new Vector2(r.Position.X, y), new Vector2(r.End.X, y), GridColor);
                DrawString(_font, new Vector2(4, y + 5), _yLabels[i], HorizontalAlignment.Left, 48, 13, UiTheme.Muted);
            }
            for (int i = 0; i < _xLabels.Length; i++)
            {
                if (float.IsNaN(_xLabelAt[i])) continue;
                float x = r.Position.X + r.Size.X * _xLabelAt[i];
                DrawString(_font, new Vector2(x - 30, r.End.Y + 18), _xLabels[i], HorizontalAlignment.Center, 60, 13, UiTheme.Muted);
            }
        }
        if (_yMax > 2f && _yMin < 2f) DrawDashedLine(new Vector2(r.Position.X, Y(r, 2f)), new Vector2(r.End.X, Y(r, 2f)), FinishColor, 1f, 6f);

        // Segment dividers.
        for (int i = 0; i < _dividerCount; i++)
        {
            if (_generations <= 1) break;
            // A divider at the generation running now (not on the graph yet) is drawn at the right edge.
            float x = r.Position.X + Math.Min(Dividers[i], _generations - 1) / (float)(_generations - 1) * r.Size.X;
            DrawDashedLine(new Vector2(x, r.Position.Y), new Vector2(x, r.End.Y), DividerColor, Large ? 2f : 1.5f, 5f);
        }

        DrawSeries(r, Average, AverageColor, Large ? 1.5f : 1.2f);
        DrawSeries(r, Best, BestColor, Large ? 2f : 1.5f);

        if (Large && _hoverBucket >= 0 && _hoverBucket < _count)
        {
            float x = X(r, _hoverBucket);
            DrawLine(new Vector2(x, r.Position.Y), new Vector2(x, r.End.Y), new Color(1, 1, 1, 0.35f));
            DrawString(_font, new Vector2(r.Position.X + 8, r.Position.Y + 18), _hover, HorizontalAlignment.Left, -1, 14, UiTheme.Text);
        }
    }

    /// <summary>
    /// One polyline per run of known points: the run is copied to the start of the buffer and the rest is padded with its
    /// last point (zero-length segments draw nothing), so nothing is allocated.
    /// </summary>
    private void DrawSeries(Rect2 r, float[] values, Color color, float width)
    {
        int i = 0;
        while (i < _count)
        {
            while (i < _count && float.IsNaN(values[i])) i++;
            int n = 0;
            while (i < _count && !float.IsNaN(values[i]))
            {
                _line[n++] = new Vector2(X(r, i), Y(r, values[i]));
                i++;
            }
            if (n == 0) break;
            if (n == 1)
            {
                DrawCircle(_line[0], width * 1.5f, color);
                continue;
            }
            for (int k = n; k < _line.Length; k++) _line[k] = _line[n - 1];
            DrawPolyline(_line, color, width, true);
        }
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
        {
            Clicked?.Invoke();
            AcceptEvent();
        }
        else if (Large && e is InputEventMouseMotion motion)
        {
            Rect2 r = PlotRect();
            int bucket = _count <= 1 ? 0 : (int)MathF.Round((motion.Position.X - r.Position.X) / r.Size.X * (_count - 1));
            bucket = motion.Position.X < r.Position.X - 4 || motion.Position.X > r.End.X + 4 ? -1 : Math.Clamp(bucket, 0, Math.Max(0, _count - 1));
            if (bucket != _hoverBucket)
            {
                _hoverBucket = bucket;
                UpdateHover();
                QueueRedraw();
            }
        }
    }

    private void UpdateHover()
    {
        if (_hoverBucket < 0 || _hoverBucket >= _count || _generations == 0)
        {
            _hover = "";
            return;
        }
        int from = (int)((long)_hoverBucket * _generations / _count), to = (int)((long)(_hoverBucket + 1) * _generations / _count) - 1;
        string gens = from == to ? $"generation {from}" : $"generations {from}–{to}";
        string best = float.IsNaN(Best[_hoverBucket]) ? "—" : Best[_hoverBucket].ToString("0.000", CultureInfo.InvariantCulture);
        string avg = float.IsNaN(Average[_hoverBucket]) ? "—" : Average[_hoverBucket].ToString("0.000", CultureInfo.InvariantCulture);
        _hover = $"{gens}:  best {best}  ·  average {avg}";
    }

    public override void _Notification(int what)
    {
        if (what == NotificationResized) QueueRedraw();
        if (what == NotificationMouseExit && _hoverBucket >= 0)
        {
            _hoverBucket = -1;
            QueueRedraw();
        }
    }

    /// <summary>The legend text of the series.</summary>
    public static Control Legend(int size = 13)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);
        row.AddChild(UiTheme.Label("━ best", size, BestColor));
        row.AddChild(UiTheme.Label("━ average", size, AverageColor));
        row.AddChild(UiTheme.Label("┆ new segment", size, DividerColor));
        return row;
    }
}
