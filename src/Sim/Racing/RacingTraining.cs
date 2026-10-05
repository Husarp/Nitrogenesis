using Nitrogenesis.Sim.Brain;
using Nitrogenesis.Sim.Core;
using Nitrogenesis.Sim.Evolution;
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
    public RacingTraining(Track track, RacingSettings settings, ulong seed, EvolutionSettings? evolution = null,
        int populationSize = EvolutionSettings.DefaultPopulationSize, int threads = 1,
        int hidden1 = BrainShape.DefaultHidden, int hidden2 = 0)
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
            evolution ?? new EvolutionSettings(), threads);
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
