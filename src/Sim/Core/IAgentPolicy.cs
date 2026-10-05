namespace Nitrogenesis.Sim.Core;

/// <summary>
/// Decides an agent's outputs from its sensor inputs: the brains during training, a scripted driver, or a
/// fixed input sequence in tests. <see cref="IAgentMode.Step"/> calls it once per agent per tick.
/// </summary>
/// <remarks>
/// Implementations must not allocate and must be safe to call concurrently for different agents
/// (workers own disjoint agent ranges, PLAN §2.1).
/// </remarks>
public interface IAgentPolicy
{
    void Act(int agent, ReadOnlySpan<float> inputs, Span<float> outputs);
}
