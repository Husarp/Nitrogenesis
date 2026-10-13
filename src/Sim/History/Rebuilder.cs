using Nitrogenesis.Sim.Evolution;
using Nitrogenesis.Sim.Io;
using Nitrogenesis.Sim.Racing;
using Nitrogenesis.Sim.Rng;

namespace Nitrogenesis.Sim.History;

/// <summary>
/// Rebuilds the full population of any stored generation (PLAN §5: "any generation can be rebuilt deterministically
/// from the nearest earlier snapshot plus the stored seeds/parameters"), so every generation can be forked or
/// watched whole, even after its snapshot was thinned.
/// </summary>
/// <remarks>
/// <para>It walks back through the lineage to the nearest population it has: a snapshot, the session's current
/// population, or a branch's first population (generation 0 from the branch seed; a best-only fork from the
/// parent's best genome; a full fork is the parent's population, so the walk continues in the parent). Then it
/// re-evaluates each generation under its own segment's config for exactly the ticks it ran, and breeds the next
/// with the stored seed and parameters.</para>
/// <para>Every re-evaluated generation is checked against its record (stats, best genome and ticks, bit for bit);
/// a difference throws, so a rebuild never silently returns another population. Same SimVersion only.</para>
/// </remarks>
public static class Rebuilder
{
    private sealed record Step(string Branch, ulong BranchSeed, int Generation, SegmentEntry Segment, HistoryEntry Evaluated, EvolutionParams BreedParams);

    private sealed record Plan(Population Start, int StartGeneration, List<Step> Steps);

    /// <summary>Generations that must be re-evaluated to rebuild <paramref name="generation"/> (0 when a snapshot is at hand).</summary>
    public static int StepsNeeded(Session session, string branch, int generation) => MakePlan(session, branch, generation).Steps.Count;

    /// <summary>
    /// The full population of generation <paramref name="generation"/> in <paramref name="branch"/>'s lineage (a new
    /// object the caller owns). <paramref name="progress"/> gets (generations done, generations needed).
    /// </summary>
    /// <exception cref="InvalidOperationException">Another SimVersion, a record is missing, or a re-evaluated generation differs from its record.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancel"/> was triggered.</exception>
    public static Population Rebuild(Session session, string branch, int generation, int threads = 1,
        Action<int, int>? progress = null, CancellationToken cancel = default)
    {
        var plan = MakePlan(session, branch, generation);
        Population population = plan.Start;
        var steps = plan.Steps;
        int i = 0;
        while (i < steps.Count)
        {
            // One runner per stretch of generations on the same branch and segment.
            var first = steps[i];
            int j = i;
            while (j + 1 < steps.Count && steps[j + 1].Branch == first.Branch && steps[j + 1].Segment.Id == first.Segment.Id
                   && steps[j + 1].Generation == steps[j].Generation + 1)
                j++;
            using (var training = new RacingTraining(first.Segment.Track, first.Segment.Config, first.BranchSeed, null, population.Size, threads,
                       population, first.Generation, first.Evaluated.Params))
            {
                var runner = training.Runner;
                runner.KeepPopulationSnapshots = false;
                runner.RecordBestCar = false;
                for (int k = i; k <= j; k++)
                {
                    cancel.ThrowIfCancellationRequested();
                    var step = steps[k];
                    runner.BeginGeneration();
                    int ticks = step.Evaluated.Ticks;
                    if (ticks < 0)
                    {
                        while (!runner.RunTicks(int.MaxValue)) { }
                    }
                    else
                    {
                        while (runner.ElapsedTicks < ticks && !runner.RunTicks(ticks - (int)runner.ElapsedTicks))
                            cancel.ThrowIfCancellationRequested();
                    }
                    var record = runner.EndGeneration(step.BreedParams);
                    if (!HistoryEntry.SameStats(record.Stats, step.Evaluated.Stats) || !record.BestGenome.BitEquals(step.Evaluated.BestGenome)
                        || (ticks >= 0 && record.Ticks != ticks))
                        throw new InvalidOperationException(
                            $"Rebuilding generation {step.Generation} of '{step.Branch}' gave another result than its record: the simulation is not deterministic.");
                    progress?.Invoke(k + 1, steps.Count);
                }
                population = new Population(runner.Population.Shape, (float[])runner.Population.Weights.Clone());
            }
            i = j + 1;
        }
        return population;
    }

    private static Plan MakePlan(Session session, string branch, int generation)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (session.SyncRoot)
        {
            if (!session.Compatibility.CanResimulate) throw new InvalidOperationException(session.Compatibility.Message);
            string b = session.OwnerOf(branch, generation);
            // A branch's first generation needs no record: its first population follows from the fork (or the seed), so a fork
            // that was made active and left again before it finished a generation can be switched back to.
            if (session.History.Get(b, generation) is null && !(session.Current.Branch == b && session.Current.Generation == generation)
                && generation != session.Branch(b).FirstGeneration)
            {
                if (generation >= session.HistoryEnd(b)) throw new ArgumentException($"Generation {generation} of '{b}' has not been evaluated.", nameof(generation));
                throw new InvalidOperationException($"The record of generation {generation} of '{b}' is missing (a damaged history file), so it cannot be rebuilt.");
            }
            int g = generation;
            var steps = new List<Step>();
            Population start;
            while (true)
            {
                var history = session.History.Find(b);
                var entry = history?.Get(g);
                if (entry?.Snapshot is { } snapshot)
                {
                    start = new Population(entry.BestGenome.Shape, (float[])snapshot.Clone());
                    break;
                }
                var current = session.Current;
                if (current.Branch == b && current.Generation == g)
                {
                    start = new Population(current.Population.Shape, (float[])current.Population.Weights.Clone());
                    break;
                }
                var br = session.Branch(b);
                if (g == br.FirstGeneration)
                {
                    if (br.Kind == ForkKind.Full)
                    {
                        b = br.Parent!; // the same population as the parent's generation g
                        continue;
                    }
                    var segment = session.SegmentAt(b, g);
                    int size = segment.PopulationSize > 0 ? segment.PopulationSize : current.Population.Size;
                    start = new Population(segment.Config.Brain, size);
                    ulong seed = SeedHash.Derive(br.Seed, (ulong)g);
                    if (br.Kind == ForkKind.Root)
                    {
                        start.Randomize(seed, RacingSettings.InitialOutputBias());
                    }
                    else
                    {
                        var parent = session.History.Get(br.Parent!, g)
                                     ?? throw new InvalidOperationException($"Generation {g} of '{br.Parent}' is missing, so the fork '{b}' cannot be rebuilt.");
                        Breeder.FromBest(parent.BestGenome.Weights, start, br.ForkParams!.Value, seed);
                    }
                    break;
                }
                // Population g = breed(evaluated population g − 1, seed of g, parameters of g).
                if (entry is null || history!.Get(g - 1) is not { } previous)
                    throw new InvalidOperationException($"The record of generation {(entry is null ? g : g - 1)} of '{b}' is missing (a damaged history file), so generation {generation} cannot be rebuilt.");
                if (entry.Seed != SeedHash.Derive(br.Seed, (ulong)g))
                    throw new InvalidOperationException($"Generation {g} of '{b}' has an unexpected seed.");
                steps.Add(new Step(b, br.Seed, g - 1, session.SegmentAt(b, g - 1), previous, entry.Params));
                g--;
            }
            steps.Reverse();
            return new Plan(start, g, steps);
        }
    }
}
