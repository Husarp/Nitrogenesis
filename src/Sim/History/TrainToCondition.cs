using System.Globalization;

namespace Nitrogenesis.Sim.History;

/// <summary>What "Train to…" waits for (PLAN §4 "Train without visuals").</summary>
public enum TrainToKind
{
    /// <summary>Until generation N is the next to run (N − 1 generations evaluated in all).</summary>
    Generation,
    /// <summary>Until a generation has a car that finished.</summary>
    FirstFinish,
    /// <summary>Until a generation's best finish time is below a number of seconds.</summary>
    BestTimeBelow,
}

/// <summary>
/// A stop condition for training without visuals (PLAN §4): "train to generation N", "until a car finishes" or
/// "until best time &lt; X s". It is checked after every generation that ended during the run.
/// </summary>
public readonly record struct TrainToCondition(TrainToKind Kind, int Generation, float Seconds)
{
    public static TrainToCondition ToGeneration(int generation) =>
        new(TrainToKind.Generation, Math.Max(0, generation), 0f);

    public static TrainToCondition UntilFinish() => new(TrainToKind.FirstFinish, 0, 0f);

    public static TrainToCondition UntilBestTimeBelow(float seconds) =>
        new(TrainToKind.BestTimeBelow, 0, float.IsFinite(seconds) && seconds > 0 ? seconds : throw new ArgumentOutOfRangeException(nameof(seconds)));

    /// <summary>True when there is nothing to do before the first generation ends (only "to generation N" can be).</summary>
    public bool IsMetAt(int nextGeneration) => Kind == TrainToKind.Generation && nextGeneration >= Generation;

    /// <summary>True when the generation that just ended (<paramref name="stats"/>), with <paramref name="nextGeneration"/> now next, meets it.</summary>
    public bool IsMet(GenerationStats stats, int nextGeneration, int ticksPerSecond) => Kind switch
    {
        TrainToKind.Generation => nextGeneration >= Generation,
        TrainToKind.FirstFinish => stats.FinishedCount > 0,
        TrainToKind.BestTimeBelow => stats.FinishedCount > 0 && stats.BestFinishTicks / ticksPerSecond < Seconds,
        _ => true,
    };

    /// <summary>Short text for the progress screen.</summary>
    public string Describe() => Kind switch
    {
        TrainToKind.Generation => string.Create(CultureInfo.InvariantCulture, $"to generation {Generation}"),
        TrainToKind.FirstFinish => "until a car finishes",
        _ => string.Create(CultureInfo.InvariantCulture, $"until best time < {Seconds:0.##} s"),
    };
}
