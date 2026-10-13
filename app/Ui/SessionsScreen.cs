using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Godot;
using Nitrogenesis.Sim.Io;

namespace Nitrogenesis.Ui;

/// <summary>
/// "Sessions" (PLAN §6.1): every saved training, newest first, with Open, Rename and Delete (with a confirmation).
/// Sessions from another physics version open read-only (PLAN §5.1); damaged ones are listed with the reason.
/// </summary>
public partial class SessionsScreen : Control
{
    private SessionStore _store = null!;
    private string? _message, _select;
    private List<SessionInfo> _sessions = new();
    private Tree _list = null!;
    private readonly List<TreeItem> _items = new();
    private Label _info = null!;
    private Button _open = null!, _rename = null!, _delete = null!;

    public event Action<string>? Open;
    public event Action? Back;

    /// <param name="store">The sessions folder.</param>
    /// <param name="message">A message to show at once (e.g. why a session did not open), or null.</param>
    /// <param name="select">The session to select, or null for the newest.</param>
    public static SessionsScreen Create(SessionStore store, string? message = null, string? select = null) =>
        new() { _store = store, _message = message, _select = select };

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
        column.AddThemeConstantOverride("separation", 14);
        margin.AddChild(column);
        column.AddChild(UiTheme.Label("SESSIONS", 32, UiTheme.Accent));
        var where = UiTheme.Label($"Saved trainings in {_store.Root}", 14, UiTheme.Muted);
        where.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        column.AddChild(where);

        _list = new Tree { SizeFlagsVertical = SizeFlags.ExpandFill, Columns = 5, HideRoot = true, ColumnTitlesVisible = true, SelectMode = Tree.SelectModeEnum.Row };
        _list.AddThemeFontSizeOverride("font_size", 16);
        _list.AddThemeFontSizeOverride("title_button_font_size", 14);
        string[] titles = ["Name", "Track", "Generation", "Branches", "Saved"];
        for (int c = 0; c < titles.Length; c++)
        {
            _list.SetColumnTitle(c, titles[c]);
            _list.SetColumnTitleAlignment(c, HorizontalAlignment.Left);
            _list.SetColumnExpand(c, c == 0);
            _list.SetColumnClipContent(c, c == 0);
            if (c > 0) _list.SetColumnCustomMinimumWidth(c, c == 4 ? 170 : 130);
        }
        _list.ItemSelected += Selected;
        _list.ItemActivated += OpenSelected;
        column.AddChild(_list);
        _info = UiTheme.Label("", 15, UiTheme.Muted);
        _info.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        column.AddChild(_info);

        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 12);
        column.AddChild(buttons);
        var back = new Button { Text = "Back", CustomMinimumSize = new Vector2(140, 44) };
        back.Pressed += () => Back?.Invoke();
        buttons.AddChild(back);
        _open = new Button { Text = "Open", CustomMinimumSize = new Vector2(160, 44) };
        _open.Pressed += OpenSelected;
        buttons.AddChild(_open);
        _rename = new Button { Text = "Rename…", CustomMinimumSize = new Vector2(140, 44) };
        _rename.Pressed += RenameSelected;
        buttons.AddChild(_rename);
        _delete = new Button { Text = "Delete…", CustomMinimumSize = new Vector2(140, 44) };
        _delete.Pressed += DeleteSelected;
        buttons.AddChild(_delete);

        Reload(_select);
        _list.CallDeferred(Control.MethodName.GrabFocus);
        if (_message is not null) Modal.Message(this, "Could not open the session", _message);
    }

    private void Reload(string? select)
    {
        _sessions = _store.List();
        _list.Clear();
        _items.Clear();
        var root = _list.CreateItem();
        int selectIndex = 0;
        for (int i = 0; i < _sessions.Count; i++)
        {
            var s = _sessions[i];
            var item = _list.CreateItem(root);
            bool readOnly = s.CanOpen && !s.Compatibility.SimVersionMatches;
            item.SetText(0, s.Name + (readOnly ? "   (older physics: read-only)" : !s.CanOpen ? "   (damaged)" : ""));
            item.SetText(1, s.CanOpen ? s.TrackName : "—");
            item.SetText(2, s.CanOpen ? s.Generation.ToString(CultureInfo.InvariantCulture) : "—");
            item.SetText(3, s.CanOpen ? $"{s.Branches} ({s.Branch})" : "—");
            item.SetText(4, Local(s.SavedAtUtc));
            if (!s.CanOpen || readOnly)
                for (int c = 0; c < 5; c++) item.SetCustomColor(c, UiTheme.Muted);
            _items.Add(item);
            if (s.Name == select) selectIndex = i;
        }
        bool any = _sessions.Count > 0;
        if (any)
        {
            _items[selectIndex].Select(0);
            _list.ScrollToItem(_items[selectIndex]);
        }
        _info.Text = any ? "" : "No saved trainings yet. Start one with New training in the menu; it is saved automatically.";
        Selected();
    }

    private static string Local(DateTime utc) =>
        utc == default ? "?" : utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private SessionInfo? Current
    {
        get
        {
            var selected = _list.GetSelected();
            int index = selected is null ? -1 : _items.IndexOf(selected);
            return index >= 0 && index < _sessions.Count ? _sessions[index] : null;
        }
    }

    private void Selected()
    {
        // Muted rows turn to the on-accent text while selected (the custom colour would hide the selected font colour).
        var selected = _list.GetSelected();
        for (int i = 0; i < _items.Count && i < _sessions.Count; i++)
        {
            bool muted = !_sessions[i].CanOpen || !_sessions[i].Compatibility.SimVersionMatches;
            if (!muted) continue;
            var color = _items[i] == selected ? Colors.White : UiTheme.Muted;
            for (int c = 0; c < 5; c++) _items[i].SetCustomColor(c, color);
        }
        var s = Current;
        _open.Disabled = s is not { CanOpen: true };
        _rename.Disabled = s is null;
        _delete.Disabled = s is null;
        if (s is not null && s.CanOpen && !s.Compatibility.SimVersionMatches) _info.Text = s.Compatibility.Message ?? "";
        else if (s is not null && !s.CanOpen) _info.Text = "This session cannot be opened: " + s.Problem;
        else if (_sessions.Count > 0) _info.Text = "";
    }

    private void OpenSelected()
    {
        if (Current is { CanOpen: true } s) Open?.Invoke(s.Name);
    }

    private void RenameSelected()
    {
        if (Current is not { } s || Modal.OpenCount > 0) return;
        var edit = new LineEdit { Text = s.Name, CustomMinimumSize = new Vector2(460, 0), MaxLength = SessionStore.MaxNameLength, SelectAllOnFocus = true };
        edit.AddThemeFontSizeOverride("font_size", 17);
        var modal = Modal.Open(this, "Rename session", edit,
            new Modal.Choice("Cancel"),
            new Modal.Choice("Rename", () => DoRename(s.Name, edit.Text), Primary: true));
        edit.TextSubmitted += text => modal.Close(() => DoRename(s.Name, text));
        edit.CallDeferred(Control.MethodName.GrabFocus);
    }

    /// <summary>Renames the session (the debug script calls it too).</summary>
    public void DoRename(string name, string newName)
    {
        string wanted = (newName ?? "").Trim();
        if (wanted.Length == 0)
        {
            Modal.Message(this, "Could not rename", "Type a name for the session.");
            return;
        }
        if (SessionStore.Sanitize(wanted) != wanted)
        {
            Modal.Message(this, "Could not rename", $"'{wanted}' is not a usable name. Use letters, digits, spaces and - _ ( ) [ ] , . + ' & ! # only (at most {SessionStore.MaxNameLength} characters, no dot or space at the end, not a Windows device name like CON).");
            return;
        }
        if (!string.Equals(wanted, name, StringComparison.OrdinalIgnoreCase) && _store.UniqueName(wanted) != wanted)
        {
            Modal.Message(this, "Could not rename", $"A session named '{wanted}' already exists. Pick another name.");
            return;
        }
        try
        {
            Reload(_store.Rename(name, wanted));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Modal.Message(this, "Could not rename", e is SessionLockedException ? "This session is open in another Nitrogenesis window. Close it there first." : e.Message);
            Reload(name);
        }
    }

    private void DeleteSelected()
    {
        if (Current is not { } s || Modal.OpenCount > 0) return;
        Modal.Open(this, "Delete session?", Modal.Text($"'{s.Name}' and its whole history (every generation and branch) will be deleted. This cannot be undone.", UiTheme.Text),
            new Modal.Choice("Cancel", Primary: true),
            new Modal.Choice("Delete", () => DoDelete(s.Name)));
    }

    /// <summary>Opens the rename box for the selected session (debug script).</summary>
    public void AskRename() => RenameSelected();

    /// <summary>Opens the delete confirmation for the selected session (debug script).</summary>
    public void AskDelete() => DeleteSelected();

    /// <summary>Deletes the session (after the confirmation; the debug script calls it too).</summary>
    public void DoDelete(string name)
    {
        try
        {
            _store.Delete(name);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Modal.Message(this, "Could not delete", e is SessionLockedException ? "This session is open in another Nitrogenesis window. Close it there first." : e.Message);
        }
        Reload(null);
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (Modal.OpenCount > 0) return;
        if (e is InputEventKey { Pressed: true, Keycode: Key.Escape })
        {
            Back?.Invoke();
            GetViewport().SetInputAsHandled();
        }
        else if (e is InputEventKey { Pressed: true, Keycode: Key.Delete })
        {
            DeleteSelected();
            GetViewport().SetInputAsHandled();
        }
        else if (e is InputEventKey { Pressed: true, Keycode: Key.F2 })
        {
            RenameSelected();
            GetViewport().SetInputAsHandled();
        }
    }
}
