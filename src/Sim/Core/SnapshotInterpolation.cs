namespace Nitrogenesis.Sim.Core;

/// <summary>
/// Render-side interpolation between the <c>prev</c> and <c>curr</c> snapshots (PLAN §4). Never extrapolates.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>alpha is clamped to 0…1; position lerps, heading lerps along the shortest arc.</item>
/// <item>Without interpolation (above 1×), when the generation id changed, or when the pair is not exactly one tick
/// apart (a pair left from a faster speed spans a whole chunk of ticks), every agent is drawn at curr.</item>
/// <item>An agent whose status changed between the two (alive → finished/crashed, or a reset) is drawn at curr.</item>
/// </list>
/// No allocation; the output arrays belong to the caller.
/// </remarks>
public static class SnapshotInterpolation
{
    private const float TwoPi = 6.28318531f, Pi = 3.14159265f;

    public static void Interpolate(AgentSnapshot prev, AgentSnapshot curr, int count, float alpha, bool interpolate,
        float[] x, float[] y, float[] angle)
    {
        ArgumentNullException.ThrowIfNull(prev);
        ArgumentNullException.ThrowIfNull(curr);
        if (count > curr.Capacity || count > prev.Capacity || count > x.Length || count > y.Length || count > angle.Length)
            throw new ArgumentOutOfRangeException(nameof(count));
        alpha = alpha > 1f ? 1f : alpha >= 0f ? alpha : 0f; // NaN → 0
        bool lerp = interpolate && alpha < 1f && prev.GenerationId == curr.GenerationId && curr.Tick - prev.Tick == 1;
        for (int i = 0; i < count; i++)
        {
            if (!lerp || prev.Status[i] != curr.Status[i])
            {
                x[i] = curr.X[i];
                y[i] = curr.Y[i];
                angle[i] = curr.Angle[i];
                continue;
            }
            x[i] = prev.X[i] + (curr.X[i] - prev.X[i]) * alpha;
            y[i] = prev.Y[i] + (curr.Y[i] - prev.Y[i]) * alpha;
            angle[i] = LerpAngle(prev.Angle[i], curr.Angle[i], alpha);
        }
    }

    /// <summary>Heading between <paramref name="from"/> and <paramref name="to"/> (radians) along the shortest arc, in [0, 2π).</summary>
    public static float LerpAngle(float from, float to, float alpha)
    {
        float d = to - from;
        d -= TwoPi * MathF.Floor((d + Pi) / TwoPi); // wrap to [−π, π)
        float a = from + d * alpha;
        if (a < 0f) a += TwoPi;
        else if (a >= TwoPi) a -= TwoPi;
        return a;
    }
}
