using Nitrogenesis.Sim.Core;
using Nitrogenesis.Sim.Evolution;
using Nitrogenesis.Sim.History;
using Nitrogenesis.Sim.Map;

namespace Nitrogenesis.Sim.Racing;

/// <summary>Outcome of one <see cref="LearnTest"/> run.</summary>
/// <param name="Passed">A car finished within the budget (and, when required, the final best route kept to the road).</param>
/// <param name="FirstFinish">Generation of the first finish, or −1 when no car finished.</param>
/// <param name="GenerationsRun">Generations evaluated.</param>
/// <param name="BestFinishSeconds">Fastest finish of the last generation run, or +∞.</param>
/// <param name="GrassShare">Road check only: the last generation's best car's share of ticks on grass (else null).</param>
/// <param name="Failure">Why it failed, or null.</param>
public sealed record LearnTestResult(bool Passed, int FirstFinish, int GenerationsRun, float BestFinishSeconds, float? GrassShare, string? Failure);

/// <summary>
/// The learning test of PLAN §9 ("learn-suite"): train on a track with default settings and a fixed seed, and
/// require a finishing car within a generation budget. Used by <c>SimBench learn-suite</c> now and by the
/// generator's per-template check later (§3.4, §7).
/// </summary>
/// <remarks>
/// <para>The optional road check is the grass-shortcut regression: training runs the whole budget, and the last
/// generation's best car must be a finisher whose recording spends less than <see cref="MaxGrassShare"/> of its
/// ticks with the centre on grass.</para>
/// <para><b>Random-search control:</b> with <c>randomSearch</c>, every generation is a fresh random population
/// (the same budget of cars, but nothing learned). A track that random search also solves cannot tell working
/// evolution from none, so the learn-suite requires this control to fail on its hard tracks.</para>
/// </remarks>
public static class LearnTest
{
    /// <summary>Largest share of ticks on grass for a route that "stays on the road".</summary>
    public const float MaxGrassShare = 0.05f;

    /// <param name="track">The track.</param>
    /// <param name="budget">Generations allowed; the first finish must come in generation 0 … budget − 1.</param>
    /// <param name="seed">Training seed.</param>
    /// <param name="threads">Simulation threads (results do not depend on it).</param>
    /// <param name="requireRoadRoute">Also run the full budget and apply the road check.</param>
    /// <param name="settings">Racing settings; null = defaults.</param>
    /// <param name="onGeneration">Called after every generation (progress output).</param>
    /// <param name="randomSearch">Control run: replace every bred population with a fresh random one.</param>
    public static LearnTestResult Run(Track track, int budget, ulong seed, int threads = 1, bool requireRoadRoute = false,
        RacingSettings? settings = null, Action<GenerationRecord>? onGeneration = null, bool randomSearch = false)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentOutOfRangeException.ThrowIfLessThan(budget, 1);
        using var training = new RacingTraining(track, settings ?? new RacingSettings(), seed, threads: threads);
        float[] outputBias = RacingSettings.InitialOutputBias();
        int firstFinish = -1, run = 0;
        GenerationRecord? last = null;
        while (run < budget && (firstFinish < 0 || requireRoadRoute))
        {
            last = training.Runner.RunGeneration();
            run++;
            if (firstFinish < 0 && last.Stats.FinishedCount > 0) firstFinish = last.Generation;
            onGeneration?.Invoke(last);
            // The next generation's seed gives every random population its own streams.
            if (randomSearch) training.Runner.Population.Randomize(training.Runner.GenerationSeed(training.Runner.Generation), outputBias);
        }

        float bestSeconds = last!.Stats.BestFinishTicks / RacingSettings.TicksPerSecond;
        if (firstFinish < 0)
            return new(false, -1, run, bestSeconds, null, $"no car finished within {budget} generations");
        if (!requireRoadRoute) return new(true, firstFinish, run, bestSeconds, null, null);

        if (training.Mode.Status(last.Stats.BestIndex) != AgentStatus.Finished)
            return new(false, firstFinish, run, bestSeconds, null, "the last generation's best car did not finish");
        float grass = GrassShare(last.BestRecording, track.Grid);
        return grass < MaxGrassShare
            ? new(true, firstFinish, run, bestSeconds, grass, null)
            : new(false, firstFinish, run, bestSeconds, grass, $"the best route spends {grass:P1} of its ticks on grass");
    }

    /// <summary>
    /// Share of a recording's ticks whose car centre is on a Grass cell: samples 1…Count−1 (the pose after each
    /// tick; sample 0 is the start pose). 0 for a recording without ticks.
    /// </summary>
    public static float GrassShare(Recording recording, Grid grid)
    {
        ArgumentNullException.ThrowIfNull(recording);
        ArgumentNullException.ThrowIfNull(grid);
        int ticks = recording.Count - 1, onGrass = 0;
        if (ticks <= 0) return 0f;
        for (int i = 1; i < recording.Count; i++)
        {
            int x = (int)MathF.Floor(recording.X(i)), y = (int)MathF.Floor(recording.Y(i));
            if (grid.InBounds(x, y) && grid[x, y] == CellType.Grass) onGrass++;
        }
        return (float)onGrass / ticks;
    }
}
