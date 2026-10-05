namespace Nitrogenesis.Sim.Map;

/// <summary>
/// A movement graph over grid cells, seen backwards, for <see cref="Dijkstra"/>. Nodes are cell indices
/// (y·width + x). Distances are computed outward from the goal, so the graph lists for each node the
/// cells an agent could move <em>from</em> to reach it, with the cost of that move.
/// </summary>
/// <remarks>
/// This is the pluggable neighbour/edge function of PLAN §3.2. Racing uses <see cref="RacingGraph"/>
/// (8 neighbours, symmetric); the platformer (M10) will supply a directed jump graph.
/// Implement it as a struct so the generic Dijkstra is specialised and the calls are inlined.
/// </remarks>
public interface IReverseGraph
{
    /// <summary>Upper bound on the number of edges <see cref="GetIncoming"/> can return for one node.</summary>
    int MaxIncoming { get; }

    /// <summary>
    /// Writes the predecessors of <paramref name="node"/> and the cost to travel from each of them to
    /// <paramref name="node"/>, and returns how many were written. Costs must be positive (or +∞ for "no edge").
    /// </summary>
    int GetIncoming(int node, Span<int> from, Span<double> cost);
}
