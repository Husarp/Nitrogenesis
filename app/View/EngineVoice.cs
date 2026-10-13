using Godot;
using Nitrogenesis.Sim.Settings;
using Nitrogenesis.Ui;

namespace Nitrogenesis.View;

/// <summary>
/// The engine sound of the followed or player car (PLAN §6.6): a looped hum (app/audio/engine_loop.wav, made by
/// tools/make_audio.py) with <c>pitch = 0.8 + 1.2 × speed / max speed</c>. Silent when sound is off, above 5× speed, paused or
/// when there is no car to follow.
/// </summary>
public partial class EngineVoice : AudioStreamPlayer
{
    /// <summary>True while the engine is playing.</summary>
    public bool Sounding => Playing;

    public override void _Ready()
    {
        var wav = GD.Load<AudioStreamWav>("res://audio/engine_loop.wav");
        wav.LoopMode = AudioStreamWav.LoopModeEnum.Forward;
        wav.LoopBegin = 0;
        wav.LoopEnd = (int)(wav.GetLength() * wav.MixRate);
        Stream = wav;
        Bus = "Master";
    }

    /// <param name="active">There is a car to hear and it is running.</param>
    /// <param name="speed">Its forward speed, cells/s.</param>
    /// <param name="maxSpeed">The top speed of the segment, cells/s.</param>
    /// <param name="gameSpeed">The training speed (a multiple of real time; 0 when paused; ∞ for MAX).</param>
    public void Update(bool active, float speed, float maxSpeed, double gameSpeed)
    {
        var settings = AppRuntime.Settings;
        bool on = active && settings.EngineSoundOn && settings.EngineVolume > 0 && gameSpeed > 0 && EngineSound.Audible(gameSpeed);
        if (!on)
        {
            if (Playing) Stop();
            return;
        }
        PitchScale = EngineSound.Pitch(speed, maxSpeed);
        VolumeDb = Mathf.LinearToDb(settings.EngineVolume);
        if (!Playing) Play();
    }
}
