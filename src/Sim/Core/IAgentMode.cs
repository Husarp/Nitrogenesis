using Nitrogenesis.Sim.History;

namespace Nitrogenesis.Sim.Core;

/// <summary>
/// A game mode as seen by the generic machinery (evolution, history, scheduler, replay, HUD; PLAN §2.1).
/// Racing implements it in M1, the platformer in M10.
/// </summary>
/// <remarks>
/// <para>State lives in per-agent arrays (struct of arrays). Agents never interact, so methods called with
/// disjoint <see cref="AgentRange"/>s may run on different threads at the same time. No method allocates.</para>
/// <para>One tick for one agent = <see cref="Sense"/> (write its inputs) → the policy fills its outputs →
/// <see cref="Advance"/> (apply them). <see cref="Step"/> does this K times per agent with no barrier.
/// Agents that are done do not change any more.</para>
/// </remarks>
public interface IAgentMode
{
    int AgentCount { get; }

    /// <summary>Sensor values per agent (the brain's input size).</summary>
    int InputCount { get; }

    /// <summary>Values per agent the mode reads from <see cref="Outputs"/> each tick.</summary>
    int OutputCount { get; }

    /// <summary>Inputs of all agents, agent-major: agent i uses [i·InputCount, (i+1)·InputCount).</summary>
    float[] Inputs { get; }

    /// <summary>Outputs of all agents, agent-major: agent i uses [i·OutputCount, (i+1)·OutputCount).</summary>
    float[] Outputs { get; }

    /// <summary>Puts the agents back at the start with a fresh run (tick 0, no score).</summary>
    void Reset(AgentRange range);

    /// <summary>Writes the current inputs of every running agent in the range.</summary>
    void Sense(AgentRange range);

    /// <summary>Advances every running agent in the range by one tick, using its values in <see cref="Outputs"/>.</summary>
    void Advance(AgentRange range);

    /// <summary>Runs <paramref name="ticks"/> ticks (sense → policy → advance) for every running agent in the range.</summary>
    void Step(AgentRange range, int ticks, IAgentPolicy policy);

    /// <summary>Ticks this agent has run since its reset.</summary>
    int Tick(int agent);

    AgentStatus Status(int agent);

    /// <summary>True once the agent's run is over (any status except Running).</summary>
    bool IsDone(int agent);

    /// <summary>Fitness of the agent's run so far (final once it is done). Higher is better.</summary>
    float Score(int agent);

    /// <summary>
    /// How far the agent got towards the goal in the mode's distance units (racing: cells along the distance
    /// field, start distance − best distance; the full start distance once finished). Used for the stagnation
    /// check (PLAN §3.7) and generation stats.
    /// </summary>
    float ProgressDistance(int agent);

    /// <summary>Finish time in ticks with sub-tick precision; only meaningful when the status is Finished.</summary>
    float FinishTicks(int agent);

    /// <summary>Copies position, heading and status of the range into the same slots of <paramref name="target"/>.</summary>
    void WriteSnapshot(AgentRange range, AgentSnapshot target);

    /// <summary>Appends the agent's current pose (position and heading) to a trajectory recording.</summary>
    void Record(int agent, Recording recording);
}
