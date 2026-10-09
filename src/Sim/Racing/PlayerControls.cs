namespace Nitrogenesis.Sim.Racing;

/// <summary>
/// Turns the player's input into throttle and steer for <see cref="CarPhysics"/> (PLAN §6.5), once per tick.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Keyboard: each axis is −1, 0 or +1 (WASD / arrows); the output moves towards it at
/// 1 / <see cref="RampSeconds"/> per second, so a full press takes 0.1 s (6 ticks) and so does letting go.</item>
/// <item>Gamepad: the left stick steers, the right trigger is throttle and the left trigger brake/reverse. Outside
/// its dead zone an analog axis is used directly (rescaled so the dead zone's edge is 0) and wins over the keys;
/// the ramp then continues from that value when the keys take over again.</item>
/// </list>
/// Deterministic and allocation-free; the app reads the devices, this decides what reaches the car.
/// </remarks>
public sealed class PlayerControls
{
    public const float RampSeconds = 0.1f;
    /// <summary>Change per tick of a keyboard axis: 1/6.</summary>
    public const float RampPerTick = RacingSettings.Dt / RampSeconds;
    public const float StickDeadZone = 0.2f;
    public const float TriggerDeadZone = 0.05f;

    /// <summary>Throttle applied in the last tick, −1…1.</summary>
    public float Throttle { get; private set; }

    /// <summary>Steer applied in the last tick, −1…1 (positive turns towards +y, i.e. right on screen).</summary>
    public float Steer { get; private set; }

    public void Reset()
    {
        Throttle = 0f;
        Steer = 0f;
    }

    /// <summary>
    /// One tick. <paramref name="keyThrottle"/> / <paramref name="keySteer"/>: keyboard axes (−1, 0, +1; both keys of
    /// an axis held = 0). <paramref name="stickX"/>: left stick −1…1. <paramref name="rightTrigger"/> /
    /// <paramref name="leftTrigger"/>: 0…1.
    /// </summary>
    public void Tick(int keyThrottle, int keySteer, float stickX, float rightTrigger, float leftTrigger)
    {
        float padThrottle = Analog(rightTrigger, TriggerDeadZone) - Analog(leftTrigger, TriggerDeadZone);
        float padSteer = Analog(stickX, StickDeadZone);
        Throttle = padThrottle != 0f ? Clamp(padThrottle) : Ramp(Throttle, Math.Sign(keyThrottle));
        Steer = padSteer != 0f ? Clamp(padSteer) : Ramp(Steer, Math.Sign(keySteer));
    }

    /// <summary>Moves <paramref name="value"/> towards <paramref name="target"/> by at most one ramp step.</summary>
    public static float Ramp(float value, float target)
    {
        float d = target - value;
        // A small tolerance so 6 float steps of 1/6 land exactly on the target.
        if (MathF.Abs(d) <= RampPerTick + 1e-5f) return target;
        return value + (d > 0f ? RampPerTick : -RampPerTick);
    }

    /// <summary>An analog axis with a dead zone: 0 inside it, rescaled to reach ±1 at full deflection; NaN = 0.</summary>
    public static float Analog(float v, float deadZone)
    {
        if (!(MathF.Abs(v) > deadZone)) return 0f;
        float a = (MathF.Abs(v) - deadZone) / (1f - deadZone);
        return MathF.CopySign(a > 1f ? 1f : a, v);
    }

    private static float Clamp(float v) => v > 1f ? 1f : v < -1f ? -1f : v;
}
