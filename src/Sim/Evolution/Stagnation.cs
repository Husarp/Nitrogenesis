namespace Nitrogenesis.Sim.Evolution;

/// <summary>
/// Detects stagnation and runs the automatic mutation boost (PLAN §3.7). Fed once per generation with that
/// generation's results; asked once per breeding whether the boost applies.
/// </summary>
/// <remarks>
/// <para>It tracks the segment's best-ever progress (distance units, e.g. cells) and best-ever finish time
/// (ticks) after every generation, and compares the newest values with those <c>window</c> generations
/// earlier:</para>
/// <list type="bullet">
/// <item>no car has finished yet: stagnant if best progress grew by less than <see cref="MinProgressGain"/>;</item>
/// <item>after a finish: stagnant if best time dropped by less than max(1 tick, 0.2 % of the older best time).
/// A first finish inside the window counts as improvement.</item>
/// </list>
/// <para>Stagnation starts a boost of <see cref="EvolutionSettings.BoostGenerations"/> breedings and restarts
/// the window, so the next check needs a full new window. Turning the boost setting off cancels a running
/// boost and stops new ones. State depends only on the observed values, so resuming a session can rebuild
/// it by replaying the stored generation stats.</para>
/// </remarks>
public sealed class StagnationTracker
{
    /// <summary>Progress gain (distance units) below which a window without a finish is stagnant.</summary>
    public const float MinProgressGain = 1f;
    /// <summary>Relative time gain below which a window after a finish is stagnant (0.2 %).</summary>
    public const float MinTimeGainFraction = 0.002f;
    /// <summary>Absolute time gain (ticks) below which a window after a finish is always stagnant.</summary>
    public const float MinTimeGainTicks = 1f;

    // Best-ever values per observed generation since the window (re)started. A ring buffer long enough for
    // the largest window, so a window change between generations needs no reallocation.
    private readonly float[] _progress = new float[EvolutionSettings.MaxStagnationWindow + 1];
    private readonly float[] _time = new float[EvolutionSettings.MaxStagnationWindow + 1];
    private int _count;
    private float _bestProgress, _bestTime = float.PositiveInfinity;

    /// <summary>Breedings the current boost still covers.</summary>
    public int BoostRemaining { get; private set; }

    /// <summary>True when the last <see cref="Observe"/> started a boost (for the "Boost active" banner).</summary>
    public bool JustTriggered { get; private set; }

    /// <summary>Best progress ever in this segment (distance units).</summary>
    public float BestProgress => _bestProgress;
    /// <summary>Best finish time ever in this segment (ticks), +∞ before the first finish.</summary>
    public float BestTime => _bestTime;

    /// <summary>Forgets everything (a new segment starts, PLAN §5).</summary>
    public void Reset()
    {
        _count = 0;
        _bestProgress = 0f;
        _bestTime = float.PositiveInfinity;
        BoostRemaining = 0;
        JustTriggered = false;
    }

    /// <summary>Records one evaluated generation's best progress and best finish time (+∞ when none finished).</summary>
    public void Observe(float bestProgress, float bestFinishTicks, EvolutionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings = settings.Clamped();
        if (bestProgress > _bestProgress) _bestProgress = bestProgress;
        if (bestFinishTicks < _bestTime) _bestTime = bestFinishTicks;

        int window = settings.StagnationWindow, size = window + 1;
        int slot = _count % _progress.Length;
        _progress[slot] = _bestProgress;
        _time[slot] = _bestTime;
        _count++;

        JustTriggered = false;
        if (!settings.StagnationBoost) BoostRemaining = 0;
        if (!settings.StagnationBoost || BoostRemaining > 0 || _count < size) return;

        int oldSlot = (_count - 1 - window) % _progress.Length;
        float oldProgress = _progress[oldSlot], oldTime = _time[oldSlot];
        bool stagnant = float.IsPositiveInfinity(_bestTime)
            ? _bestProgress - oldProgress < MinProgressGain
            : !float.IsPositiveInfinity(oldTime) && oldTime - _bestTime < MathF.Max(MinTimeGainTicks, MinTimeGainFraction * oldTime);
        if (!stagnant) return;

        BoostRemaining = EvolutionSettings.BoostGenerations;
        JustTriggered = true;
        _count = 0; // the next check needs a full new window
    }

    /// <summary>
    /// Called once per breeding: true if the boost applies to it (then one boost generation is used up).
    /// Always false while the boost setting is off.
    /// </summary>
    public bool ConsumeBoost(EvolutionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (BoostRemaining == 0 || !settings.StagnationBoost) return false;
        BoostRemaining--;
        return true;
    }
}
