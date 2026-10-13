using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Nitrogenesis.Sim.Gen;
using Nitrogenesis.Sim.Map;
using Nitrogenesis.Ui;

namespace Nitrogenesis.Editor;

/// <summary>
/// The editor's Generate and Similar buttons (PLAN §6.3, §7). Generate opens a box to pick a template, change its parameters and
/// the seed; Similar takes the template and parameters from the open track's meta and a new seed. The generator runs off the
/// main thread behind a progress box with Cancel (it tries the next seeds when one fails its checks, up to 50); the valid track
/// opens as a new document without a file, free to edit, and the reference driver's Test runs on it at once.
/// </summary>
public sealed class EditorGenerator
{
    private sealed class Job
    {
        public Task<GenResult> Task = null!;
        public readonly CancellationTokenSource Cancel = new();
        public Modal Box = null!;
        public Label Text = null!;
        public volatile int Attempt;
        public long Seed;
        public bool Closed;
    }

    private readonly EditorView _view;
    private Job? _job;
    private TrackTemplate _template = TrackTemplate.Sprint; // the box starts with the last choice
    private GeneratorParams _params = GeneratorParams.Defaults(TrackTemplate.Sprint);

    public EditorGenerator(EditorView view) => _view = view;

    /// <summary>True while a track is being generated (the debug script waits).</summary>
    public bool Running => _job is not null;

    /// <summary>The template the open track was generated with (from its meta), or null: then Similar is greyed out.</summary>
    public TrackTemplate? CurrentTemplate => Templates.FromMeta(_view.Document.Meta);

    public void AskGenerate() => _view.Files.Guard(ShowBox);

    public void AskSimilar()
    {
        if (CurrentTemplate is not { } template || Running) return;
        var p = GeneratorParams.FromJson(template, _view.Document.Meta.Params);
        _view.Files.Guard(() => Start(template, p, NewSeed()));
    }

    /// <summary>Debug: generate with the template's defaults on a map of <paramref name="size"/> (after the unsaved-changes question), from <paramref name="seed"/> or a random one.</summary>
    public void DebugGenerate(TrackTemplate template, ulong? seed, MapSize size) =>
        _view.Files.Guard(() => Start(template, GeneratorParams.Defaults(template) with { Size = size }, seed ?? NewSeed()));

    private static ulong NewSeed() => (ulong)Random.Shared.Next(1, int.MaxValue);

    private void ShowBox()
    {
        if (Modal.OpenCount > 0 || Running) return;
        if (CurrentTemplate is { } current)
        {
            _template = current;
            _params = GeneratorParams.FromJson(current, _view.Document.Meta.Params);
        }
        var body = new VBoxContainer();
        body.AddThemeConstantOverride("separation", 10);

        var templates = new GridContainer { Columns = 3 };
        templates.AddThemeConstantOverride("h_separation", 6);
        templates.AddThemeConstantOverride("v_separation", 6);
        body.AddChild(templates);
        var description = UiTheme.Label("", 15, UiTheme.Muted);
        body.AddChild(description);
        var sizes = new HBoxContainer();
        sizes.AddThemeConstantOverride("separation", 6);
        body.AddChild(sizes);
        var fields = new GridContainer { Columns = 2 };
        fields.AddThemeConstantOverride("h_separation", 12);
        fields.AddThemeConstantOverride("v_separation", 6);
        body.AddChild(fields);

        var seedRow = new HBoxContainer();
        seedRow.AddThemeConstantOverride("separation", 8);
        var seed = new SpinBox { MinValue = 0, MaxValue = int.MaxValue, Step = 1, Value = NewSeed(), CustomMinimumSize = new Vector2(150, 0), TooltipText = "The same template, parameters and seed always make the same track" };
        seedRow.AddChild(seed);
        seedRow.AddChild(UiTheme.SmallButton("New seed", () => seed.Value = NewSeed()));

        var templateGroup = new ButtonGroup();
        foreach (var t in Templates.All)
        {
            var template = t;
            var b = new Button { Text = Templates.Name(t), ToggleMode = true, ButtonGroup = templateGroup, ButtonPressed = t == _template, TooltipText = Templates.Description(t), CustomMinimumSize = new Vector2(152, 36) };
            b.AddThemeFontSizeOverride("font_size", 16);
            b.Pressed += () =>
            {
                _template = template;
                _params = GeneratorParams.Defaults(template) with { Size = _params.Size };
                Fill();
            };
            templates.AddChild(b);
        }

        sizes.AddChild(Caption("Map size"));
        var sizeGroup = new ButtonGroup();
        foreach (var s in new[] { MapSize.S, MapSize.M, MapSize.L })
        {
            var size = s;
            var (w, h) = EditDocument.Dimensions(s);
            var b = new Button { Text = string.Create(CultureInfo.InvariantCulture, $"{s}  {w}×{h}"), ToggleMode = true, ButtonGroup = sizeGroup, ButtonPressed = s == _params.Size };
            b.AddThemeFontSizeOverride("font_size", 15);
            b.Pressed += () => _params = _params with { Size = size };
            sizes.AddChild(b);
        }

        void Fill()
        {
            description.Text = Templates.Description(_template);
            foreach (var child in fields.GetChildren())
            {
                fields.RemoveChild(child);
                if (child != seedRow) child.QueueFree();
            }
            foreach (var info in GeneratorParams.For(_template))
            {
                string key = info.Key;
                fields.AddChild(Caption(info.Label, info.Tip));
                var spin = new SpinBox { MinValue = info.Min, MaxValue = info.Max, Step = 1, Value = _params.Get(key), CustomMinimumSize = new Vector2(150, 0), TooltipText = info.Tip, Suffix = key.EndsWith("Seconds", StringComparison.Ordinal) ? "s" : "" };
                spin.ValueChanged += v => _params = _params.With(key, (int)v);
                fields.AddChild(spin);
            }
            fields.AddChild(Caption("Seed"));
            fields.AddChild(seedRow);
        }
        Fill();
        var note = Modal.Text("A track must be finishable, have no grass shortcut and take the reference driver between the shortest and the " +
                              "longest time. If one fails, the next seeds are tried (up to 50).", UiTheme.Muted);
        note.AddThemeFontSizeOverride("font_size", 14);
        body.AddChild(note);

        Modal.Open(_view, "Generate track", body,
            new Modal.Choice("Cancel"),
            new Modal.Choice("Generate", () => Start(_template, _params, (ulong)seed.Value), Primary: true));
    }

    private static Label Caption(string text, string? tip = null)
    {
        var label = UiTheme.Label(text, 16, UiTheme.Text);
        label.CustomMinimumSize = new Vector2(130, 0);
        if (tip is not null)
        {
            label.TooltipText = tip;
            label.MouseFilter = Control.MouseFilterEnum.Pass;
        }
        return label;
    }

    private void Start(TrackTemplate template, GeneratorParams p, ulong seed)
    {
        if (Running) return;
        _template = template;
        _params = p;
        var job = new Job { Seed = (long)seed };
        job.Text = Modal.Text($"{Templates.Name(template)}, seed {seed}…");
        job.Box = Modal.Open(_view, "Generating track", job.Text, new Modal.Choice("Cancel"));
        job.Box.Closed += () =>
        {
            job.Closed = true;
            job.Cancel.Cancel();
        };
        job.Task = Task.Run(() => TrackGenerator.GenerateValid(template, p, seed,
            progress: (attempt, s) =>
            {
                job.Attempt = attempt;
                Interlocked.Exchange(ref job.Seed, (long)s);
            }, ct: job.Cancel.Token));
        _job = job;
        GD.Print($"editor-generate: {template} from seed {seed}");
    }

    /// <summary>Once per frame: shows the progress, and when the job is done opens its track (or says why there is none).</summary>
    public void Poll()
    {
        if (_job is not { } job) return;
        if (!job.Task.IsCompleted)
        {
            if (!job.Closed && job.Attempt > 1)
                job.Text.Text = string.Create(CultureInfo.InvariantCulture,
                    $"{Templates.Name(_template)}: trying seed {Interlocked.Read(ref job.Seed)} ({job.Attempt} of {TrackGenerator.MaxAttempts})…");
            return;
        }
        _job = null;
        bool cancelled = job.Closed; // Cancel or Esc
        if (!cancelled) job.Box.Close();
        job.Cancel.Dispose();
        if (cancelled || job.Task.IsCanceled)
        {
            GD.Print("editor-generate: cancelled");
            return;
        }
        if (job.Task.Exception is { } e)
        {
            GD.PushError($"Generating a track failed: {e.GetBaseException()}");
            Modal.Message(_view, "Could not generate a track", e.GetBaseException().Message);
            return;
        }
        var result = job.Task.Result;
        GD.Print($"editor-generate: {result.Summary}");
        if (result.Track is not { } track)
        {
            Modal.Message(_view, "Could not generate a track", result.Summary + "\n\n" + result.Hint);
            return;
        }
        _view.ReplaceFile(new EditDocument(track), null);
        _view.Test(); // shows the reference driver's time and route, as the generator checked it
    }
}
