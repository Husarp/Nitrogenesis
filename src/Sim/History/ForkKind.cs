namespace Nitrogenesis.Sim.History;

/// <summary>How a branch's first population was made (PLAN §5, "Fork").</summary>
public enum ForkKind
{
    /// <summary>The first branch: a random generation 0.</summary>
    Root = 0,
    /// <summary>"Fork from generation X": the full population of the parent's generation X.</summary>
    Full = 1,
    /// <summary>"Fork from best only": the best genome of the parent's generation X plus mutated copies of it.</summary>
    BestOnly = 2,
}
