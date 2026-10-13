using Godot;
using Nitrogenesis.Sim.Settings;

namespace Nitrogenesis.Ui;

/// <summary>
/// The interface sounds (PLAN §6.6): a click when any button is pressed. Main adds one <see cref="Sfx"/> to the tree and hooks
/// every button that enters it. The sound is made by tools/make_audio.py (app/audio/click.wav).
/// </summary>
public partial class Sfx : Node
{
    private static Sfx? s_instance;
    private AudioStreamPlayer _player = null!;

    /// <summary>Clicks played so far (the debug <c>sfx</c> script step prints it).</summary>
    public static int ClicksPlayed { get; private set; }

    public override void _Ready()
    {
        s_instance = this;
        _player = new AudioStreamPlayer { Stream = GD.Load<AudioStream>("res://audio/click.wav"), Bus = "Master" };
        AddChild(_player);
        Refresh();
        GetTree().NodeAdded += Hook;
    }

    public override void _ExitTree()
    {
        if (s_instance == this) s_instance = null;
        GetTree().NodeAdded -= Hook;
    }

    private static void Hook(Node node)
    {
        if (node is not BaseButton button || button.HasMeta("sfx")) return;
        button.SetMeta("sfx", true);
        button.Pressed += Click;
    }

    /// <summary>Picks up a changed volume.</summary>
    public static void Refresh()
    {
        if (s_instance is not { } sfx) return;
        sfx._player.VolumeDb = Mathf.LinearToDb(Mathf.Max(AppRuntime.Settings.UiVolume, 0.0001f));
    }

    public static void Click()
    {
        if (s_instance is not { } sfx || !AppRuntime.Settings.UiSound || AppRuntime.Settings.UiVolume <= 0) return;
        ClicksPlayed++;
        sfx._player.Play();
    }
}
