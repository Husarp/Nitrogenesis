using System;
using Godot;
using Nitrogenesis.Sim;

namespace Nitrogenesis.Ui;

/// <summary>Minimal main menu for M2: Watch training, Drive, Quit (the full §6.1 menu comes with M3+).</summary>
public partial class MainMenu : Control
{
    public event Action? WatchTraining;
    public event Action? Drive;

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
        box.AddThemeConstantOverride("separation", 14);
        center.AddChild(box);

        var version = (string)ProjectSettings.GetSetting("application/config/version");
        box.AddChild(UiTheme.Label("NITROGENESIS", 64, UiTheme.Accent, HorizontalAlignment.Center));
        box.AddChild(UiTheme.Label($"version {version} · sim {SimInfo.SimVersion}", 16, UiTheme.Muted, HorizontalAlignment.Center));
        box.AddChild(new Control { CustomMinimumSize = new Vector2(0, 24) });

        var watch = MenuButton("Watch training");
        watch.Pressed += () => WatchTraining?.Invoke();
        box.AddChild(watch);
        var drive = MenuButton("Drive");
        drive.Pressed += () => Drive?.Invoke();
        drive.Disabled = Drive is null;
        drive.TooltipText = "Drive a track yourself";
        box.AddChild(drive);
        var quit = MenuButton("Quit");
        quit.Pressed += () => GetTree().Quit();
        box.AddChild(quit);

        watch.CallDeferred(Control.MethodName.GrabFocus);
    }

    private static Button MenuButton(string text) =>
        new() { Text = text, CustomMinimumSize = new Vector2(320, 48) };
}
