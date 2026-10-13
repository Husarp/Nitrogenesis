using Nitrogenesis.Sim.History;

namespace Nitrogenesis.Sim.Io;

/// <summary>
/// The history records of one chunk window (generations 25w … 25w+24 of a branch) that a save writes, captured
/// when the save was planned. Entries are immutable, so the save thread can write them while training goes on.
/// </summary>
/// <param name="Branch">Branch name.</param>
/// <param name="Window">Chunk window index (generation / <see cref="SessionFolder.ChunkSize"/>).</param>
/// <param name="First">The branch's first generation (files never contribute generations before it).</param>
/// <param name="End">The branch's history end in this save (exclusive): generations from here on are not committed.</param>
/// <param name="Entries">The window's records below <paramref name="End"/>, oldest first.</param>
public sealed record WindowWrite(string Branch, int Window, int First, int End, IReadOnlyList<HistoryEntry> Entries);

/// <summary>
/// Everything one save writes, copied from the session at one moment (PLAN §5.1: "from a copied snapshot"): the
/// header (with its own copy of the current population) and the chunk windows that are new or were thinned since
/// the last save. <see cref="SessionFolder.Save"/> writes it; <see cref="Session.MarkSaved"/> records it.
/// </summary>
public sealed class SavePlan
{
    internal SavePlan(SessionHeader header, IReadOnlyList<WindowWrite> windows, IReadOnlyList<string> newBranches, long stamp)
    {
        Header = header;
        Windows = windows;
        NewBranches = newBranches;
        Stamp = stamp;
    }

    public SessionHeader Header { get; }
    public IReadOnlyList<WindowWrite> Windows { get; }
    /// <summary>Branches never saved before: a leftover folder of that name (from a save that never committed) is cleared first.</summary>
    public IReadOnlyList<string> NewBranches { get; }
    /// <summary>The session's change counter when the plan was made.</summary>
    internal long Stamp { get; }
    /// <summary>
    /// The session was loaded from the backup header because header.json was damaged: the save deletes header.json
    /// before writing the new one, so the damaged file never replaces the good backup (whichever
    /// <see cref="SessionFolder"/> object writes the save).
    /// </summary>
    public bool ReplacesDamagedHeader { get; init; }
}
