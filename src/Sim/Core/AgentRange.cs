namespace Nitrogenesis.Sim.Core;

/// <summary>A contiguous slice of agents [<see cref="Start"/>, <see cref="End"/>), e.g. one worker's chunk.</summary>
public readonly record struct AgentRange(int Start, int Count)
{
    public int End => Start + Count;

    /// <summary>All agents of a mode.</summary>
    public static AgentRange All(int agentCount) => new(0, agentCount);
}
