using System;
using Godot;
using Nitrogenesis.Sim.Brain;
using Nitrogenesis.Ui;

namespace Nitrogenesis.View;

/// <summary>
/// The live brain of the followed car (PLAN §6.2): a column of circles per layer (inputs, hidden, outputs), a line for each
/// weight (orange = positive, blue = negative, thicker and brighter = stronger) and the neurons filled by what they computed in
/// the last tick (orange = towards +1, blue = towards −1, dark = 0).
/// </summary>
/// <remarks>
/// The weight lines are a child (<see cref="Lines"/>) that redraws only when the car, generation or shape changed (Godot keeps
/// the draw commands of a control until it asks to redraw), into buffers made once for the biggest possible brain; a frame only
/// redraws the panel and the circles. Nothing allocates per frame.
/// </remarks>
public partial class BrainView : Control
{
    private const float Margin = 14f, Header = 26f;
    private static readonly Color Positive = UiTheme.Accent, Negative = new(0.35f, 0.62f, 1f), Idle = new(0.16f, 0.17f, 0.23f);

    /// <summary>The weight lines (every weight of a brain with 39 inputs, 32 + 32 hidden and 2 outputs fits).</summary>
    private sealed partial class Lines : Control
    {
        public const int Capacity = 40 * 32 + 33 * 32 + 33 * 2;
        public readonly Vector2[] From = new Vector2[Capacity], To = new Vector2[Capacity];
        public readonly Color[] Color = new Color[Capacity];
        public readonly float[] Width = new float[Capacity];
        public int Count;

        public Lines() => MouseFilter = MouseFilterEnum.Ignore;

        public override void _Draw()
        {
            for (int i = 0; i < Count; i++) DrawLine(From[i], To[i], Color[i], Width[i], true);
        }
    }

    private readonly Lines _lines = new();
    private BrainShape _shape;
    private float[] _values = Array.Empty<float>();
    private int _version = -1;
    private string _title = "";
    private int[] _layers = [];
    private Vector2[][] _nodes = [];

    public BrainView()
    {
        CustomMinimumSize = new Vector2(360, 250);
        MouseFilter = MouseFilterEnum.Ignore;
        _lines.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(_lines);
    }

    /// <summary>Shows the brain of a car.</summary>
    /// <param name="version">Changes when the car, the generation or the shape changed (the weight lines are then rebuilt).</param>
    /// <param name="values">Inputs, hidden and output values (see <see cref="BrainProbe"/>), <paramref name="count"/> long.</param>
    /// <param name="weights">The genome.</param>
    public void Show(BrainShape shape, int version, string title, float[] values, int count, float[] weights)
    {
        _title = title;
        if (_values.Length != count) _values = new float[count];
        Array.Copy(values, _values, count);
        if (version != _version || shape != _shape)
        {
            _version = version;
            if (shape != _shape || _nodes.Length == 0)
            {
                _shape = shape;
                Layout(shape);
            }
            BuildLines(weights);
        }
        QueueRedraw();
    }

    /// <summary>Where the circles go: layers left to right, each spread over the height.</summary>
    private void Layout(BrainShape shape)
    {
        _layers = shape.Hidden2 > 0 ? [shape.Inputs, shape.Hidden1, shape.Hidden2, shape.Outputs] : [shape.Inputs, shape.Hidden1, shape.Outputs];
        _nodes = new Vector2[_layers.Length][];
        var size = CustomMinimumSize;
        float top = Margin + Header, bottom = size.Y - Margin;
        for (int l = 0; l < _layers.Length; l++)
        {
            float x = Margin + 30f + l * (size.X - 2 * Margin - 75f) / (_layers.Length - 1);
            int n = _layers[l];
            _nodes[l] = new Vector2[n];
            for (int i = 0; i < n; i++)
                _nodes[l][i] = new Vector2(x, n == 1 ? (top + bottom) / 2 : top + (bottom - top) * (i + 0.5f) / n);
        }
    }

    /// <summary>One line per weight in the genome's layout: each neuron's incoming weights in input order, then its bias.</summary>
    private void BuildLines(float[] weights)
    {
        int count = 0, offset = 0;
        for (int l = 0; l + 1 < _layers.Length; l++)
        {
            int from = _layers[l], to = _layers[l + 1];
            for (int j = 0; j < to; j++)
            {
                for (int i = 0; i < from; i++)
                {
                    float w = weights[offset + j * (from + 1) + i];
                    float strength = Math.Min(MathF.Abs(w), 2f);
                    if (strength < 0.04f) continue;
                    Color c = w >= 0 ? Positive : Negative;
                    c.A = 0.12f + 0.55f * Math.Min(strength / 1.5f, 1f);
                    _lines.From[count] = _nodes[l][i];
                    _lines.To[count] = _nodes[l + 1][j];
                    _lines.Color[count] = c;
                    _lines.Width[count] = 0.7f + 0.9f * strength;
                    count++;
                }
            }
            offset += to * (from + 1);
        }
        _lines.Count = count;
        _lines.QueueRedraw();
    }

    public override void _Draw()
    {
        var size = CustomMinimumSize;
        DrawRect(new Rect2(Vector2.Zero, size), new Color(UiTheme.Panel.R, UiTheme.Panel.G, UiTheme.Panel.B, 0.88f));
        DrawRect(new Rect2(Vector2.Zero, size), UiTheme.Border, false, 1f);
        var font = ThemeDB.FallbackFont;
        DrawString(font, new Vector2(Margin, Margin + 8), _title, HorizontalAlignment.Left, -1, 14, UiTheme.Accent);
        if (_nodes.Length == 0 || _values.Length < BrainProbe.ValueCount(_shape)) return;

        int value = 0;
        for (int l = 0; l < _nodes.Length; l++)
        {
            float radius = Math.Clamp(Math.Min((size.Y - 2 * Margin - Header) / _layers[l] * 0.4f, 7f), 1.6f, 7f);
            for (int i = 0; i < _nodes[l].Length; i++)
            {
                float v = Math.Clamp(_values[value++], -1f, 1f);
                DrawCircle(_nodes[l][i], radius, Idle.Lerp(v >= 0 ? Positive : Negative, MathF.Abs(v)));
                if (radius > 3f) DrawArc(_nodes[l][i], radius, 0, MathF.Tau, 12, UiTheme.Muted, 1f);
            }
        }
        // The two outputs are named.
        for (int i = 0; i < _nodes[^1].Length && i < OutputNames.Length; i++)
            DrawString(font, _nodes[^1][i] + new Vector2(11, 5), OutputNames[i], HorizontalAlignment.Left, -1, 12, UiTheme.Text);
        DrawString(font, new Vector2(Margin, size.Y - 3), "inputs", HorizontalAlignment.Left, -1, 11, UiTheme.Muted);
    }

    private static readonly string[] OutputNames = ["throttle", "steer"];
}
