namespace Nitrogenesis.Sim.Settings;

/// <summary>The engine sound of the followed or player car (PLAN §6.6).</summary>
public static class EngineSound
{
    public const float MinPitch = 0.8f, PitchRange = 1.2f;
    /// <summary>Above this training speed the engine is silent.</summary>
    public const double SilentAboveSpeed = 5;

    /// <summary>Pitch scale = 0.8 + 1.2 × speed / max speed (speed clamped to 0 … max).</summary>
    public static float Pitch(float speed, float maxSpeed)
    {
        float share = maxSpeed > 0 ? Math.Clamp(Math.Abs(speed) / maxSpeed, 0f, 1f) : 0f;
        return MinPitch + PitchRange * share;
    }

    /// <summary>Whether the engine may be heard at this game speed (a multiple of real time; MAX = ∞).</summary>
    public static bool Audible(double gameSpeed) => gameSpeed <= SilentAboveSpeed;
}
