namespace Nitrogenesis.Sim.Core;

/// <summary>
/// The double-buffered snapshot the renderer reads (PLAN §2.1, §4): <c>prev</c> and <c>curr</c> agent states,
/// each with its tick index, generation id and alive flags (<see cref="AgentSnapshot.Status"/>).
/// </summary>
/// <remarks>
/// The simulation publishes only while its workers are idle (at a phase end), so the mode's arrays are stable
/// while they are copied. Publishing and reading take a short lock: a reader copies both buffers into its own
/// (<see cref="CopyTo"/>) and never sees a half-written snapshot. Neither side allocates.
/// </remarks>
public sealed class SnapshotBuffer
{
    private readonly object _lock = new();
    private AgentSnapshot _prev, _curr;

    public SnapshotBuffer(int capacity)
    {
        _prev = new AgentSnapshot(capacity);
        _curr = new AgentSnapshot(capacity);
    }

    public int Capacity => _curr.Capacity;

    /// <summary>Snapshots published so far (0 = nothing to read yet).</summary>
    public long Version { get; private set; }

    /// <summary>Makes the current snapshot the previous one and writes the mode's state as the new current one.</summary>
    public void Publish(IAgentMode mode, long tick, int generationId)
    {
        ArgumentNullException.ThrowIfNull(mode);
        if (mode.AgentCount > Capacity) throw new ArgumentException("The snapshot is too small for the mode.", nameof(mode));
        lock (_lock)
        {
            (_prev, _curr) = (_curr, _prev);
            mode.WriteSnapshot(AgentRange.All(mode.AgentCount), _curr);
            _curr.Tick = tick;
            _curr.GenerationId = generationId;
            Version++;
        }
    }

    /// <summary>
    /// Copies the latest pair into the reader's own buffers (same capacity). Before the second publish
    /// <paramref name="prev"/> gets the same data as <paramref name="curr"/>. Returns the version copied (0 = none yet).
    /// </summary>
    public long CopyTo(AgentSnapshot prev, AgentSnapshot curr)
    {
        ArgumentNullException.ThrowIfNull(prev);
        ArgumentNullException.ThrowIfNull(curr);
        if (prev.Capacity != Capacity || curr.Capacity != Capacity) throw new ArgumentException("Reader buffers must match the capacity.");
        lock (_lock)
        {
            if (Version == 0) return 0;
            Copy(Version == 1 ? _curr : _prev, prev);
            Copy(_curr, curr);
            return Version;
        }
    }

    private static void Copy(AgentSnapshot from, AgentSnapshot to)
    {
        Array.Copy(from.X, to.X, from.Capacity);
        Array.Copy(from.Y, to.Y, from.Capacity);
        Array.Copy(from.Angle, to.Angle, from.Capacity);
        Array.Copy(from.Status, to.Status, from.Capacity);
        to.Tick = from.Tick;
        to.GenerationId = from.GenerationId;
    }
}
