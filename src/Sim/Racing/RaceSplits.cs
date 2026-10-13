namespace Nitrogenesis.Sim.Racing;

/// <summary>How the player's split compares with one ghost's: <see cref="Seconds"/> = player − ghost, so negative = ahead.</summary>
/// <param name="Seconds">The difference in seconds (0 when the ghost never got there).</param>
/// <param name="GhostReached">False when the ghost never reached the split (it crashed or stalled first): the player is ahead.</param>
public readonly record struct SplitDelta(float Seconds, bool GhostReached)
{
    public bool Ahead => !GhostReached || Seconds < 0f;
}

/// <summary>
/// Split times of a race (PLAN §6.5): the tick at which the player's progress first crossed 25 / 50 / 75 %, compared
/// with each ghost's. Progress only ever grows (best distance so far), so a split is set once and never changes until
/// <see cref="Reset"/> (a restart).
/// </summary>
public sealed class RaceSplits
{
    public const int Count = 3;

    private readonly int[] _player = new int[Count];
    private readonly int[][] _ghosts;

    /// <param name="ghostTicks">Per ghost: the tick it reached each split, −1 when it never did.</param>
    public RaceSplits(IReadOnlyList<int[]> ghostTicks)
    {
        ArgumentNullException.ThrowIfNull(ghostTicks);
        _ghosts = ghostTicks.Select(t => t.Length == Count ? t : throw new ArgumentException($"A ghost needs {Count} split ticks.", nameof(ghostTicks))).ToArray();
        Reset();
    }

    /// <summary>The share of the track split <paramref name="split"/> is at: 0.25, 0.5, 0.75.</summary>
    public static float Fraction(int split) => (split + 1) * 0.25f;

    public int GhostCount => _ghosts.Length;

    /// <summary>Splits the player has crossed so far.</summary>
    public int Crossed { get; private set; }

    public void Reset()
    {
        Array.Fill(_player, -1);
        Crossed = 0;
    }

    /// <summary>The player's tick at split <paramref name="split"/>, or −1 if not crossed yet.</summary>
    public int PlayerTick(int split) => _player[split];

    /// <summary>Records every split that <paramref name="progress"/> (0…1) has reached and that is not set yet, at <paramref name="tick"/>.</summary>
    public void Update(float progress, int tick)
    {
        while (Crossed < Count && progress >= Fraction(Crossed))
            _player[Crossed++] = tick;
    }

    /// <summary>The player against ghost <paramref name="ghost"/> at <paramref name="split"/>, or null while the player has not crossed it.</summary>
    public SplitDelta? Delta(int split, int ghost)
    {
        if (_player[split] < 0) return null;
        int g = _ghosts[ghost][split];
        return g < 0 ? new SplitDelta(0f, false) : new SplitDelta((float)(_player[split] - g) / RacingSettings.TicksPerSecond, true);
    }
}
