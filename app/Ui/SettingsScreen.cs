using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using Nitrogenesis.Sim.Settings;

namespace Nitrogenesis.Ui;

/// <summary>
/// The Settings screen (PLAN §6.4). Every setting has a tooltip, a "reset to default" button and a kind badge: <b>live</b>
/// applies now, <b>new segment</b> asks first, <b>new training</b> is greyed out while a training is open. Presets Easy /
/// Default / Chaos set the training settings at once.
/// </summary>
/// <remarks>
/// Opened from the main menu there is no training: the training settings are the defaults of the next new training, everything
/// is saved at once. Opened over a running training (<see cref="Create"/> with <c>training</c>) the training settings show that
/// training's own values: live ones go to <c>liveChanged</c> at once, new-segment ones wait until "Start new segment…" has
/// been confirmed (then <c>startSegment</c> gets them), new-training ones are greyed out. App settings (graphics,
/// performance, sound) always apply and save at once.
/// </remarks>
public partial class SettingsScreen : Control
{
    private static readonly string[] Tabs = ["Training", "Graphics", "Performance", "Sound"];

    private sealed class Row
    {
        public SettingDef Def = null!;
        public Control Row_ = null!;
        public Control Input = null!;
        public Label Value = null!;
        public Button Reset = null!;
        public bool Locked;
    }

    private AppSettings? _training;
    private Action<AppSettings, string>? _liveChanged;
    private Action<AppSettings>? _startSegment;
    private readonly Dictionary<string, Row> _rows = new();
    private readonly HashSet<string> _pending = new();
    private readonly Dictionary<string, double> _opened = new(); // the new-segment values the training runs with now
    private TabContainer _tabs = null!;
    private Label _fpsHint = null!, _pendingNote = null!;
    private Button _startSegmentButton = null!;
    private bool _refreshing;

    /// <summary>Raised when the user leaves the screen (Back or Esc).</summary>
    public event Action? Back;

    /// <param name="training">A copy of the running training's values (<see cref="AppSettings.ForTraining"/>), or null from the menu.</param>
    /// <param name="liveChanged">A live training setting changed (the copy, the key).</param>
    /// <param name="startSegment">The user confirmed a new segment with the copy's values.</param>
    public static SettingsScreen Create(AppSettings? training = null, Action<AppSettings, string>? liveChanged = null, Action<AppSettings>? startSegment = null) =>
        new() { _training = training, _liveChanged = liveChanged, _startSegment = startSegment };

    private bool InTraining => _training is not null;

    private AppSettings Source(SettingDef def) => def.Scope == SettingScope.App ? AppRuntime.Settings : _training ?? AppRuntime.Settings;

    public override void _Ready()
    {
        Theme = UiTheme.Theme;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        RememberOpened();
        var bg = new ColorRect { Color = UiTheme.Background };
        bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(bg);

        var margin = new MarginContainer();
        margin.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        foreach (string side in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride("margin_" + side, 28);
        AddChild(margin);
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 10);
        margin.AddChild(column);

        column.AddChild(UiTheme.Label("SETTINGS", 34, UiTheme.Accent));
        column.AddChild(UiTheme.Label(InTraining
            ? "A training is open. Live settings apply now. New-segment settings wait for \"Start new segment…\". Greyed-out settings need a new training."
            : "Training settings here are the defaults of your next new training. Everything is saved as you change it.", 15, UiTheme.Muted));

        var tabs = _tabs = new TabContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        column.AddChild(tabs);
        var defs = AppRuntime.Settings.Defs;
        foreach (string tab in Tabs)
        {
            var page = new VBoxContainer { Name = tab };
            page.AddThemeConstantOverride("separation", 6);
            var scroll = new ScrollContainer { Name = tab, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
            scroll.AddChild(page);
            page.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            tabs.AddChild(scroll);
            foreach (string section in SectionsOf(tab))
            {
                page.AddChild(UiTheme.Label(section.ToUpperInvariant(), 15, UiTheme.Accent));
                foreach (var def in defs.Where(d => d.Section == section)) page.AddChild(MakeRow(def));
                if (section == SettingsCatalog.Graphics)
                {
                    _fpsHint = UiTheme.Label("", 14, UiTheme.Accent);
                    _fpsHint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                    page.AddChild(_fpsHint);
                }
                if (section == SettingsCatalog.Performance)
                {
                    var m = AppRuntime.Machine;
                    page.AddChild(UiTheme.Label($"This computer: {m.PhysicalCores} cores, {m.LogicalCores} threads, {m.TotalMemoryBytes / (1024.0 * 1024 * 1024):0.#} GB of memory. " +
                        "The history only grows with the training; a bigger limit does not make training faster. A priority change needs Windows.", 14, UiTheme.Muted));
                }
                page.AddChild(new Control { CustomMinimumSize = new Vector2(0, 8) });
            }
        }

        var footer = new HBoxContainer();
        footer.AddThemeConstantOverride("separation", 10);
        column.AddChild(footer);
        var back = new Button { Text = "Back  (Esc)", CustomMinimumSize = new Vector2(150, 42) };
        back.Pressed += () => Back?.Invoke();
        footer.AddChild(back);
        footer.AddChild(new Control { CustomMinimumSize = new Vector2(24, 0) });
        footer.AddChild(UiTheme.Label("Presets", 16, UiTheme.Muted));
        foreach (var preset in Enum.GetValues<SettingsPreset>())
        {
            var p = preset;
            var button = new Button { Text = preset.ToString(), CustomMinimumSize = new Vector2(96, 42) };
            button.TooltipText = p switch
            {
                SettingsPreset.Easy => "Easier learning: the direction hint, more cars and rays, gentler mutation, forgiving grass.",
                SettingsPreset.Chaos => "Wild evolution and hard rules: strong mutation, many new random cars, walls that kill, slippery grass.",
                _ => "Every training setting back to its default.",
            };
            button.Pressed += () => ApplyPreset(p);
            footer.AddChild(button);
        }
        footer.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        _pendingNote = UiTheme.Label("", 14, UiTheme.Accent);
        footer.AddChild(_pendingNote);
        _startSegmentButton = new Button { Text = "Start new segment…", CustomMinimumSize = new Vector2(190, 42), Visible = InTraining, Disabled = true };
        _startSegmentButton.TooltipText = "Applies the changed new-segment settings from the next generation. The best time, the stagnation check and the graph restart; the population carries on.";
        _startSegmentButton.Pressed += AskStartSegment;
        footer.AddChild(_startSegmentButton);

        RefreshAll();
    }

    private static IEnumerable<string> SectionsOf(string tab) => tab switch
    {
        "Training" => [SettingsCatalog.Training, SettingsCatalog.Evolution, SettingsCatalog.Population, SettingsCatalog.Physics, SettingsCatalog.Sensors],
        "Graphics" => [SettingsCatalog.Graphics],
        "Performance" => [SettingsCatalog.Performance],
        _ => [SettingsCatalog.Sound],
    };

    // ---------------------------------------------------------------- rows

    private Control MakeRow(SettingDef def)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        row.MouseFilter = MouseFilterEnum.Pass;

        var label = UiTheme.Label(def.Label, 16, UiTheme.Text);
        label.CustomMinimumSize = new Vector2(250, 0);
        label.MouseFilter = MouseFilterEnum.Stop;
        label.TooltipText = def.Tooltip;
        row.AddChild(label);

        Control input = def.Type switch
        {
            SettingType.Bool => MakeCheck(def),
            SettingType.Choice => MakeChoice(def),
            _ => MakeSpin(def),
        };
        input.TooltipText = def.Tooltip;
        row.AddChild(input);

        var value = UiTheme.Label("", 15, UiTheme.Muted);
        value.CustomMinimumSize = new Vector2(90, 0);
        row.AddChild(value);

        var badge = UiTheme.Label(def.Kind switch { SettingKind.Live => "live", SettingKind.NewSegment => "new segment", _ => "new training" }, 13,
            def.Kind switch { SettingKind.Live => UiTheme.Good, SettingKind.NewSegment => UiTheme.Accent, _ => UiTheme.Muted });
        badge.CustomMinimumSize = new Vector2(100, 0);
        badge.MouseFilter = MouseFilterEnum.Stop;
        badge.TooltipText = def.Kind switch
        {
            SettingKind.Live => "Applies now (evolution settings at the next generation).",
            SettingKind.NewSegment => "Changing it starts a new segment of the training; you are asked first.",
            _ => "Only a new training can use it: greyed out while a training is open.",
        };
        row.AddChild(badge);

        var reset = new Button { Text = "Reset", FocusMode = FocusModeEnum.None, TooltipText = "Back to the default" };
        reset.AddThemeFontSizeOverride("font_size", 14);
        reset.Pressed += () => Change(def, def.Default);
        row.AddChild(reset);

        _rows[def.Key] = new Row { Def = def, Row_ = row, Input = input, Value = value, Reset = reset };
        return row;
    }

    private Control MakeCheck(SettingDef def)
    {
        var check = new CheckBox { Text = "", CustomMinimumSize = new Vector2(190, 0) };
        check.Toggled += on =>
        {
            if (!_refreshing) Change(def, on ? 1 : 0);
        };
        return check;
    }

    private Control MakeChoice(SettingDef def)
    {
        var option = new OptionButton { CustomMinimumSize = new Vector2(190, 0) };
        foreach (string choice in def.Choices!) option.AddItem(choice);
        option.ItemSelected += index =>
        {
            if (!_refreshing) Change(def, index);
        };
        return option;
    }

    private Control MakeSpin(SettingDef def)
    {
        var spin = new SpinBox
        {
            MinValue = def.ZeroMeans is not null ? 0 : def.Min,
            MaxValue = def.Max,
            Step = def.Step,
            Rounded = def.Type == SettingType.Int,
            Suffix = def.Unit,
            CustomMinimumSize = new Vector2(190, 0),
            UpdateOnTextChanged = false,
        };
        spin.ValueChanged += v =>
        {
            if (!_refreshing) Change(def, v);
        };
        return spin;
    }

    /// <summary>The user changed a control (or pressed Reset): store the value where it belongs and apply it.</summary>
    private void Change(SettingDef def, double value)
    {
        var source = Source(def);
        if (!source.Set(def.Key, value))
        {
            RefreshRow(_rows[def.Key]); // clamped back, or reset when it already was the default
            return;
        }
        Applied(source, def);
        RefreshAll();
    }

    /// <summary>Sends a changed setting where it takes effect, by scope and kind.</summary>
    private void Applied(AppSettings source, SettingDef def)
    {
        if (def.Scope == SettingScope.App) AppRuntime.NotifyChanged();
        else if (!InTraining) AppRuntime.Save();
        else if (def.Kind == SettingKind.Live) _liveChanged?.Invoke(source, def.Key);
        else if (def.Kind == SettingKind.NewSegment)
        {
            // Changed back to what the training runs with: nothing to start.
            if (Math.Abs(source.Get(def.Key) - _opened[def.Key]) < 1e-6) _pending.Remove(def.Key);
            else _pending.Add(def.Key);
        }
    }

    private void RememberOpened()
    {
        if (_training is null) return;
        foreach (var def in _training.Defs.Where(d => d.Kind == SettingKind.NewSegment)) _opened[def.Key] = _training.Get(def.Key);
    }

    private void ApplyPreset(SettingsPreset preset)
    {
        var source = _training ?? AppRuntime.Settings;
        foreach (string key in source.ApplyPreset(preset, InTraining)) Applied(source, source.Def(key));
        RefreshAll();
    }

    private void RefreshAll()
    {
        foreach (var row in _rows.Values) RefreshRow(row);
        var settings = AppRuntime.Settings;
        _fpsHint.Text = FpsCap.Hint(settings.FpsCapIndex, FrameRate.MonitorHz, settings.VSync) ?? "";
        _fpsHint.Visible = _fpsHint.Text.Length > 0;
        _pendingNote.Text = _pending.Count > 0 ? $"{_pending.Count} new-segment change{(_pending.Count == 1 ? "" : "s")} waiting" : "";
        _startSegmentButton.Disabled = _pending.Count == 0;
    }

    private void RefreshRow(Row row)
    {
        _refreshing = true;
        var def = row.Def;
        var source = Source(def);
        double value = source.Get(def.Key);
        row.Locked = !AppSettings.CanEdit(def.Kind, InTraining);
        switch (row.Input)
        {
            case CheckBox check:
                check.SetPressedNoSignal(value >= 0.5);
                check.Text = value >= 0.5 ? "on" : "off";
                check.Disabled = row.Locked;
                break;
            case OptionButton option:
                option.Selected = (int)value;
                option.Disabled = row.Locked;
                break;
            case SpinBox spin:
                spin.SetValueNoSignal(value);
                spin.Editable = !row.Locked;
                break;
        }
        row.Value.Text = def.ZeroMeans is not null && value == 0 ? def.ZeroMeans : "";
        row.Reset.Disabled = row.Locked || source.IsDefault(def.Key);
        row.Row_.Modulate = row.Locked ? new Color(1, 1, 1, 0.45f) : Colors.White;
        string locked = row.Locked ? "\n\nStart a new training to change this." : "";
        row.Input.TooltipText = def.Tooltip + locked;
        row.Row_.TooltipText = "";
        _refreshing = false;
    }

    // ---------------------------------------------------------------- new segment

    private void AskStartSegment()
    {
        if (_training is null || _pending.Count == 0 || Modal.OpenCount > 0) return;
        var list = string.Join("\n", _pending.Select(k => _training.Def(k)).OrderBy(d => d.Label).Select(d => $"• {d.Label}: {d.Format(_training.Get(d.Key))}"));
        string text = $"These settings change the training's rules, so from the next generation a new segment starts:\n\n{list}\n\n" +
                      "The best time, the stagnation check and the graph restart there. The population carries on" +
                      (_pending.Contains("populationSize") ? "; with another number of cars the extra ones are dropped, or mutated copies of the population are added." : ".");
        Modal.Open(this, "Start a new segment?", Modal.Text(text),
            new Modal.Choice("Cancel"),
            new Modal.Choice("Start new segment", () =>
            {
                _startSegment?.Invoke(_training);
                _pending.Clear();
                RememberOpened();
                RefreshAll();
            }, Primary: true));
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Keycode: Key.Escape } && Modal.OpenCount == 0)
        {
            GetViewport().SetInputAsHandled();
            Back?.Invoke();
        }
    }

    // ---------------------------------------------------------------- debug

    /// <summary>Debug: sets a value as the controls would (with the same side effects).</summary>
    public string? DebugSet(string key, string text)
    {
        if (!_rows.TryGetValue(key, out var row)) return $"no setting '{key}'";
        var def = row.Def;
        if (row.Locked) return $"'{key}' is locked while a training is open";
        double value = def.Type == SettingType.Choice && def.Choices!.ToList().FindIndex(c => c.Equals(text, StringComparison.OrdinalIgnoreCase)) is >= 0 and var i
            ? i : double.Parse(text, CultureInfo.InvariantCulture);
        Change(def, value);
        return null;
    }

    public void DebugPreset(string name) => ApplyPreset(Enum.Parse<SettingsPreset>(name, true));

    public void DebugStartSegment() => AskStartSegment();

    public void DebugBack() => Back?.Invoke();

    /// <summary>Debug: scrolls the shown tab.</summary>
    public void DebugScroll(int pixels) => ((ScrollContainer)_tabs.GetCurrentTabControl()).ScrollVertical = pixels;

    /// <summary>Debug: shows tab <paramref name="index"/>.</summary>
    public void DebugTab(int index) => _tabs.CurrentTab = index;
}
