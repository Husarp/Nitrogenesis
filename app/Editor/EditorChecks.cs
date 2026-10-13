using System.Diagnostics;
using Godot;
using Nitrogenesis.Sim.Map;
using Nitrogenesis.Sim.Racing;

namespace Nitrogenesis.Editor;

/// <summary>
/// The editor's live checks (PLAN §3.2, §6.3): watches the document, asks an <see cref="EditorAnalysisRunner"/> for a
/// fresh analysis after every change (debounced 150 ms, off the main thread) and shows the newest result in the status
/// bar and as overlays on the map, but only if it is for the document's current revision. Nothing runs per frame
/// unless the worker has a result ready.
/// </summary>
public partial class EditorChecks : Node
{
    private readonly EditorView _view;
    private EditorAnalysisRunner _runner = null!;
    private EditorStatusBar _bar = null!;
    private AnalysisOverlay _overlay = null!;
    private EditDocument _doc = null!;
    private long _editTicks;
    private long _appliedRevision = -1;
    private bool _ready;

    public EditorChecks(EditorView view)
    {
        _view = view;
        Name = "EditorChecks";
    }

    /// <summary>The result on screen, if any (debug and tests).</summary>
    public EditorAnalysisResult? Shown { get; private set; }

    public override void _Ready()
    {
        _bar = new EditorStatusBar();
        foreach (var child in _view.StatusBarSlot.GetChildren())
        {
            _view.StatusBarSlot.RemoveChild(child); // the empty placeholder
            child.QueueFree();
        }
        _view.StatusBarSlot.AddChild(_bar);
        _overlay = new AnalysisOverlay();
        _view.MapLayer.AddChild(_overlay);
        _ready = true;
        _view.DocumentReplaced -= OnDocumentReplaced;
        _view.DocumentReplaced += OnDocumentReplaced;
        Attach(_view.Document);
    }

    /// <summary>Back in the tree after Drive (the view was parked, not freed): start over with a new runner and a fresh analysis.</summary>
    public override void _EnterTree()
    {
        if (!_ready) return; // the first time _Ready does this
        _view.DocumentReplaced -= OnDocumentReplaced;
        _view.DocumentReplaced += OnDocumentReplaced;
        Attach(_view.Document);
    }

    /// <summary>Leaving the tree (freed, or parked while driving): stop the worker thread and unhook; _EnterTree starts again.</summary>
    public override void _ExitTree()
    {
        _view.DocumentReplaced -= OnDocumentReplaced;
        if (_doc is not null) _doc.Changed -= OnChanged;
        _runner?.Dispose();
        _runner = null!;
    }

    /// <summary>Shows the Test button's reference time in the status bar (null = the live estimate again).</summary>
    public void SetReferenceTime(float? seconds) => _bar.SetReferenceTime(seconds);

    private void Attach(EditDocument doc)
    {
        if (_doc is not null) _doc.Changed -= OnChanged;
        _doc = doc;
        _doc.Changed += OnChanged;
        _runner?.Dispose(); // a result of the old document must never reach the new one (revisions restart at 0)
        _runner = new EditorAnalysisRunner(new RacingSettings());
        _appliedRevision = -1;
        Shown = null;
        _bar.Clear();
        _overlay.SetPending(true);
        _editTicks = Stopwatch.GetTimestamp();
        _runner.Request(_doc.Snapshot(), _doc.Revision);
    }

    private void OnDocumentReplaced() => Attach(_view.Document);

    private void OnChanged(EditResult _)
    {
        if (_runner is null) return;
        // Ask at once, so the 150 ms debounce starts at the edit, not at the next frame. A snapshot is one small array
        // copy (36 KB on an M map), so even a stroke's many small edits cost next to nothing; the runner keeps only the newest.
        _editTicks = Stopwatch.GetTimestamp();
        _runner.Request(_doc.Snapshot(), _doc.Revision);
    }

    public override void _Process(double delta)
    {
        if (_runner is null) return;
        if (_runner.TryTakeResult(out var result) && result.Revision == _doc.Revision)
        {
            Shown = result;
            _appliedRevision = result.Revision;
            _bar.Show(result);
            _overlay.Show(result);
            _overlay.SetPending(false);
            double ms = Stopwatch.GetElapsedTime(_editTicks).TotalMilliseconds;
            double ready = Stopwatch.GetElapsedTime(_editTicks, _runner.LatestTimestamp).TotalMilliseconds;
            GD.Print(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"editor-check: {result.Summary} rev {result.Revision} applied {ms:0} ms after the edit (result ready after {ready:0} ms, frame wait {ms - ready:0} ms)"));
        }
        if (_appliedRevision != _doc.Revision)
        {
            _bar.SetPending(true);
            _overlay.SetPending(true);
        }
    }
}
