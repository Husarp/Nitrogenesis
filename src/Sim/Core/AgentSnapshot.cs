namespace Nitrogenesis.Sim.Core;

/// <summary>
/// What the renderer needs of every agent at one tick (PLAN §2.1, "Snapshot"). The scheduler keeps two of these
/// (prev and curr) and fills them with <see cref="IAgentMode.WriteSnapshot"/>; the render thread only reads.
/// </summary>
public sealed class AgentSnapshot
{
    public AgentSnapshot(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        X = new float[capacity];
        Y = new float[capacity];
        Angle = new float[capacity];
        Status = new AgentStatus[capacity];
    }

    public int Capacity => X.Length;

    /// <summary>Agent position in world units (cells).</summary>
    public float[] X { get; }
    public float[] Y { get; }
    /// <summary>Agent heading in radians, [0, 2π) (0 = +x, positive towards +y).</summary>
    public float[] Angle { get; }
    /// <summary>Alive flag and outcome; only <see cref="AgentStatus.Running"/> agents are alive.</summary>
    public AgentStatus[] Status { get; }

    /// <summary>Tick index the snapshot was taken at (set by the scheduler).</summary>
    public long Tick { get; set; }
    /// <summary>Generation the snapshot belongs to (set by the scheduler; changes disable interpolation).</summary>
    public int GenerationId { get; set; }
}
