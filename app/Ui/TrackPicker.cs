using System;
using System.Collections.Generic;
using Godot;
using Nitrogenesis.Sim.Io;
using Nitrogenesis.Editor;
using Nitrogenesis.Sim.Map;
using Nitrogenesis.Sim.Racing;
using Nitrogenesis.View;

namespace Nitrogenesis.Ui;

/// <summary>Pick a track: the bundled ones first, then the user's own saved ones (list + preview). Start with the button, Enter or a double click; Esc goes back.</summary>
public partial class TrackPicker : Control
{
    private string _heading = "";
    private List<Track?> _tracks = new(); // parallel to the list rows; null = a heading or a damaged file (not selectable)
    private ItemList _list = null!;
    private TextureRect _preview = null!;
    private Label _info = null!;
    private Label _warn = null!;
    private Button _start = null!;
    private readonly Dictionary<Track, string?> _blocked = new(); // per track: why it is not finishable (null = fine), checked once

    public event Action<Track>? Picked;
    public event Action? Back;

    public static TrackPicker Create(string heading) => new() { _heading = heading };

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
        column.AddChild(UiTheme.Label(_heading.ToUpperInvariant(), 32, UiTheme.Accent));
        column.AddChild(UiTheme.Label("Pick a track", 16, UiTheme.Muted));

        var row = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 24);
        column.AddChild(row);

        _list = new ItemList { CustomMinimumSize = new Vector2(280, 0), SizeFlagsVertical = SizeFlags.ExpandFill };
        row.AddChild(_list);
        var right = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        row.AddChild(right);
        _preview = new TextureRect
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            TextureFilter = TextureFilterEnum.Nearest,
        };
        right.AddChild(_preview);
        _info = UiTheme.Label("", 16, UiTheme.Muted);
        right.AddChild(_info);
        _warn = UiTheme.Label("", 16, UiTheme.Bad);
        _warn.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        right.AddChild(_warn);

        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 12);
        column.AddChild(buttons);
        var back = new Button { Text = "Back", CustomMinimumSize = new Vector2(140, 44) };
        back.Pressed += () => Back?.Invoke();
        buttons.AddChild(back);
        var start = new Button { Text = "Start", CustomMinimumSize = new Vector2(180, 44) };
        start.Pressed += StartSelected;
        _start = start;
        buttons.AddChild(start);

        var bundled = BundledTracks.LoadAll();
        foreach (var t in bundled)
        {
            _list.AddItem(t.Name);
            _tracks.Add(t);
        }
        var users = TrackLibrary.Default().List();
        if (users.Count > 0)
        {
            int h = _list.AddItem("YOUR TRACKS", null, false);
            _list.SetItemCustomFgColor(h, UiTheme.Accent);
            _tracks.Add(null);
            foreach (var u in users)
            {
                if (u.CanOpen)
                {
                    try
                    {
                        var track = TrackLibrary.Load(u.Path);
                        _list.AddItem(u.Name + "  · your track");
                        _tracks.Add(track);
                        continue;
                    }
                    catch (System.Exception e) when (e is System.IO.InvalidDataException or System.IO.IOException or System.UnauthorizedAccessException)
                    {
                        GD.PushError($"Track {u.Path}: {e.Message}");
                    }
                }
                int d = _list.AddItem(u.Name + "  · damaged", null, false);
                _list.SetItemCustomFgColor(d, UiTheme.Muted);
                _list.SetItemTooltip(d, $"{EditorFileDialogs.DamagedReason}\n{u.Problem}");
                GD.PushWarning($"Track {u.Path}: {u.Problem}");
                _tracks.Add(null);
            }
        }
        _list.ItemSelected += Show;
        _list.ItemActivated += _ => StartSelected();
        int first = _tracks.FindIndex(t => t is not null);
        if (first >= 0)
        {
            _list.Select(first);
            Show(first);
        }
        else
        {
            _info.Text = "No tracks found.";
            start.Disabled = true;
        }
        _list.CallDeferred(Control.MethodName.GrabFocus);
    }

    private void Show(long index)
    {
        var t = _tracks[(int)index];
        if (t is null) return;
        _preview.Texture = MapTexture.Create(t.Grid, t.Start);
        _info.Text = $"{t.Name} · {t.Grid.Width} × {t.Grid.Height} cells";
        string? why = NotFinishable(t);
        _warn.Text = why is null ? "" : $"Not finishable: {why} - fix it in the Track editor";
        _start.Disabled = why is not null;
    }

    private string? NotFinishable(Track t)
    {
        if (_blocked.TryGetValue(t, out var cached)) return cached;
        string? why = Solvability.Check(t, new RacingSettings().GrassCost) switch
        {
            SolvabilityResult.Solvable => null,
            SolvabilityResult.NoFinish => "no finish zone",
            SolvabilityResult.StartOutside => "the start is outside the map",
            SolvabilityResult.StartNotPassable => "the start is in a wall or too narrow",
            SolvabilityResult.StartOverlapsWall => "the car at the start touches a wall or danger",
            _ => "the finish cannot be reached from the start",
        };
        _blocked[t] = why;
        return why;
    }

    /// <summary>Debug: selects the row called <paramref name="name"/> (ignoring case; user tracks too). False when there is none.</summary>
    public bool DebugPick(string name)
    {
        int i = _tracks.FindIndex(t => t is not null && string.Equals(t.Name, name, System.StringComparison.OrdinalIgnoreCase));
        if (i < 0) return false;
        _list.Select(i);
        Show(i);
        return true;
    }

    /// <summary>Debug: the Start button.</summary>
    public void DebugStart() => StartSelected();

    private void StartSelected()
    {
        int[] selected = _list.GetSelectedItems();
        if (selected.Length > 0 && _tracks[selected[0]] is { } track)
        {
            if (NotFinishable(track) is { } why)
            {
                GD.Print($"picker: '{track.Name}' not started - not finishable: {why}");
                return;
            }
            Picked?.Invoke(track);
        }
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Keycode: Key.Escape })
        {
            Back?.Invoke();
            GetViewport().SetInputAsHandled();
        }
    }
}
