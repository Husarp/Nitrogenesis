using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Godot;
using Nitrogenesis.Sim.Io;
using Nitrogenesis.Sim.Map;
using Nitrogenesis.Ui;

namespace Nitrogenesis.Editor;

/// <summary>
/// The editor's New / Open / Save / Save as flows and the "Save changes?" question (ticket 05), as boxes over the
/// <see cref="EditorView"/>. The files themselves are handled by <see cref="TrackLibrary"/> (atomic save, name rules). A save
/// that fails shows the reason and changes nothing: the document stays dirty and the file on disk stays as it was.
/// The debug verbs call the same methods as the buttons (<see cref="DoSaveAs"/>, <see cref="OpenUser"/>, …).
/// </summary>
public sealed class EditorFileDialogs
{
    private readonly EditorView _view;
    private readonly TrackLibrary _lib;

    public EditorFileDialogs(EditorView view, TrackLibrary? library = null)
    {
        _view = view;
        _lib = library ?? TrackLibrary.Default();
    }

    private EditDocument Doc => _view.Document;
    private static bool Busy => Modal.OpenCount > 0;

    // ---------------------------------------------------------------- unsaved changes

    /// <summary>Runs <paramref name="proceed"/> now when nothing is unsaved, otherwise after asking "Save changes to X?" (Save / Don't save / Cancel).</summary>
    public void Guard(Action proceed)
    {
        if (!Doc.IsDirty)
        {
            proceed();
            return;
        }
        if (Busy) return;
        Modal.Open(_view, "Unsaved changes", Modal.Text($"Save changes to \"{Doc.Name}\"? Without saving they are lost."),
            new Modal.Choice("Cancel"),
            new Modal.Choice("Don't save", proceed),
            new Modal.Choice("Save", () => Save(proceed), Primary: true));
    }

    // ---------------------------------------------------------------- New

    public void AskNew() => Guard(ShowNew);

    private void ShowNew()
    {
        if (Busy) return;
        var (sw, sh) = EditDocument.Dimensions(MapSize.S);
        var (mw, mh) = EditDocument.Dimensions(MapSize.M);
        var (lw, lh) = EditDocument.Dimensions(MapSize.L);
        Modal.Open(_view, "New map", Modal.Text("An empty map, all wall. Pick its size."),
            new Modal.Choice("Cancel"),
            new Modal.Choice(Dim("S", sw, sh), () => NewMap(MapSize.S)),
            new Modal.Choice(Dim("M", mw, mh), () => NewMap(MapSize.M), Primary: true),
            new Modal.Choice(Dim("L", lw, lh), () => NewMap(MapSize.L)));
        static string Dim(string label, int w, int h) => string.Create(CultureInfo.InvariantCulture, $"{label}  {w}×{h}");
    }

    public void NewMap(MapSize size) => _view.ReplaceFile(EditDocument.New(size), null);

    /// <summary>Debug: New with the unsaved-changes question.</summary>
    public void NewGuarded(MapSize size) => Guard(() => NewMap(size));

    // ---------------------------------------------------------------- Open

    public void AskOpen() => Guard(ShowOpen);

    private void ShowOpen()
    {
        if (Busy) return;
        var users = _lib.List();
        var bundled = BundledTracks.LoadAll();
        var list = new ItemList { CustomMinimumSize = new Vector2(480, 300) };
        var actions = new List<Action?>();
        void Heading(string text)
        {
            int i = list.AddItem(text, null, false);
            list.SetItemCustomFgColor(i, UiTheme.Accent);
            actions.Add(null);
        }
        void Row(string text, Action? open, string? tip = null)
        {
            int i = list.AddItem(text, null, open is not null);
            if (open is null) list.SetItemCustomFgColor(i, UiTheme.Muted);
            if (tip is not null) list.SetItemTooltip(i, tip);
            actions.Add(open);
        }
        Heading("YOUR TRACKS");
        if (users.Count == 0) Row("(none saved yet)", null);
        foreach (var u in users)
        {
            var info = u;
            if (info.CanOpen) Row(string.Create(CultureInfo.InvariantCulture, $"{info.Name}   {info.Width}×{info.Height}"), () => OpenUser(info));
            else
            {
                GD.PushWarning($"Track {info.Path}: {info.Problem}");
                Row($"{info.Name} - {DamagedReason}", null, info.Problem);
            }
        }
        Heading("BUNDLED (read-only: opens a copy)");
        foreach (var t in bundled)
        {
            var track = t;
            Row(string.Create(CultureInfo.InvariantCulture, $"{track.Name}   {track.Grid.Width}×{track.Grid.Height}   bundled"), () => OpenBundled(track));
        }
        for (int i = 0; i < actions.Count; i++)
            if (actions[i] is not null) { list.Select(i); break; }

        Modal? modal = null;
        modal = Modal.Open(_view, "Open track", list,
            new Modal.Choice("Cancel"),
            new Modal.Choice("Open", () =>
            {
                int[] sel = list.GetSelectedItems();
                if (sel.Length > 0) actions[sel[0]]?.Invoke();
            }, Primary: true));
        list.ItemActivated += i => modal.Close(actions[(int)i]);
        list.CallDeferred(Control.MethodName.GrabFocus);
    }

    /// <summary>What the lists say about a track file that cannot be read (the parser's own text goes to the tooltip and the log).</summary>
    public const string DamagedReason = "damaged: not a readable track file";

    public void OpenUser(UserTrackInfo info)
    {
        try
        {
            var track = TrackLibrary.Load(info.Path);
            _view.ReplaceFile(new EditDocument(track), info.Path);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            GD.PushWarning($"Track {info.Path}: {e.Message}");
            Modal.Message(_view, "Could not open the track", $"{info.Name} - {DamagedReason}.");
        }
    }

    /// <summary>A bundled track is never edited in place: the document is a copy with no file, so Save asks for a name.</summary>
    public void OpenBundled(Track track)
    {
        var doc = new EditDocument(track) { Name = track.Name + " copy" };
        _view.ReplaceFile(doc, null);
    }

    /// <summary>Debug: open the user track called <paramref name="name"/>, with the unsaved-changes question.</summary>
    public string? OpenUserByName(string name)
    {
        var info = _lib.List().Find(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
        if (info is null) return $"no user track '{name}'";
        Guard(() => OpenUser(info));
        return null;
    }

    public string? OpenBundledByName(string name)
    {
        var track = BundledTracks.LoadAll().Find(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)
                                                      || string.Equals(t.Name.Replace(' ', '_').Replace('-', '_'), name, StringComparison.OrdinalIgnoreCase));
        if (track is null) return $"no bundled track '{name}'";
        Guard(() => OpenBundled(track));
        return null;
    }

    // ---------------------------------------------------------------- Save / Save as

    /// <summary>Save: back to the file the document came from, otherwise Save as. <paramref name="then"/> runs after a successful save.</summary>
    public void Save(Action? then = null)
    {
        if (_view.FilePath is { } path && File.Exists(path)) Write(Path.GetFileNameWithoutExtension(path), then);
        else SaveAs(then);
    }

    /// <summary>The name box. <paramref name="text"/> is what it starts with (default: a suggestion).</summary>
    public void SaveAs(Action? then = null, string? text = null)
    {
        if (Busy) return;
        var body = new VBoxContainer();
        body.AddThemeConstantOverride("separation", 8);
        body.AddChild(Modal.Text("Name of the track:", UiTheme.Muted));
        var edit = new LineEdit { Text = text ?? Suggest(), CustomMinimumSize = new Vector2(460, 0), MaxLength = TrackLibrary.MaxNameLength, SelectAllOnFocus = true };
        edit.AddThemeFontSizeOverride("font_size", 17);
        body.AddChild(edit);
        var modal = Modal.Open(_view, "Save track as", body,
            new Modal.Choice("Cancel"),
            new Modal.Choice("Save", () => DoSaveAs(edit.Text, then), Primary: true));
        edit.TextSubmitted += t => modal.Close(() => DoSaveAs(t, then));
        edit.CallDeferred(Control.MethodName.GrabFocus);
    }

    /// <summary>A name for a document that has no file: its own name, made unique among the saved tracks.</summary>
    public string Suggest()
    {
        string name = TrackLibrary.Normalize(Doc.Name);
        if (TrackLibrary.CheckName(name) is not null) name = "My track";
        if (_view.FilePath is not null || !_lib.Exists(name)) return name;
        for (int n = 2; ; n++)
        {
            string candidate = string.Create(CultureInfo.InvariantCulture, $"{name} ({n})");
            if (candidate.Length <= TrackLibrary.MaxNameLength && !_lib.Exists(candidate)) return candidate;
            if (n > 999) return name;
        }
    }

    /// <summary>Checks the name, asks before replacing another track, then saves (the debug script calls this too).</summary>
    public void DoSaveAs(string name, Action? then = null)
    {
        string wanted = TrackLibrary.Normalize(name);
        if (TrackLibrary.CheckName(wanted) is { } problem)
        {
            Modal.Open(_view, "Could not save", Modal.Text(problem), new Modal.Choice("OK", () => SaveAs(then, wanted), Primary: true));
            return;
        }
        bool sameFile = _view.FilePath is { } own && string.Equals(Path.GetFileNameWithoutExtension(own), wanted, StringComparison.OrdinalIgnoreCase);
        if (!sameFile && _lib.FindExisting(wanted) is { } existing)
        {
            Modal.Open(_view, "Overwrite?", Modal.Text($"A track named {Path.GetFileNameWithoutExtension(existing)} already exists. Overwrite?"),
                new Modal.Choice("Cancel", () => SaveAs(then, wanted)),
                new Modal.Choice("Overwrite", () => Write(wanted, then), Primary: true));
            return;
        }
        Write(wanted, then);
    }

    private bool Write(string name, Action? then)
    {
        var doc = Doc;
        try
        {
            string path = _lib.Save(doc.Snapshot(), name);
            doc.Name = Path.GetFileNameWithoutExtension(path); // the file's spelling (an existing track keeps its letter case)
            doc.MarkSaved();
            _view.FilePath = path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            GD.PushError($"Saving the track '{name}' failed: {e.Message}");
            Modal.Message(_view, "Could not save the track", $"{e.Message}\n\nNothing was lost: the map is still open and unsaved. Fix the problem (free disk space, make the folder writable) and save again.");
            return false;
        }
        GD.Print($"editor-save: '{Doc.Name}' -> {_view.FilePath}");
        then?.Invoke();
        return true;
    }
}
