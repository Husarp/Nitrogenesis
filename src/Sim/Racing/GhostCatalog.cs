using Nitrogenesis.Sim.History;
using Nitrogenesis.Sim.Io;

namespace Nitrogenesis.Sim.Racing;

/// <summary>
/// One saved generation's best car that can race as a ghost (PLAN §6.5): where it came from, the physics of its
/// segment and its recording (kept encoded; <see cref="DecodeRecording"/> when the race starts).
/// </summary>
/// <param name="SessionName">The session it was trained in.</param>
/// <param name="Branch">Its branch ("main" or a fork).</param>
/// <param name="Generation">The generation whose best car it is.</param>
/// <param name="Segment">The segment (SimConfig) the generation ran in.</param>
/// <param name="SavedAtUtc">When the session was last saved (for ordering the list).</param>
/// <param name="SimVersion">SimVersion the session was trained with.</param>
/// <param name="Physics">The racing settings of the segment (the physics the recording was driven with).</param>
/// <param name="FinishTicks">Finish time in ticks at sub-tick precision, +∞ when the best car did not finish.</param>
/// <param name="RecordingBytes">The best car's recording, encoded (<see cref="Recording.Encode"/>).</param>
public sealed record GhostEntry(string SessionName, string Branch, int Generation, int Segment, DateTime SavedAtUtc, int SimVersion,
    RacingSettings Physics, float FinishTicks, byte[] RecordingBytes)
{
    public bool Finished => float.IsFinite(FinishTicks);

    public float FinishSeconds => FinishTicks / RacingSettings.TicksPerSecond;

    /// <summary>"gen 12 · session", with the branch in brackets when it is not the main one (the generation first: the session name is long and gets cut).</summary>
    public string Label => Branch == Session.RootBranch ? $"gen {Generation} · {SessionName}" : $"gen {Generation} [{Branch}] · {SessionName}";

    public Recording DecodeRecording() => Recording.Decode(RecordingBytes);
}

/// <summary>The physics a race is driven with, and whether the chosen ghosts disagreed about it.</summary>
/// <param name="Settings">The settings the player's car uses.</param>
/// <param name="Mismatch">Two or more of the ghosts come from different physics.</param>
/// <param name="Warning">Text for the screen when <paramref name="Mismatch"/>, else null.</param>
public readonly record struct GhostPhysics(RacingSettings Settings, bool Mismatch, string? Warning)
{
    /// <summary>
    /// PLAN §6.5: you drive with the physics of the chosen ghosts' segment; ghosts from different physics give a
    /// warning and the first ghost's physics is used. No ghosts: the default physics.
    /// </summary>
    public static GhostPhysics Choose(IReadOnlyList<GhostEntry> ghosts)
    {
        ArgumentNullException.ThrowIfNull(ghosts);
        if (ghosts.Count == 0) return new GhostPhysics(new RacingSettings(), false, null);
        var first = ghosts[0];
        bool mismatch = ghosts.Any(g => !SamePhysics(first, g));
        return new GhostPhysics(first.Physics, mismatch,
            mismatch ? $"The chosen ghosts were driven with different physics. You drive with the physics of the first one ({first.Label})." : null);
    }

    /// <summary>
    /// Same physics: the same SimVersion and the settings that move a car (speed, acceleration, turn rate, grass,
    /// walls). Sensors, stall time and the time limit do not matter to a player.
    /// </summary>
    public static bool SamePhysics(GhostEntry a, GhostEntry b) =>
        a.SimVersion == b.SimVersion && a.Physics.MaxSpeed == b.Physics.MaxSpeed && a.Physics.Acceleration == b.Physics.Acceleration
        && a.Physics.TurnRateDeg == b.Physics.TurnRateDeg && a.Physics.GrassSpeedFraction == b.Physics.GrassSpeedFraction
        && a.Physics.GrassGrip == b.Physics.GrassGrip && a.Physics.Walls == b.Physics.Walls;
}

/// <summary>
/// Finds the ghosts a track can race against (PLAN §6.5): the best car of every saved generation, in any session and
/// segment whose track hash is the track's (<see cref="Nitrogenesis.Sim.Map.TrackHash"/>: the name does not count).
/// </summary>
public static class GhostCatalog
{
    /// <summary>Snapshots are not needed for ghosts: a loaded session keeps little of them.</summary>
    private const long LoadMemoryLimit = 64L << 20;

    /// <summary>
    /// The ghosts of every session in <paramref name="store"/> that has <paramref name="trackHash"/>, newest save
    /// first. A session that cannot be read is skipped and named in <paramref name="problems"/>.
    /// </summary>
    public static List<GhostEntry> Scan(SessionStore store, string trackHash, ICollection<string>? problems = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        var result = new List<GhostEntry>();
        foreach (var info in store.List())
        {
            if (!info.CanOpen) continue;
            try
            {
                var folder = new SessionFolder(info.Path);
                if (!folder.LoadHeader().Segments.Exists(s => s.Config.TrackHash == trackHash)) continue; // cheap test before the history is read
                result.AddRange(FromSession(info.Name, info.SavedAtUtc, Session.Load(folder, LoadMemoryLimit), trackHash));
            }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                problems?.Add($"{info.Name}: {e.Message}");
            }
        }
        return result;
    }

    /// <summary>
    /// The ghosts of one session: per branch (main first) its stored generations, newest first, that ran in a segment
    /// of this track and have a recording. A fork's own history starts where it was forked, so no generation appears twice.
    /// </summary>
    public static List<GhostEntry> FromSession(string name, DateTime savedAtUtc, Session session, string trackHash)
    {
        ArgumentNullException.ThrowIfNull(session);
        var result = new List<GhostEntry>();
        lock (session.SyncRoot)
        {
            foreach (var branch in session.History.Branches)
            {
                int first = result.Count;
                foreach (var entry in branch.Entries)
                {
                    if (entry.RecordingBytes.Length == 0) continue;
                    var segment = session.SegmentAt(branch.Name, entry.Generation);
                    if (segment.Config.TrackHash != trackHash || segment.Config.Mode is not RacingSettings physics) continue;
                    float finish = entry.Stats.FinishedCount > 0 ? entry.Stats.BestFinishTicks : float.PositiveInfinity;
                    result.Add(new GhostEntry(name, branch.Name, entry.Generation, segment.Id, savedAtUtc, session.SimVersion, physics, finish, entry.RecordingBytes));
                }
                result.Reverse(first, result.Count - first);
            }
        }
        return result;
    }
}
