using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using Nitrogenesis.Sim.History;
using Nitrogenesis.Sim.Io;
using Nitrogenesis.Sim.Racing;
using Nitrogenesis.Ui;

namespace Nitrogenesis.View;

/// <summary>
/// The Training panel (PLAN §6.2): the history of the active branch's lineage (newest first; each row: Watch best ·
/// Watch whole generation · Fork), the branches (one is active; Switch makes another active) and the segments of the
/// lineage, with the generation time limit as the one "new segment" setting until M7's Settings screen.
/// </summary>
/// <remarks>
/// The history list is virtual: a few row controls show the part that is scrolled into view, so it stays fast with
/// tens of thousands of generations. The panel re-reads the session (under its lock) only when the session's version
/// changed, at most twice a second, and only while it is shown. While the mouse is over the list of the open tab the
/// list stays as it is (new generations would otherwise move the rows, or rebuild the branch rows, under a click), and a
/// history scrolled down keeps showing the same generations.
/// </remarks>
public partial class TrainingPanel : PanelContainer
{
    private const float RowHeight = 31f;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private Session _session = null!;
    private bool _readOnly;
    private TabContainer _tabs = null!;
    private Label _historyInfo = null!;
    private Control _rowsBox = null!;
    private VScrollBar _scroll = null!;
    private readonly List<Row> _rows = new();
    private VBoxContainer _branchesBox = null!, _segmentsBox = null!;
    private SpinBox _timeLimit = null!;
    private Button _applyTimeLimit = null!;
    private List<HistoryEntry> _entries = new();
    private long _version = -1;
    private string _branch = "";
    private int _currentGeneration;
    private double _sinceRefresh = 1;

    /// <summary>"Watch best" / "Watch whole generation" / "Fork…" of a stored generation of the active lineage.</summary>
    public event Action<int>? WatchBest, WatchGeneration, ForkRequested;
    public event Action<string>? SwitchRequested;
    /// <summary>A new generation time limit (seconds, 0 = auto) for the next generation: a new segment.</summary>
    public event Action<float>? TimeLimitRequested;
    public event Action? CloseRequested;

    private sealed class Row
    {
        public HBoxContainer Box = null!;
        public Label Generation = null!, Best = null!, Time = null!, Finished = null!, Snapshot = null!;
        public Button WatchBest = null!, WatchAll = null!, Fork = null!;
        public int Gen = -1;
    }

    /// <param name="session">The session shown.</param>
    /// <param name="readOnly">Another SimVersion (PLAN §5.1): only recordings play.</param>
    public static TrainingPanel Create(Session session, bool readOnly) => new() { _session = session, _readOnly = readOnly };

    public override void _Ready()
    {
        Theme = UiTheme.Theme;
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 8);
        AddChild(column);
        var head = new HBoxContainer();
        head.AddChild(UiTheme.Label("TRAINING PANEL", 18, UiTheme.Accent));
        head.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        head.AddChild(UiTheme.SmallButton("Close  (H)", () => CloseRequested?.Invoke(), 14));
        column.AddChild(head);
        // The reason a read-only session cannot be continued is shown once by the screen that owns the panel (HistoryScreen), and in the buttons' tooltips.

        _tabs = new TabContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        _tabs.AddThemeFontSizeOverride("font_size", 15);
        column.AddChild(_tabs);
        _tabs.AddChild(HistoryTab());
        _tabs.AddChild(BranchesTab());
        _tabs.AddChild(SegmentsTab());
        _tabs.SetTabTitle(0, "History");
        _tabs.SetTabTitle(1, "Branches");
        _tabs.SetTabTitle(2, "Segments");
        _tabs.TabChanged += _ => { if (_branch != "") Refresh(0, _branch, _currentGeneration, force: true); }; // fill the tab now shown
    }

    /// <summary>Scrolls the history list by <paramref name="rows"/> rows (positive = older), as the mouse wheel does (debug scripts).</summary>
    public void ScrollRows(int rows) => _scroll.Value += rows;

    /// <summary>The centre of the history list on screen (debug scripts push real mouse events there).</summary>
    public Vector2 ListCenter => _rowsBox.GetGlobalRect().GetCenter();

    /// <summary>The first generation shown in the history list (debug scripts), or -1.</summary>
    public int FirstShownGeneration => _rows.Count > 0 ? _rows[0].Gen : -1;

    /// <summary>Shows tab 0 (history), 1 (branches) or 2 (segments).</summary>
    public void ShowTab(int tab) => _tabs.CurrentTab = Math.Clamp(tab, 0, 2);

    // ---------------------------------------------------------------- history

    private Control HistoryTab()
    {
        var box = new VBoxContainer { Name = "History" };
        box.AddThemeConstantOverride("separation", 6);
        _historyInfo = UiTheme.Label("", 13, UiTheme.Muted);
        _historyInfo.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        box.AddChild(_historyInfo);
        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 6);
        foreach (var (text, width, tip) in new[]
                 {
                     ("Gen", 44, "Generation"), ("Best", 50, "Best score (2 and above: finished)"), ("Time", 58, "Best finish time"),
                     ("Fin.", 32, "Cars that finished"), ("", 12, "● full population kept · ○ thinned (rebuilt when needed)"),
                 })
        {
            var label = UiTheme.Label(text, 13, UiTheme.Muted);
            label.CustomMinimumSize = new Vector2(width, 0);
            label.TooltipText = tip;
            label.MouseFilter = MouseFilterEnum.Pass;
            header.AddChild(label);
        }
        box.AddChild(header);

        var body = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        box.AddChild(body);
        // A plain control (not a container): the rows are placed by hand, so they never grow it (and resize it again).
        _rowsBox = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill, ClipContents = true };
        _rowsBox.Resized += EnsureRows;
        _rowsBox.GuiInput += e =>
        {
            if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown } wheel)
            {
                _scroll.Value += wheel.ButtonIndex == MouseButton.WheelUp ? -3 : 3;
                _rowsBox.AcceptEvent();
            }
        };
        body.AddChild(_rowsBox);
        _scroll = new VScrollBar { SizeFlagsVertical = SizeFlags.ExpandFill, Step = 1 };
        _scroll.ValueChanged += _ => ShowRows();
        body.AddChild(_scroll);
        return box;
    }

    private void EnsureRows()
    {
        int wanted = Math.Max(1, (int)(_rowsBox.Size.Y / RowHeight));
        while (_rows.Count < wanted) _rows.Add(MakeRow());
        for (int i = 0; i < _rows.Count; i++)
        {
            var box = _rows[i].Box;
            box.Visible = i < wanted;
            box.Position = new Vector2(0, i * RowHeight);
            box.Size = new Vector2(_rowsBox.Size.X, RowHeight);
        }
        _scroll.Page = wanted;
        ShowRows();
    }

    private Row MakeRow()
    {
        var row = new Row { Box = new HBoxContainer { CustomMinimumSize = new Vector2(0, RowHeight) } };
        row.Box.AddThemeConstantOverride("separation", 6);
        row.Box.MouseFilter = MouseFilterEnum.Pass;
        Label Cell(int width, Color color)
        {
            var label = UiTheme.Label("", 14, color);
            label.CustomMinimumSize = new Vector2(width, 0);
            label.MouseFilter = MouseFilterEnum.Pass;
            row.Box.AddChild(label);
            return label;
        }
        row.Generation = Cell(44, UiTheme.Text);
        row.Best = Cell(50, GraphView.BestColor);
        row.Time = Cell(58, UiTheme.Text);
        row.Finished = Cell(32, UiTheme.Muted);
        row.Snapshot = Cell(12, UiTheme.Muted);
        row.Box.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Pass });
        row.WatchBest = UiTheme.SmallButton("Watch best", () => { if (row.Gen >= 0) WatchBest?.Invoke(row.Gen); }, 12);
        row.WatchBest.TooltipText = "Watch best: play this generation's best car (its recording)";
        row.WatchAll = UiTheme.SmallButton("Whole gen.", () => { if (row.Gen >= 0) WatchGeneration?.Invoke(row.Gen); }, 12);
        row.WatchAll.TooltipText = _readOnly ? _session.Compatibility.Message ?? "" : "Watch whole generation: every car of it, simulated again";
        row.Fork = UiTheme.SmallButton("Fork…", () => { if (row.Gen >= 0) ForkRequested?.Invoke(row.Gen); }, 12);
        row.Fork.TooltipText = _readOnly ? _session.Compatibility.Message ?? "" : "Start a new branch from this generation";
        row.Box.AddChild(row.WatchBest);
        row.Box.AddChild(row.WatchAll);
        row.Box.AddChild(row.Fork);
        _rowsBox.AddChild(row.Box);
        return row;
    }

    /// <summary>Fills the visible rows from the newest generation down.</summary>
    private void ShowRows()
    {
        int first = (int)_scroll.Value;
        for (int i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            int index = _entries.Count - 1 - (first + i);
            if (index < 0 || !row.Box.Visible)
            {
                row.Gen = -1;
                row.Generation.Text = row.Best.Text = row.Time.Text = row.Finished.Text = row.Snapshot.Text = "";
                row.WatchBest.Visible = row.WatchAll.Visible = row.Fork.Visible = false;
                continue;
            }
            var e = _entries[index];
            row.Gen = e.Generation;
            row.Generation.Text = e.Generation.ToString(Inv);
            row.Best.Text = e.Stats.BestScore.ToString("0.000", Inv);
            row.Time.Text = float.IsFinite(e.Stats.BestFinishTicks) ? (e.Stats.BestFinishTicks / RacingSettings.TicksPerSecond).ToString("0.00", Inv) + " s" : "—";
            row.Finished.Text = e.Stats.FinishedCount.ToString(Inv);
            row.Snapshot.Text = e.Snapshot is null ? "○" : "●";
            row.WatchBest.Visible = row.WatchAll.Visible = row.Fork.Visible = true;
            row.WatchBest.Disabled = e.RecordingBytes.Length == 0;
            row.WatchAll.Disabled = _readOnly;
            row.Fork.Disabled = _readOnly;
        }
    }

    // ---------------------------------------------------------------- branches and segments

    private Control BranchesTab()
    {
        var scroll = new ScrollContainer { Name = "Branches", HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _branchesBox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _branchesBox.AddThemeConstantOverride("separation", 8);
        scroll.AddChild(_branchesBox);
        return scroll;
    }

    private Control SegmentsTab()
    {
        var box = new VBoxContainer { Name = "Segments" };
        box.AddThemeConstantOverride("separation", 8);
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _segmentsBox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _segmentsBox.AddThemeConstantOverride("separation", 8);
        scroll.AddChild(_segmentsBox);
        box.AddChild(scroll);

        var change = new VBoxContainer();
        change.AddThemeConstantOverride("separation", 6);
        change.AddChild(UiTheme.Label("Generation time limit (a new segment)", 14, UiTheme.Text));
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        _timeLimit = new SpinBox
        {
            MinValue = 0, MaxValue = RacingSettings.MaxTimeLimitSeconds, Step = 0.5, Suffix = "s", CustomMinimumSize = new Vector2(120, 0),
            TooltipText = "0 = auto (the reference driver's time × 2.5)",
        };
        row.AddChild(_timeLimit);
        _applyTimeLimit = UiTheme.SmallButton("Apply at the next generation…", () => TimeLimitRequested?.Invoke((float)_timeLimit.Value), 14);
        _applyTimeLimit.Disabled = _readOnly;
        row.AddChild(_applyTimeLimit);
        change.AddChild(row);
        var note = UiTheme.Label("0 = auto. A change starts a new segment: best time, the stagnation check and the graph restart. More settings come with the Settings screen.", 12, UiTheme.Muted);
        note.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        change.AddChild(note);
        box.AddChild(change);
        return box;
    }

    // ---------------------------------------------------------------- refresh

    /// <summary>Re-reads the session when it changed (call every frame while shown; it limits itself to 2 per second).</summary>
    public void Refresh(double delta, string branch, int currentGeneration, bool force = false)
    {
        _sinceRefresh += delta;
        long version = _session.Version;
        if (!force && (version == _version && branch == _branch && currentGeneration == _currentGeneration || _sinceRefresh < 0.5)) return;
        if (!force && branch == _branch && MouseOverList()) return; // a click lands on the row it was aimed at
        _sinceRefresh = 0;
        bool branchChanged = branch != _branch;
        int before = _entries.Count;
        _version = version;
        _branch = branch;
        _currentGeneration = currentGeneration;

        List<BranchEntry> branches;
        List<SegmentRow> segments;
        long historyBytes, limit;
        int snapshots;
        SegmentEntry current;
        lock (_session.SyncRoot)
        {
            _entries = _session.LineageEntries(branch);
            branches = new List<BranchEntry>(_session.Branches);
            segments = _tabs.CurrentTab == 2 ? LineageSegments(branch, currentGeneration) : new List<SegmentRow>();
            historyBytes = _session.History.Bytes;
            limit = _session.History.MemoryLimit;
            snapshots = _session.History.SnapshotCount;
            current = _session.SegmentAt(branch, Math.Max(currentGeneration, 0));
        }

        _historyInfo.Text = $"{branch}: {_entries.Count} generations. History of all branches: {snapshots} full populations kept, " +
                            $"{historyBytes / 1048576.0:0.#} MB of {limit / 1048576.0:0} MB (older ones are thinned when full).";
        _scroll.MaxValue = _entries.Count;
        if (branchChanged) _scroll.Value = 0;
        else if (_scroll.Value > 0 && _entries.Count > before) _scroll.Value += _entries.Count - before; // scrolled down: keep the same generations in view
        ShowRows();
        // Only the tab that is shown is rebuilt (a tab change forces the next refresh), so training at speed does not churn the others.
        if (_tabs.CurrentTab == 1) ShowBranches(branches, branch);
        else if (_tabs.CurrentTab == 2) ShowSegments(segments, current);
        if (force || branchChanged) _timeLimit.SetValueNoSignal(((RacingSettings)current.Config.Mode).TimeLimitSeconds);
    }

    /// <summary>The mouse is over the list of the open tab (history rows, branches, segments).</summary>
    private bool MouseOverList()
    {
        Control list = _tabs.CurrentTab switch
        {
            0 => _rowsBox,
            1 => (Control)_branchesBox.GetParent(),
            _ => (Control)_segmentsBox.GetParent(),
        };
        return list.IsVisibleInTree() && list.GetGlobalRect().HasPoint(list.GetGlobalMousePosition());
    }

    private readonly record struct SegmentRow(SegmentEntry Segment, int From);

    /// <summary>The segments the lineage runs through, oldest first, each with the first generation it covers in the lineage.</summary>
    private List<SegmentRow> LineageSegments(string branch, int currentGeneration)
    {
        var rows = new List<SegmentRow>();
        var seen = new HashSet<int>();
        foreach (var part in _session.Lineage(branch))
        {
            int last = part.Branch == branch ? Math.Max(currentGeneration, part.From) : part.To - 1;
            var starts = new SortedSet<int> { part.From };
            foreach (var s in _session.Segments)
                if (s.Branch == part.Branch && s.FirstGeneration > part.From && s.FirstGeneration <= last) starts.Add(s.FirstGeneration);
            foreach (int g in starts)
            {
                if (g > last && part.Branch != branch) continue;
                var segment = _session.SegmentAt(branch, g);
                if (seen.Add(segment.Id)) rows.Add(new SegmentRow(segment, g));
            }
        }
        return rows;
    }

    /// <summary>A label that wraps instead of widening the panel.</summary>
    private static Label Wrapping(string text, int size, Color color)
    {
        var label = UiTheme.Label(text, size, color);
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        label.CustomMinimumSize = new Vector2(40, 0);
        return label;
    }

    private void ShowBranches(List<BranchEntry> branches, string active)
    {
        foreach (Node child in _branchesBox.GetChildren()) child.QueueFree();
        var depth = new Dictionary<string, int>();
        foreach (var b in branches)
        {
            int d = b.Parent is not null && depth.TryGetValue(b.Parent, out int pd) ? pd + 1 : 0;
            depth[b.Name] = d;
            int end = _session.HistoryEnd(b.Name);
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 8);
            row.AddChild(new Control { CustomMinimumSize = new Vector2(18 * d, 0) });
            var text = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            text.AddThemeConstantOverride("separation", 0);
            bool isActive = b.Name == active;
            text.AddChild(Wrapping((isActive ? "● " : "") + b.Name + (isActive ? "  (active)" : ""), 15, isActive ? UiTheme.Accent : UiTheme.Text));
            string origin = b.Parent is null ? "the first branch"
                : $"from {b.Parent} at generation {b.FirstGeneration} · {(b.Kind == ForkKind.BestOnly ? "best car only" : "whole population")}";
            string range = end > b.FirstGeneration ? $"generations {b.FirstGeneration}–{end - 1}" : "no generations yet";
            text.AddChild(Wrapping($"{origin} · {range}", 13, UiTheme.Muted));
            row.AddChild(text);
            var name = b.Name;
            var sw = UiTheme.SmallButton("Switch", () => SwitchRequested?.Invoke(name), 13);
            sw.Disabled = isActive || _readOnly;
            sw.TooltipText = isActive ? "This branch is active" : $"Make {name} active: it continues from its last generation";
            row.AddChild(sw);
            _branchesBox.AddChild(row);
        }
    }

    private void ShowSegments(List<SegmentRow> segments, SegmentEntry current)
    {
        foreach (Node child in _segmentsBox.GetChildren()) child.QueueFree();
        string? previousHash = null;
        foreach (var (segment, from) in segments)
        {
            var s = (RacingSettings)segment.Config.Mode;
            bool isCurrent = segment.Id == current.Id;
            // A fork starts its own segment entry; with the same settings it goes on with the same run (no divider).
            bool sameRun = segment.Config.Hash == previousHash;
            previousHash = segment.Config.Hash;
            if (sameRun)
            {
                // A fork starts its own segment entry, but with the same settings it is not a new segment for the user: a note, not a block.
                _segmentsBox.AddChild(Wrapping($"{(isCurrent ? "● " : "↳ ")}segment {segment.Id}: {segment.Branch} starts at generation {from}: same settings, the run goes on", 13,
                    isCurrent ? UiTheme.Accent : UiTheme.Muted));
                continue;
            }
            if (_segmentsBox.GetChildCount() > 0) _segmentsBox.AddChild(new HSeparator());
            var box = new VBoxContainer();
            box.AddThemeConstantOverride("separation", 0);
            box.AddChild(Wrapping($"{(isCurrent ? "● " : "")}Segment {segment.Id} · from generation {from} ({segment.Branch})", 15, isCurrent ? UiTheme.Accent : UiTheme.Text));
            string limit = (segment.Config.TimeLimitTicks / (double)RacingSettings.TicksPerSecond).ToString("0.0", Inv) + " s" + (s.TimeLimitSeconds <= 0 ? " (auto)" : "");
            box.AddChild(Wrapping($"time limit {limit} · walls {s.Walls.ToString().ToLowerInvariant()} · max speed {s.MaxSpeed:0.#} · {segment.PopulationSize} cars", 13, UiTheme.Muted));
            box.AddChild(Wrapping($"track {segment.Track.Name} · config {segment.Config.Hash[..12]}", 12, UiTheme.Muted.Darkened(0.2f)));
            _segmentsBox.AddChild(box);
        }
    }
}
