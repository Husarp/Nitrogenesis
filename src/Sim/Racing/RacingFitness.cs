namespace Nitrogenesis.Sim.Racing;

/// <summary>
/// Racing score and stop rules (PLAN §3.6). Pure functions over a car's progress record, so
/// <see cref="RacingMode"/> keeps the record in its per-agent arrays.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>progress = 1 − bestDist / startDist, clamped to 0…1 (bilinear distance-field sample).</item>
/// <item>Not finished: score = progress − 0.01 × ticksToBestProgress / timeLimitTicks.</item>
/// <item>Finished: score = 2 + (timeLimit − finishTime) / timeLimit, finish time at sub-tick precision.
/// Any finisher (score &gt; 2) beats any non-finisher (score ≤ 1).</item>
/// <item>Stall: after a 1.5 s start grace, a car whose best distance has not improved by more than 0.25 cell
/// for the stall time stops.</item>
/// </list>
/// </remarks>
public static class RacingFitness
{
    public const float TieBreakWeight = 0.01f;
    public const float FinishBase = 2f;
    public const float StartGraceSeconds = 1.5f;
    /// <summary>Best-distance improvement (cells) that resets the stall clock.</summary>
    public const float StallDistance = 0.25f;
    /// <summary>Auto time limit = reference driver time × this.</summary>
    public const float AutoTimeLimitFactor = 2.5f;
    /// <summary>Live editor estimate = path length / (this × max speed) (PLAN §3.2).</summary>
    public const float EstimateSpeedFactor = 0.6f;

    public static int StartGraceTicks => (int)MathF.Ceiling(StartGraceSeconds * RacingSettings.TicksPerSecond);

    /// <summary>
    /// Per-car progress record. <see cref="BestDistance"/> and <see cref="BestTick"/> follow every improvement;
    /// <see cref="StallDistanceRef"/> and <see cref="StallTick"/> only move when the best distance improves by
    /// more than <see cref="StallDistance"/>.
    /// </summary>
    public struct Progress
    {
        public float BestDistance;
        public int BestTick;
        public float StallDistanceRef;
        public int StallTick;

        public static Progress Start(float startDistance) => new()
        {
            BestDistance = startDistance,
            StallDistanceRef = startDistance,
        };

        /// <summary>Records the car's distance to the finish after tick <paramref name="tick"/> (1-based count of ticks run).</summary>
        public void Update(float distance, int tick)
        {
            if (distance < BestDistance)
            {
                BestDistance = distance;
                BestTick = tick;
            }
            // StallDistanceRef − distance is +∞ when the start was unreachable: any finite distance then counts.
            if (StallDistanceRef - BestDistance > StallDistance)
            {
                StallDistanceRef = BestDistance;
                StallTick = tick;
            }
        }

        /// <summary>True when, after the start grace, the stall time has passed without enough improvement.</summary>
        public readonly bool IsStalled(int tick, int stallTicks)
        {
            int clockStart = Math.Max(StallTick, StartGraceTicks);
            return tick - clockStart >= stallTicks;
        }
    }

    /// <summary>1 − best / start, clamped to 0…1; 0 when the start distance is not a usable positive number.</summary>
    public static float ProgressFraction(float bestDistance, float startDistance)
    {
        if (!(startDistance > 0f) || startDistance == float.PositiveInfinity) return 0f;
        float p = 1f - bestDistance / startDistance;
        return p > 1f ? 1f : p > 0f ? p : 0f; // also maps NaN to 0
    }

    /// <summary>Score of a car that has not finished.</summary>
    public static float UnfinishedScore(float bestDistance, float startDistance, int bestTick, int timeLimitTicks) =>
        ProgressFraction(bestDistance, startDistance) - TieBreakWeight * bestTick / timeLimitTicks;

    /// <summary>Score of a car that finished after <paramref name="finishTicks"/> ticks (fractional).</summary>
    public static float FinishedScore(float finishTicks, int timeLimitTicks) =>
        FinishBase + (timeLimitTicks - finishTicks) / timeLimitTicks;

    /// <summary>Generation time limit "auto" (PLAN §3.6): reference time × 2.5, in whole ticks (rounded up).</summary>
    public static int AutoTimeLimitTicks(float referenceSeconds) =>
        Math.Max(1, (int)MathF.Ceiling(referenceSeconds * AutoTimeLimitFactor * RacingSettings.TicksPerSecond));

    /// <summary>The editor's live estimate between Test presses: path length / (0.6 × max speed), seconds.</summary>
    public static float EstimatedSeconds(float pathLength, float maxSpeed) => pathLength / (EstimateSpeedFactor * maxSpeed);

    /// <summary>
    /// Resolves the time limit in ticks: the explicit setting, else auto from the reference time, else (when the
    /// reference driver did not finish) auto from the live estimate.
    /// </summary>
    public static int ResolveTimeLimitTicks(RacingSettings settings, float? referenceSeconds, float pathLength)
    {
        settings = settings.Clamped();
        if (settings.TimeLimitSeconds > 0)
            return (int)MathF.Ceiling(settings.TimeLimitSeconds * RacingSettings.TicksPerSecond);
        float seconds = referenceSeconds ?? EstimatedSeconds(pathLength, settings.MaxSpeed);
        if (!float.IsFinite(seconds)) seconds = RacingSettings.MaxTimeLimitSeconds / AutoTimeLimitFactor;
        return AutoTimeLimitTicks(seconds);
    }
}
