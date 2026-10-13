using Nitrogenesis.Sim.Brain;
using Nitrogenesis.Sim.Core;
using Nitrogenesis.Sim.Evolution;
using Nitrogenesis.Sim.History;
using Nitrogenesis.Sim.Io;
using Nitrogenesis.Sim.Map;

namespace Nitrogenesis.Sim.Racing;

/// <summary>
/// Wires up racing training on one track: the prepared track, the resolved time limit (auto = reference time ×
/// 2.5, PLAN §3.6), the mode with one car per genome, its <see cref="SimConfig"/> and the
/// <see cref="GenerationRunner"/>.
/// </summary>
public sealed class RacingTraining : IDisposable
{
    /// <param name="track">The track.</param>
    /// <param name="settings">Racing settings (clamped).</param>
    /// <param name="seed">Training seed.</param>
    /// <param name="evolution">Evolution settings; null = defaults.</param>
    /// <param name="populationSize">Cars per generation.</param>
    /// <param name="threads">Simulation worker threads.</param>
    /// <param name="hidden1">First hidden layer size (4…32).</param>
    /// <param name="hidden2">Second hidden layer size (4…32), or 0 for none.</param>
    /// <param name="population">Population to start from (a new segment carries the current one over), or null for a random generation <paramref name="generation"/>.</param>
    /// <param name="generation">Generation number of the starting population.</param>
    /// <param name="populationParams">Parameters that bred <paramref name="population"/>.</param>
    public RacingTraining(Track track, RacingSettings settings, ulong seed, EvolutionSettings? evolution = null,
        int populationSize = EvolutionSettings.DefaultPopulationSize, int threads = 1,
        int hidden1 = BrainShape.DefaultHidden, int hidden2 = 0,
        Population? population = null, int generation = 0, EvolutionParams? populationParams = null)
    {
        Track = new RacingTrack(track, settings);
        if (Track.Settings.TimeLimitSeconds <= 0)
        {
            var reference = ReferenceDriver.Run(Track);
            if (reference.Finished) ReferenceSeconds = reference.TimeSeconds;
        }
        TimeLimitTicks = RacingFitness.ResolveTimeLimitTicks(Track.Settings, ReferenceSeconds, Track.StartDistance);
        Mode = new RacingMode(Track, populationSize, TimeLimitTicks);
        Shape = new BrainShape(Mode.InputCount, hidden1, hidden2, Mode.OutputCount);
        Config = new SimConfig(TrackHash.Compute(track), Track.Settings, Shape, TimeLimitTicks);
        Runner = new GenerationRunner(Mode, Shape, RacingSettings.InitialOutputBias(), seed,
            evolution ?? new EvolutionSettings(), threads, population, generation, populationParams);
    }

    /// <summary>
    /// The training that continues a session (resume, fork, branch switch): its current population and generation
    /// under the current segment's config and the active branch's seed, with the stagnation check rebuilt by
    /// replaying the stored stats of the current segment run (PLAN §3.7). Call it on the thread that will train.
    /// </summary>
    /// <exception cref="InvalidOperationException">The session is read-only (another SimVersion).</exception>
    public static RacingTraining ResumeFrom(Session session, int threads)
    {
        ArgumentNullException.ThrowIfNull(session);
        PopulationEntry current;
        SegmentEntry segment;
        ulong seed;
        EvolutionSettings evolution;
        List<GenerationStats> replay;
        lock (session.SyncRoot)
        {
            if (session.ReadOnly) throw new InvalidOperationException(session.Compatibility.Message);
            current = session.Current;
            segment = session.SegmentAt(current.Branch, current.Generation);
            seed = session.Branch(current.Branch).Seed;
            evolution = session.Evolution;
            replay = session.SegmentRunStats(current.Branch, current.Generation);
        }
        var training = new RacingTraining(segment.Track, segment.Config, seed, evolution, current.Population.Size, threads,
            current.Population, current.Generation, current.Params);
        foreach (var stats in replay)
        {
            training.Runner.Stagnation.Observe(stats.BestProgress, stats.BestFinishTicks, evolution);
            training.Runner.Stagnation.ConsumeBoost(evolution);
        }
        return training;
    }

    /// <summary>
    /// Re-creates the training of a stored segment (resume, fork, rebuild): the time limit comes from
    /// <paramref name="config"/> (no reference run), and the result must reproduce its hash exactly.
    /// </summary>
    /// <param name="track">The segment's track copy.</param>
    /// <param name="config">The segment's config (racing settings, brain, resolved time limit).</param>
    /// <param name="seed">The branch seed.</param>
    /// <param name="evolution">Evolution settings; null = defaults.</param>
    /// <param name="populationSize">Cars per generation.</param>
    /// <param name="threads">Simulation worker threads.</param>
    /// <param name="population">Population to start from, or null for a random generation <paramref name="generation"/>.</param>
    /// <param name="generation">Generation number of the starting population.</param>
    /// <param name="populationParams">Parameters that bred <paramref name="population"/>.</param>
    /// <exception cref="InvalidOperationException">The config cannot be reproduced (another SimVersion or track).</exception>
    public RacingTraining(Track track, SimConfig config, ulong seed, EvolutionSettings? evolution, int populationSize, int threads,
        Population? population = null, int generation = 0, EvolutionParams? populationParams = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.Mode is not RacingSettings settings) throw new ArgumentException($"Not a racing config (mode '{config.Mode.ModeId}').", nameof(config));
        Track = new RacingTrack(track, settings);
        TimeLimitTicks = config.TimeLimitTicks;
        Mode = new RacingMode(Track, populationSize, TimeLimitTicks);
        Shape = new BrainShape(Mode.InputCount, config.Brain.Hidden1, config.Brain.Hidden2, Mode.OutputCount);
        Config = new SimConfig(TrackHash.Compute(track), Track.Settings, Shape, TimeLimitTicks);
        if (Config.Hash != config.Hash)
            throw new InvalidOperationException("The stored training settings cannot be reproduced by this version (config hash differs).");
        Runner = new GenerationRunner(Mode, Shape, RacingSettings.InitialOutputBias(), seed, evolution ?? new EvolutionSettings(), threads,
            population, generation, populationParams);
    }

    public RacingTrack Track { get; }
    /// <summary>The reference driver's finish time when the time limit is auto and it finished, else null.</summary>
    public float? ReferenceSeconds { get; }
    public int TimeLimitTicks { get; }
    public RacingMode Mode { get; }
    public BrainShape Shape { get; }
    public SimConfig Config { get; }
    public GenerationRunner Runner { get; }

    public void Dispose() => Runner.Dispose();
}
