using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using Nitrogenesis.Sim.Map;
using Nitrogenesis.Sim.Racing;
using Nitrogenesis.View;

namespace Nitrogenesis.Ui;

/// <summary>
/// Race yourself, step 2 (PLAN §6.5): the ghosts of this track, from every saved session and segment with the track's
/// hash, grouped by session with the newest generation first. Several can be chosen (click each) or none. Shows the
/// physics warning when the choice mixes physics. Start with the button (the list keeps the keyboard); Esc goes back.
/// </summary>
public partial class GhostPicker : Control
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private Track _track = null!;
    private List<GhostEntry> _ghosts = null!;
    private readonly List<GhostEntry?> _rows = new(); // parallel to the list rows; null = a session heading (not selectable)
    private ItemList _list = null!;
    private Label _info = null!, _warn = null!;
    private Button _start = null!;

    /// <summary>The chosen ghosts, in list order (the first one's physics is used), possibly none.</summary>
    public event Action<IReadOnlyList<GhostEntry>>? Picked;
    public event Action? Back;

    public static GhostPicker Create(Track track, List<GhostEntry> ghosts) => new() { _track = track, _ghosts = ghosts };

    public override void _Ready()
    {
        Theme = UiTheme.Theme;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var bg = new ColorRect { Color = UiTheme.Background };
        bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(bg);

        var margin = new MarginContainer();
        margin.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        foreach (string side in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride($"margin_{side}", 40);
        AddChild(margin);
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 16);
        margin.AddChild(column);
        column.AddChild(UiTheme.Label("RACE YOURSELF · " + _track.Name.ToUpperInvariant(), 32, UiTheme.Accent));
        column.AddChild(UiTheme.Label(_ghosts.Count == 0
            ? "No saved training on this track yet: you drive alone."
            : $"Pick ghosts to race: the best car of a saved generation. Click each one you want (up to {Race.MaxGhosts}), or none.", 16, UiTheme.Muted));

        var row = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 24);
        column.AddChild(row);
        _list = new ItemList { CustomMinimumSize = new Vector2(420, 0), SizeFlagsVertical = SizeFlags.ExpandFill, SelectMode = ItemList.SelectModeEnum.Toggle };
        row.AddChild(_list);
        var right = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        row.AddChild(right);
        right.AddChild(new TextureRect
        {
            Texture = MapTexture.Create(_track.Grid, _track.Start),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            TextureFilter = TextureFilterEnum.Nearest,
        });
        _info = UiTheme.Label("", 16, UiTheme.Muted);
        _info.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        right.AddChild(_info);
        _warn = UiTheme.Label("", 16, UiTheme.Accent);
        _warn.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        right.AddChild(_warn);

        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 12);
        column.AddChild(buttons);
        var back = new Button { Text = "Back", CustomMinimumSize = new Vector2(140, 44) };
        back.Pressed += () => Back?.Invoke();
        buttons.AddChild(back);
        _start = new Button { Text = "Race", CustomMinimumSize = new Vector2(180, 44) };
        _start.Pressed += Start;
        buttons.AddChild(_start);

        string? session = null, branch = null;
        foreach (var g in _ghosts)
        {
            if (g.SessionName != session)
            {
                session = g.SessionName;
                branch = null;
                int h = _list.AddItem(session.ToUpperInvariant(), null, false);
                _list.SetItemCustomFgColor(h, UiTheme.Accent);
                _rows.Add(null);
            }
            bool firstOfBranch = g.Branch != branch; // the list is newest generation first, so the first row of a branch is its latest
            branch = g.Branch;
            string where = g.Branch == "main" ? "" : $"[{g.Branch}] ";
            string result = g.Finished ? g.FinishSeconds.ToString("0.00", Inv) + " s" : "no finish";
            int i = _list.AddItem($"{where}gen {g.Generation} · {result}{(firstOfBranch ? "   (latest)" : "")}");
            _list.SetItemTooltip(i, Physics(g));
            _rows.Add(g);
        }
        _list.MultiSelected += (_, _) => Refresh();
        _list.ItemSelected += _ => Refresh();
        Refresh();
        _list.CallDeferred(Control.MethodName.GrabFocus);
    }

    private static string Physics(GhostEntry g)
    {
        var p = g.Physics;
        return string.Create(Inv, $"Physics: top speed {p.MaxSpeed:0.#} · acceleration {p.Acceleration:0.#} · turn rate {p.TurnRateDeg:0}°/s · grass {p.GrassSpeedFraction * 100:0} % / grip {p.GrassGrip:0.##} · walls {p.Walls} · sim {g.SimVersion}");
    }

    private List<GhostEntry> Chosen() =>
        _list.GetSelectedItems().OrderBy(i => i).Select(i => _rows[i]).OfType<GhostEntry>().ToList();

    private void Refresh()
    {
        var chosen = Chosen();
        var physics = GhostPhysics.Choose(chosen);
        bool tooMany = chosen.Count > Race.MaxGhosts;
        _info.Text = chosen.Count == 0 ? "No ghosts chosen: you drive alone with the standard physics."
            : $"{chosen.Count} ghost{(chosen.Count == 1 ? "" : "s")} chosen.";
        _warn.Text = tooMany ? $"At most {Race.MaxGhosts} ghosts at once." : physics.Warning ?? "";
        _start.Disabled = tooMany;
        _start.Text = chosen.Count == 0 ? "Race alone" : "Race";
    }

    private void Start()
    {
        var chosen = Chosen();
        if (chosen.Count <= Race.MaxGhosts) Picked?.Invoke(chosen);
    }

    /// <summary>Debug: chooses ghosts by index into the ghost list: "all" (the first eight), "none" or "0,2,5".</summary>
    public void DebugSelect(string what)
    {
        _list.DeselectAll();
        var wanted = what.Equals("none", StringComparison.OrdinalIgnoreCase) ? new List<int>()
            : what.Equals("all", StringComparison.OrdinalIgnoreCase) ? Enumerable.Range(0, Math.Min(Race.MaxGhosts, _ghosts.Count)).ToList()
            : what.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => int.Parse(s, Inv)).ToList();
        foreach (int g in wanted)
            if (g >= 0 && g < _ghosts.Count) _list.Select(_rows.IndexOf(_ghosts[g]), false);
        Refresh();
    }

    /// <summary>Debug: a real left click (press and release) on the row of ghost <paramref name="ghost"/>.</summary>
    public void DebugClick(int ghost)
    {
        var rect = _list.GetItemRect(_rows.IndexOf(_ghosts[ghost]));
        var at = _list.GetGlobalRect().Position + rect.GetCenter();
        foreach (bool pressed in new[] { true, false })
            Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = pressed, Position = at, GlobalPosition = at });
    }

    /// <summary>Debug: the Race button.</summary>
    public void DebugStart() => Start();

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Keycode: Key.Escape })
        {
            Back?.Invoke();
            GetViewport().SetInputAsHandled();
        }
    }
}
