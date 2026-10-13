using System;
using System.Globalization;
using Godot;
using Nitrogenesis.Sim;
using Nitrogenesis.Sim.Io;

namespace Nitrogenesis.Ui;

/// <summary>
/// The main menu (PLAN §6.1): New training · Continue (last session) · Sessions · Track editor · Drive · Settings · Quit.
/// Settings (M7) is shown but not available yet.
/// </summary>
public partial class MainMenu : Control
{
    private SessionInfo? _latest;

    public event Action? NewTraining, Continue, Sessions, Drive, Editor;

    /// <param name="latest">The session "Continue" opens (the one saved last), or null.</param>
    public static MainMenu Create(SessionInfo? latest) => new() { _latest = latest };

    public override void _Ready()
    {
        Theme = UiTheme.Theme;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var bg = new ColorRect { Color = UiTheme.Background };
        bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(bg);

        var center = new CenterContainer();
        center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(center);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 12);
        center.AddChild(box);

        var version = (string)ProjectSettings.GetSetting("application/config/version");
        box.AddChild(UiTheme.Label("NITROGENESIS", 64, UiTheme.Accent, HorizontalAlignment.Center));
        box.AddChild(UiTheme.Label($"version {version} · sim {SimInfo.SimVersion}", 16, UiTheme.Muted, HorizontalAlignment.Center));
        box.AddChild(new Control { CustomMinimumSize = new Vector2(0, 16) });

        var newTraining = MenuButton("New training");
        newTraining.TooltipText = "Pick a track and start training a new population";
        newTraining.Pressed += () => NewTraining?.Invoke();
        box.AddChild(newTraining);

        var cont = MenuButton("Continue");
        cont.Pressed += () => Continue?.Invoke();
        cont.Disabled = _latest is null;
        box.AddChild(cont);
        var contInfo = UiTheme.Label(_latest is { } l
                ? $"{l.Name} · generation {l.Generation.ToString(CultureInfo.InvariantCulture)}"
                : "No saved training yet", 13, UiTheme.Muted, HorizontalAlignment.Center);
        contInfo.CustomMinimumSize = new Vector2(320, 0);
        contInfo.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        box.AddChild(contInfo);

        var sessions = MenuButton("Sessions");
        sessions.TooltipText = "Open, rename or delete saved trainings";
        sessions.Pressed += () => Sessions?.Invoke();
        box.AddChild(sessions);

        var editor = MenuButton("Track editor");
        editor.Pressed += () => Editor?.Invoke();
        editor.TooltipText = "Draw and edit tracks";
        box.AddChild(editor);

        var drive = MenuButton("Drive");
        drive.Pressed += () => Drive?.Invoke();
        drive.TooltipText = "Drive a track yourself";
        box.AddChild(drive);

        var settings = MenuButton("Settings");
        settings.Disabled = true;
        settings.TooltipText = "Comes in a later version (M7)";
        box.AddChild(settings);

        var quit = MenuButton("Quit");
        quit.Pressed += () => GetTree().Root.PropagateNotification((int)NotificationWMCloseRequest);
        box.AddChild(quit);

        (cont.Disabled ? newTraining : cont).CallDeferred(Control.MethodName.GrabFocus);
    }

    private static Button MenuButton(string text) =>
        new() { Text = text, CustomMinimumSize = new Vector2(320, 46) };
}
