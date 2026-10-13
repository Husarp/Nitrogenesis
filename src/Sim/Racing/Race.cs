using Nitrogenesis.Sim.Core;
using Nitrogenesis.Sim.Map;

namespace Nitrogenesis.Sim.Racing;

/// <summary>One line of the results (PLAN §6.5): the player or a ghost, the finish time or how far it got.</summary>
/// <param name="Name">"You" or the ghost's label.</param>
/// <param name="IsPlayer">The line of the player.</param>
/// <param name="Finished">It reached the finish.</param>
/// <param name="Seconds">Finish time, s (+∞ when it did not finish).</param>
/// <param name="Progress">Progress 0…1 (1 when finished).</param>
/// <param name="Ghost">Index of the ghost in <see cref="Race.Ghosts"/>, −1 for the player.</param>
public readonly record struct RaceStanding(string Name, bool IsPlayer, bool Finished, float Seconds, float Progress, int Ghost)
{
    /// <summary>
    /// Finishers by time (the player first on an exact tie), then the others by progress; equal lines keep their order.
    /// </summary>
    public static List<RaceStanding> Order(IEnumerable<RaceStanding> lines) =>
        lines.OrderByDescending(s => s.Finished).ThenBy(s => s.Finished ? s.Seconds : 0f).ThenByDescending(s => s.Finished ? 0f : s.Progress)
            .ThenByDescending(s => s.IsPlayer).ToList();
}

/// <summary>
/// Race yourself (PLAN §6.5): a <see cref="PlayerRun"/> (countdown, timer, walls block, danger kills, restart) on the
/// physics of the chosen ghosts, with up to <see cref="MaxGhosts"/> <see cref="RaceGhost"/>s played on the same clock,
/// the <see cref="RaceSplits"/> and the results. No Godot: the view reads the devices and draws.
/// </summary>
/// <remarks>
/// The race clock <see cref="Ticks"/> counts the ticks since the start signal while the player drives; it keeps
/// running after the player crashed (the ghosts go on) and stops when the player finishes. Single-threaded.
/// </remarks>
public sealed class Race
{
    /// <summary>Most ghosts at once (the picker and the HUD have room for this many).</summary>
    public const int MaxGhosts = 8;

    private readonly RaceGhost[] _ghosts;

    private Race(RacingTrack track, GhostPhysics physics, RaceGhost[] ghosts)
    {
        Track = track;
        Physics = physics;
        _ghosts = ghosts;
        Run = new PlayerRun(track);
        Splits = new RaceSplits(ghosts.Select(g => Enumerable.Range(0, RaceSplits.Count).Select(g.SplitTick).ToArray()).ToArray());
    }

    /// <summary>
    /// A race on <paramref name="track"/> against <paramref name="ghosts"/> (their order is kept; none = a plain drive).
    /// The player's car gets the physics from <see cref="GhostPhysics.Choose"/>.
    /// </summary>
    public static Race Create(Track track, IReadOnlyList<GhostEntry> ghosts)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(ghosts);
        if (ghosts.Count > MaxGhosts) throw new ArgumentException($"At most {MaxGhosts} ghosts.", nameof(ghosts));
        var physics = GhostPhysics.Choose(ghosts);
        var racing = new RacingTrack(track, physics.Settings);
        return new Race(racing, physics, ghosts.Select(g => new RaceGhost(g, racing)).ToArray());
    }

    public RacingTrack Track { get; }
    public GhostPhysics Physics { get; }
    public PlayerRun Run { get; }
    public RaceSplits Splits { get; }
    public IReadOnlyList<RaceGhost> Ghosts => _ghosts;

    /// <summary>Ticks since the start signal (see the remarks).</summary>
    public int Ticks { get; private set; }

    /// <summary>The ghosts move: the player is driving, or crashed and waiting for the restart.</summary>
    public bool ClockRunning => Run.Phase is PlayerPhase.Driving or PlayerPhase.Crashed;

    /// <summary>One tick with the player's input (see <see cref="PlayerRun.Tick"/>).</summary>
    public void Tick(int keyThrottle, int keySteer, float stickX, float rightTrigger, float leftTrigger)
    {
        var before = Run.Phase;
        Run.Tick(keyThrottle, keySteer, stickX, rightTrigger, leftTrigger);
        After(before);
    }

    /// <summary>One tick with throttle and steer given directly (the debug autopilot).</summary>
    public void Drive(float throttle, float steer)
    {
        var before = Run.Phase;
        Run.Drive(throttle, steer);
        After(before);
    }

    private void After(PlayerPhase before)
    {
        if (before is PlayerPhase.Driving or PlayerPhase.Crashed) Ticks++;
        Splits.Update(Run.Progress, Run.Ticks);
    }

    /// <summary>R at any time: back to the start with a countdown; the ghosts and the splits start again too.</summary>
    public void Restart(int countdownTicks = PlayerRun.RestartCountdownTicks)
    {
        Run.Restart(countdownTicks);
        Ticks = 0;
        Splits.Reset();
    }

    /// <summary>
    /// Ghost <paramref name="i"/>'s pose, drawn the way the player's car is: between the last two ticks by
    /// <paramref name="alpha"/>, the render fraction (0 = the tick before, 1 = the latest). It only moves while the
    /// clock runs.
    /// </summary>
    public void GhostPose(int i, float alpha, out float x, out float y, out float angle)
    {
        if (ClockRunning && Ticks > 0) _ghosts[i].Pose(Ticks - 1, alpha, out x, out y, out angle);
        else _ghosts[i].Pose(Ticks, 0f, out x, out y, out angle);
    }

    public AgentStatus GhostStatus(int i) => _ghosts[i].StatusAt(Ticks);

    /// <summary>Everyone's result, in order (see <see cref="RaceStanding.Order"/>): the player's line is its finish time, or its progress if it has not finished.</summary>
    public List<RaceStanding> Standings()
    {
        bool done = Run.Phase == PlayerPhase.Finished;
        var lines = new List<RaceStanding>
        {
            new("You", true, done, done ? Run.FinishTicks / RacingSettings.TicksPerSecond : float.PositiveInfinity, Run.Progress, -1),
        };
        for (int i = 0; i < _ghosts.Length; i++)
        {
            var g = _ghosts[i];
            lines.Add(new RaceStanding(g.Entry.Label, false, g.Finished, g.Finished ? g.Entry.FinishSeconds : float.PositiveInfinity, g.Progress, i));
        }
        return RaceStanding.Order(lines);
    }
}
