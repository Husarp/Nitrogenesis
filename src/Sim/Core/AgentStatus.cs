namespace Nitrogenesis.Sim.Core;

/// <summary>Where an agent's run stands. Every value except <see cref="Running"/> is final until the next reset.</summary>
public enum AgentStatus : byte
{
    Running = 0,
    /// <summary>Reached the goal (racing: the car centre entered a Finish cell).</summary>
    Finished = 1,
    /// <summary>Destroyed (racing: touched danger, or a wall in "kill" walls mode).</summary>
    Crashed = 2,
    /// <summary>Made no progress for the stall time.</summary>
    Stalled = 3,
    /// <summary>Ran out of the generation time limit.</summary>
    TimedOut = 4,
}
