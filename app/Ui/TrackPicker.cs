using System;
using System.Collections.Generic;
using Godot;
using Nitrogenesis.Sim.Map;
using Nitrogenesis.View;

namespace Nitrogenesis.Ui;

/// <summary>Pick a bundled track (list + preview). Start with the button, Enter or a double click; Esc goes back.</summary>
public partial class TrackPicker : Control
{
    private string _heading = "";
    private List<Track> _tracks = new();
    private ItemList _list = null!;
    private TextureRect _preview = null!;
    private Label _info = null!;

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

        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 12);
        column.AddChild(buttons);
        var back = new Button { Text = "Back", CustomMinimumSize = new Vector2(140, 44) };
        back.Pressed += () => Back?.Invoke();
        buttons.AddChild(back);
        var start = new Button { Text = "Start", CustomMinimumSize = new Vector2(180, 44) };
        start.Pressed += StartSelected;
        buttons.AddChild(start);

        _tracks = BundledTracks.LoadAll();
        foreach (var t in _tracks) _list.AddItem(t.Name);
        _list.ItemSelected += Show;
        _list.ItemActivated += _ => StartSelected();
        if (_tracks.Count > 0)
        {
            _list.Select(0);
            Show(0);
        }
        else
        {
            _info.Text = "No bundled tracks found.";
            start.Disabled = true;
        }
        _list.CallDeferred(Control.MethodName.GrabFocus);
    }

    private void Show(long index)
    {
        var t = _tracks[(int)index];
        _preview.Texture = MapTexture.Create(t.Grid, t.Start);
        _info.Text = $"{t.Name} · {t.Grid.Width} × {t.Grid.Height} cells";
    }

    private void StartSelected()
    {
        int[] selected = _list.GetSelectedItems();
        if (selected.Length > 0) Picked?.Invoke(_tracks[selected[0]]);
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
