using Godot;
using Nitrogenesis.Sim;

namespace Nitrogenesis;

/// <summary>M0 placeholder screen: name, version and live FPS, proving the export and the Sim reference work.</summary>
public partial class Main : Control
{
    private Label _fps = null!;

    public override void _Ready()
    {
        var version = (string)ProjectSettings.GetSetting("application/config/version");
        GetWindow().Title = $"Nitrogenesis {version}";
        GetNode<Label>("Center/Box/Version").Text = $"version {version} · sim {SimInfo.SimVersion}";
        _fps = GetNode<Label>("Center/Box/Fps");
    }

    public override void _Process(double delta) =>
        _fps.Text = $"{Engine.GetFramesPerSecond()} FPS (cap {Engine.MaxFps})";
}
