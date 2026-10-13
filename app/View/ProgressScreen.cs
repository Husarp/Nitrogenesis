using System;
using System.Globalization;
using Godot;
using Nitrogenesis.Sim.History;
using Nitrogenesis.Sim.Racing;
using Nitrogenesis.Ui;

namespace Nitrogenesis.View;

/// <summary>
/// "Train without visuals" (PLAN §4): the map is not drawn; this screen shows the live graph, generations per second
/// and the progress towards the stop condition, refreshed at ~10 FPS (the view lowers the frame cap while it is
/// shown). Cancel (or Esc) goes back to the view; so does meeting the condition.
/// </summary>
public partial class ProgressScreen : Control
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private Label _condition = null!, _generation = null!, _rate = null!, _elapsed = null!, _bestTime = null!, _last = null!, _footer = null!;
    private ProgressBar _bar = null!;
    private int _startGeneration;
    private TrainToCondition _goal;
    private double _seconds;

    public GraphView Graph { get; } = new(900, true);

    public event Action? CancelPressed;

    public override void _Ready()
    {
        Theme = UiTheme.Theme;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        var bg = new ColorRect { Color = UiTheme.Background };
        bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(bg);
        var margin = new MarginContainer();
        margin.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        foreach (string side in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride($"margin_{side}", 40);
        AddChild(margin);
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 14);
        margin.AddChild(column);

        column.AddChild(UiTheme.Label("TRAINING WITHOUT VISUALS", 32, UiTheme.Accent));
        _condition = UiTheme.Label("", 18, UiTheme.Text);
        column.AddChild(_condition);

        var stats = new HBoxContainer();
        stats.AddThemeConstantOverride("separation", 48);
        column.AddChild(stats);
        _generation = Big(stats, "Generation");
        _rate = Big(stats, "Generations / s");
        _elapsed = Big(stats, "Time");
        _bestTime = Big(stats, "Best time (segment)");

        _bar = new ProgressBar { MinValue = 0, MaxValue = 1, CustomMinimumSize = new Vector2(0, 22), ShowPercentage = true };
        column.AddChild(_bar);
        _last = UiTheme.Label("", 15, UiTheme.Muted);
        column.AddChild(_last);

        var graphPanel = new PanelContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        var graphColumn = new VBoxContainer();
        graphPanel.AddChild(graphColumn);
        var head = new HBoxContainer();
        head.AddChild(UiTheme.Label("Score per generation", 14, UiTheme.Muted));
        head.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        head.AddChild(GraphView.Legend(13));
        graphColumn.AddChild(head);
        Graph.SizeFlagsVertical = SizeFlags.ExpandFill;
        Graph.CustomMinimumSize = new Vector2(0, 160);
        graphColumn.AddChild(Graph);
        column.AddChild(graphPanel);

        var bottom = new HBoxContainer();
        bottom.AddThemeConstantOverride("separation", 16);
        var cancel = new Button { Text = "Cancel  (Esc)", CustomMinimumSize = new Vector2(180, 44), FocusMode = FocusModeEnum.None };
        cancel.Pressed += () => CancelPressed?.Invoke();
        bottom.AddChild(cancel);
        _footer = UiTheme.Label("", 14, UiTheme.Muted);
        _footer.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        bottom.AddChild(_footer);
        column.AddChild(bottom);
    }

    private static Label Big(HBoxContainer row, string name)
    {
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 0);
        box.AddChild(UiTheme.Label(name, 14, UiTheme.Muted));
        var value = UiTheme.Label("—", 34, UiTheme.Text);
        box.AddChild(value);
        row.AddChild(box);
        return value;
    }

    /// <summary>A new run starts (from generation <paramref name="generation"/>).</summary>
    public void Begin(TrainToCondition goal, int generation, string footer)
    {
        _goal = goal;
        _startGeneration = generation;
        _seconds = 0;
        _condition.Text = "Training " + goal.Describe() + "…";
        _bar.Visible = goal.Kind == TrainToKind.Generation;
        _bar.Value = 0;
        _footer.Text = footer;
        _last.Text = "";
    }

    /// <summary>Refreshes the numbers (every frame; the frame cap is ~10 FPS here).</summary>
    public void Refresh(in TrainingStats s, double delta)
    {
        _seconds += delta;
        _generation.Text = s.Generation.ToString(Inv);
        _rate.Text = s.GenerationsPerSecond > 0 ? s.GenerationsPerSecond.ToString(s.GenerationsPerSecond >= 10 ? "0" : "0.0", Inv) : "…";
        _elapsed.Text = Hud.Clock(_seconds);
        _bestTime.Text = float.IsFinite(s.BestTimeTicks) ? (s.BestTimeTicks / RacingSettings.TicksPerSecond).ToString("0.00", Inv) + " s" : "—";
        if (_goal.Kind == TrainToKind.Generation)
        {
            int total = Math.Max(1, _goal.Generation - _startGeneration);
            _bar.Value = Math.Clamp((s.Generation - _startGeneration) / (double)total, 0, 1);
        }
        if (s.LastGeneration >= 0)
        {
            var l = s.LastStats;
            string time = l.FinishedCount > 0 ? $", best time {l.BestFinishTicks / RacingSettings.TicksPerSecond:0.00} s" : "";
            _last.Text = $"Last generation ({s.LastGeneration}): {l.FinishedCount} of {s.PopulationSize} finished{time} · best score {l.BestScore:0.000} · average {l.AverageScore:0.000}"
                         + (s.BoostActive ? " · boost active" : "");
        }
    }
}
