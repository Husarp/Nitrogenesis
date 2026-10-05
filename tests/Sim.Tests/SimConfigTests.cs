using Nitrogenesis.Sim;
using Nitrogenesis.Sim.Brain;
using Nitrogenesis.Sim.Core;
using Nitrogenesis.Sim.Racing;

public class SimConfigTests
{
    private const string SprintHash = "65ef250c5d5e6f41be8bd1cebb1285984dbf9029d60c7c66fdb5fc8fb33c98f8";

    private static SimConfig Config(RacingSettings? s = null, BrainShape? brain = null, int timeLimit = 1914, string track = SprintHash, int simVersion = SimInfo.SimVersion)
    {
        s ??= new RacingSettings();
        return new SimConfig(track, s, brain ?? BrainShape.Default(s.InputCount, RacingSettings.OutputCount), timeLimit, simVersion);
    }

    /// <summary>
    /// Pinned hash of the default config on sprint.track. Session segments are keyed by it: a change splits
    /// every saved session into a new segment, so only change it on purpose (with a SimVersion bump).
    /// </summary>
    public const string ExpectedDefaultHash = "6d5d2b17fd1ca5e12b9ebb8c6d322c0e81a25d4cdf997637ceed9e4c66ec954a";

    [Fact]
    public void DefaultHashIsPinned() => Assert.Equal(ExpectedDefaultHash, Config().Hash);

    [Fact]
    public void EqualConfigsHashEqual() => Assert.Equal(Config().Hash, Config(new RacingSettings()).Hash);

    public static TheoryData<string, RacingSettings> SettingChanges => new()
    {
        { "max speed", new RacingSettings { MaxSpeed = 19 } },
        { "acceleration", new RacingSettings { Acceleration = 15 } },
        { "turn rate", new RacingSettings { TurnRateDeg = 201 } },
        { "grass speed", new RacingSettings { GrassSpeedFraction = 0.5f } },
        { "grass grip", new RacingSettings { GrassGrip = 0.5f } },
        { "walls bounce", new RacingSettings { Walls = WallMode.Bounce } },
        { "walls kill", new RacingSettings { Walls = WallMode.Kill } },
        { "ray count", new RacingSettings { RayCount = 9 } },
        { "ray spread", new RacingSettings { RaySpreadDeg = 200 } },
        { "ray range", new RacingSettings { RayRange = 31 } },
        { "memory inputs", new RacingSettings { MemoryInputs = false } },
        { "direction hint", new RacingSettings { DirectionHint = true } },
        { "stall time", new RacingSettings { StallSeconds = 4 } },
    };

    [Theory]
    [MemberData(nameof(SettingChanges))]
    public void EveryRelevantSettingChangesTheHash(string what, RacingSettings changed)
    {
        // Keep the brain shape fixed so only the setting itself differs (input count is covered separately).
        var brain = BrainShape.Default(22, 2);
        Assert.NotEqual(Config(brain: brain).Hash, Config(changed, brain).Hash);
        Assert.False(string.IsNullOrEmpty(what));
    }

    [Fact]
    public void TrackBrainTimeLimitAndSimVersionChangeTheHash()
    {
        string baseHash = Config().Hash;
        string otherTrack = "7eeb245f69294a7b6216ce29748025629a1f88ca24cfbc6a1687e4ec2ffe199f";
        Assert.NotEqual(baseHash, Config(track: otherTrack).Hash);
        Assert.NotEqual(baseHash, Config(timeLimit: 1915).Hash);
        Assert.NotEqual(baseHash, Config(simVersion: SimInfo.SimVersion + 1).Hash);
        Assert.NotEqual(baseHash, Config(brain: new BrainShape(22, 16, 0, 2)).Hash);
        Assert.NotEqual(baseHash, Config(brain: new BrainShape(22, 12, 8, 2)).Hash);
        Assert.NotEqual(baseHash, Config(brain: new BrainShape(23, 12, 0, 2)).Hash);
    }

    [Fact]
    public void BrainShapeEnforcesLimits()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BrainShape(22, 3, 0, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BrainShape(22, 12, 33, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BrainShape(0, 12, 0, 2));
        Assert.Equal(new BrainShape(22, 12, 0, 2), BrainShape.Default(22, 2));
    }
}
