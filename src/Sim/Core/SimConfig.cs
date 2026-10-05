using Nitrogenesis.Sim.Brain;

namespace Nitrogenesis.Sim.Core;

/// <summary>
/// Everything that affects a trajectory or a score (PLAN §5): the map (by track hash), the mode settings
/// (physics, sensors, walls mode, stall time, fitness constants), the brain shape, the resolved time limit
/// and <see cref="SimInfo.SimVersion"/>. Its <see cref="Hash"/> identifies a history segment: any change
/// starts a new one.
/// </summary>
/// <remarks>
/// Hashed layout, in order: tag "nitrogenesis.simconfig", layout 1, SimVersion, track hash, mode id, the mode
/// settings (<see cref="IModeSettings.WriteHash"/>), brain inputs / hidden 1 / hidden 2 / outputs, time limit
/// in ticks. Each value is named (<see cref="ConfigHashWriter"/>). The hash is pinned by a test.
/// </remarks>
public sealed class SimConfig
{
    private const int HashLayout = 1;

    public SimConfig(string trackHash, IModeSettings mode, BrainShape brain, int timeLimitTicks, int simVersion = SimInfo.SimVersion)
    {
        ArgumentException.ThrowIfNullOrEmpty(trackHash);
        ArgumentNullException.ThrowIfNull(mode);
        ArgumentOutOfRangeException.ThrowIfLessThan(timeLimitTicks, 1);
        TrackHash = trackHash;
        Mode = mode;
        Brain = brain;
        TimeLimitTicks = timeLimitTicks;
        SimVersion = simVersion;
        Hash = ComputeHash();
    }

    public string TrackHash { get; }
    public IModeSettings Mode { get; }
    public BrainShape Brain { get; }
    /// <summary>The generation time limit after "auto" is resolved (PLAN §3.6), in ticks.</summary>
    public int TimeLimitTicks { get; }
    public int SimVersion { get; }

    /// <summary>SHA-256 of the config, 64 lowercase hex digits.</summary>
    public string Hash { get; }

    private string ComputeHash()
    {
        using var w = new ConfigHashWriter();
        w.String("tag", "nitrogenesis.simconfig");
        w.Int("layout", HashLayout);
        w.Int("simVersion", SimVersion);
        w.String("trackHash", TrackHash);
        w.String("mode", Mode.ModeId);
        Mode.WriteHash(w);
        w.Int("brain.inputs", Brain.Inputs);
        w.Int("brain.hidden1", Brain.Hidden1);
        w.Int("brain.hidden2", Brain.Hidden2);
        w.Int("brain.outputs", Brain.Outputs);
        w.Int("timeLimitTicks", TimeLimitTicks);
        return w.Finish();
    }
}
