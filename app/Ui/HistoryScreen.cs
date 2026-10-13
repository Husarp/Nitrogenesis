using System;
using Godot;
using Nitrogenesis.Sim.History;
using Nitrogenesis.View;

namespace Nitrogenesis.Ui;

/// <summary>
/// A session made with another physics version (PLAN §5.1): its graph and history, read-only. Recordings still play
/// ("Watch best"); watching a whole generation, forking and continuing are disabled with the reason.
/// </summary>
public partial class HistoryScreen : Control
{
    private Session _session = null!;
    private TrainingPanel _panel = null!;
    private ReplayView? _replay;
    private Control _content = null!;

    public event Action? Back;

    public static HistoryScreen Create(Session session) => new() { _session = session };

    public override void _Ready()
    {
        Theme = UiTheme.Theme;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var bg = new ColorRect { Color = UiTheme.Background };
        bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(bg);
        var margin = new MarginContainer();
        margin.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        foreach (string side in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride($"margin_{side}", 32);
        AddChild(margin);
        _content = margin;
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 20);
        margin.AddChild(row);

        var left = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        left.AddThemeConstantOverride("separation", 12);
        row.AddChild(left);
        left.AddChild(UiTheme.Label("SESSION HISTORY (READ-ONLY)", 28, UiTheme.Accent));
        left.AddChild(Modal.Text(_session.Compatibility.Message ?? "", UiTheme.Text));
        var graphPanel = new PanelContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        var graphColumn = new VBoxContainer();
        graphPanel.AddChild(graphColumn);
        graphColumn.AddChild(GraphView.Legend(13));
        var graph = new GraphView(900, true) { SizeFlagsVertical = SizeFlags.ExpandFill };
        graphColumn.AddChild(graph);
        left.AddChild(graphPanel);
        var back = new Button { Text = "Back", CustomMinimumSize = new Vector2(140, 44), SizeFlagsHorizontal = SizeFlags.ShrinkBegin };
        back.Pressed += () => Back?.Invoke();
        left.AddChild(back);

        _panel = TrainingPanel.Create(_session, readOnly: true);
        _panel.CustomMinimumSize = new Vector2(580, 0);
        _panel.WatchBest += WatchBest;
        _panel.CloseRequested += () => Back?.Invoke();
        row.AddChild(_panel);

        // The graph from the stored stats of the active lineage.
        var data = new ScoreGraph();
        string branch = _session.ActiveBranch;
        foreach (var e in _session.LineageEntries(branch)) data.Set(e.Generation, e.Stats.BestScore, e.Stats.AverageScore);
        data.SetDividers(_session.SegmentRunStarts(branch));
        int n = data.Sample(graph.Best, graph.Average, out float low, out float high);
        int dividers = data.CopyDividers(graph.Dividers);
        graph.SetData(n, dividers, data.Count, low, high);
        CallDeferred(MethodName.RefreshPanel);
    }

    private void RefreshPanel() => _panel.Refresh(0, _session.ActiveBranch, _session.Current.Generation, force: true);

    /// <summary>"Watch best" of a generation of the active lineage (also the debug script's <c>replay-best:G</c>).</summary>
    public void WatchBest(int generation)
    {
        var entry = _session.EntryAt(_session.ActiveBranch, generation);
        if (entry is null || entry.RecordingBytes.Length == 0) return;
        var segment = _session.SegmentAt(_session.ActiveBranch, generation);
        _replay = ReplayView.ForBest(segment.Track, entry, segment.Config.TimeLimitTicks, new Color(1f, 0.8f, 0.2f), $"generation {generation}", false, false);
        _replay.BackRequested += () =>
        {
            _replay?.QueueFree();
            _replay = null;
            Visible = true;
            RenderingServer.SetDefaultClearColor(UiTheme.Background);
        };
        Visible = false;
        GetParent().AddChild(_replay);
    }

    /// <summary>Leaves the replay (debug script's <c>back</c>).</summary>
    public void BackFromReplay() => _replay?.Back();

    public override void _ExitTree() => _replay?.QueueFree();

    public override void _UnhandledInput(InputEvent e)
    {
        if (Modal.OpenCount > 0 || _replay is not null) return;
        if (e is InputEventKey { Pressed: true, Keycode: Key.Escape })
        {
            Back?.Invoke();
            GetViewport().SetInputAsHandled();
        }
    }
}
