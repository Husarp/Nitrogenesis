namespace Nitrogenesis.Sim.History;

/// <summary>
/// The data of the training graph (PLAN §6.2): best and average score per generation of the active lineage, plus
/// the segment dividers (generations where a new segment run starts, PLAN §5). The training thread fills it; a view
/// copies a downsampled version with <see cref="Sample"/> into its own arrays, without allocating.
/// </summary>
/// <remarks>
/// Index = generation number (a lineage always starts at generation 0). A generation that is not stored (a gap after
/// a damaged history file) is NaN and left out of the drawing. Not thread-safe: the owner locks.
/// </remarks>
public sealed class ScoreGraph
{
    private float[] _best = new float[256], _avg = new float[256];
    private int[] _dividers = new int[8];
    private int _dividerCount;

    /// <summary>Generations 0 … Count − 1 have a slot (NaN when unknown).</summary>
    public int Count { get; private set; }

    /// <summary>Goes up with every change, so a view copies only when it moved.</summary>
    public int Version { get; private set; }

    public int DividerCount => _dividerCount;

    public void Clear()
    {
        Count = 0;
        _dividerCount = 0;
        Version++;
    }

    /// <summary>Sets one generation's scores (slots before it that were never set stay NaN).</summary>
    public void Set(int generation, float best, float average)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(generation);
        if (generation >= _best.Length)
        {
            int size = _best.Length;
            while (size <= generation) size *= 2;
            Array.Resize(ref _best, size);
            Array.Resize(ref _avg, size);
        }
        for (int g = Count; g < generation; g++)
        {
            _best[g] = float.NaN;
            _avg[g] = float.NaN;
        }
        _best[generation] = best;
        _avg[generation] = average;
        if (generation >= Count) Count = generation + 1;
        Version++;
    }

    /// <summary>Replaces the divider list (generations where a new segment run starts, ascending).</summary>
    public void SetDividers(IReadOnlyList<int> generations)
    {
        ArgumentNullException.ThrowIfNull(generations);
        if (generations.Count > _dividers.Length) Array.Resize(ref _dividers, Math.Max(generations.Count, _dividers.Length * 2));
        for (int i = 0; i < generations.Count; i++) _dividers[i] = generations[i];
        _dividerCount = generations.Count;
        Version++;
    }

    public float Best(int generation) => _best[CheckIndex(generation)];
    public float Average(int generation) => _avg[CheckIndex(generation)];

    /// <summary>
    /// Downsamples the series into at most <c>best.Length</c> buckets of consecutive generations: bucket i covers
    /// generations [i·Count/n, (i+1)·Count/n) (one generation per bucket while Count ≤ n), with the highest best score
    /// and the mean of the average scores of the known generations in it (NaN when it has none). Returns the buckets
    /// used. <paramref name="lowest"/>/<paramref name="highest"/> are the smallest average and largest best score
    /// over the whole series (NaN when empty). Does not allocate.
    /// </summary>
    public int Sample(Span<float> best, Span<float> average, out float lowest, out float highest)
    {
        if (average.Length < best.Length) throw new ArgumentException("The average buffer is shorter than the best buffer.", nameof(average));
        lowest = float.NaN;
        highest = float.NaN;
        int n = Math.Min(best.Length, Count);
        for (int i = 0; i < n; i++)
        {
            int from = (int)((long)i * Count / n), to = (int)((long)(i + 1) * Count / n);
            float b = float.NaN, sum = 0f;
            int known = 0;
            for (int g = from; g < to; g++)
            {
                float bg = _best[g], ag = _avg[g];
                if (!float.IsNaN(bg) && (float.IsNaN(b) || bg > b)) b = bg;
                if (!float.IsNaN(ag))
                {
                    sum += ag;
                    known++;
                }
            }
            float a = known > 0 ? sum / known : float.NaN;
            best[i] = b;
            average[i] = a;
            if (!float.IsNaN(b) && (float.IsNaN(highest) || b > highest)) highest = b;
            if (!float.IsNaN(a) && (float.IsNaN(lowest) || a < lowest)) lowest = a;
        }
        return n;
    }

    /// <summary>Copies the dividers (at most <c>target.Length</c>) and returns how many were copied.</summary>
    public int CopyDividers(Span<int> target)
    {
        int n = Math.Min(target.Length, _dividerCount);
        _dividers.AsSpan(0, n).CopyTo(target);
        return n;
    }

    private int CheckIndex(int g) => (uint)g < (uint)Count ? g : throw new ArgumentOutOfRangeException(nameof(g));
}
