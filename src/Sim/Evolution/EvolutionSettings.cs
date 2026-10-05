namespace Nitrogenesis.Sim.Evolution;

/// <summary>
/// Evolution settings (PLAN §3.7), all "live": a change takes effect at the next generation boundary (§6.4).
/// Properties hold the defaults; <see cref="Clamped"/> forces every value into its limits.
/// </summary>
/// <remarks>
/// Population size is not here: changing it is a "new segment" setting (§6.4) handled by the session logic,
/// so a runner keeps the size it was built with. <see cref="DefaultPopulationSize"/> is its default.
/// </remarks>
public sealed record EvolutionSettings
{
    public const int DefaultPopulationSize = 200;
    /// <summary>σ of the generation-0 weights, N(0, 0.5) (§3.5); also used for the fresh random genomes.</summary>
    public const float InitialSigma = 0.5f;
    /// <summary>Generations the stagnation boost lasts once triggered.</summary>
    public const int BoostGenerations = 5;
    /// <summary>The boost multiplies the mutation σ and chance by this.</summary>
    public const float BoostFactor = 2f;

    /// <summary>Share of the population copied unchanged (the best by score). Limits 0…0.5.</summary>
    public float ElitismFraction { get; init; } = 0.05f;
    public const float MaxElitismFraction = 0.5f;

    /// <summary>Genomes drawn per tournament; the best wins. Limits 1…10 (1 = uniform random parent).</summary>
    public int TournamentSize { get; init; } = 3;
    public const int MinTournamentSize = 1, MaxTournamentSize = 10;

    /// <summary>Chance that an offspring is a uniform crossover of two parents rather than a copy of one. Limits 0…1.</summary>
    public float CrossoverRate { get; init; } = 0.5f;

    /// <summary>Chance per weight of an offspring to be mutated. Limits 0…1.</summary>
    public float MutationChance { get; init; } = 0.1f;

    /// <summary>σ of the Gaussian added to a mutated weight. Limits 0.01…2.</summary>
    public float MutationSigma { get; init; } = 0.2f;
    public const float MinMutationSigma = 0.01f, MaxMutationSigma = 2f;

    /// <summary>Share of the population replaced by fresh random genomes each generation. Limits 0…0.5.</summary>
    public float RandomFraction { get; init; } = 0.02f;
    public const float MaxRandomFraction = 0.5f;

    /// <summary>Automatic mutation boost when progress stagnates (§3.7).</summary>
    public bool StagnationBoost { get; init; } = true;

    /// <summary>Generations over which stagnation is judged. Limits 5…200.</summary>
    public int StagnationWindow { get; init; } = 15;
    public const int MinStagnationWindow = 5, MaxStagnationWindow = 200;

    /// <summary>A copy with every value inside its limits; NaN falls back to the default.</summary>
    public EvolutionSettings Clamped()
    {
        var d = new EvolutionSettings();
        return this with
        {
            ElitismFraction = Clamp(ElitismFraction, 0f, MaxElitismFraction, d.ElitismFraction),
            TournamentSize = Math.Clamp(TournamentSize, MinTournamentSize, MaxTournamentSize),
            CrossoverRate = Clamp(CrossoverRate, 0f, 1f, d.CrossoverRate),
            MutationChance = Clamp(MutationChance, 0f, 1f, d.MutationChance),
            MutationSigma = Clamp(MutationSigma, MinMutationSigma, MaxMutationSigma, d.MutationSigma),
            RandomFraction = Clamp(RandomFraction, 0f, MaxRandomFraction, d.RandomFraction),
            StagnationWindow = Math.Clamp(StagnationWindow, MinStagnationWindow, MaxStagnationWindow),
        };
    }

    private static float Clamp(float v, float min, float max, float fallback) =>
        float.IsNaN(v) ? fallback : v < min ? min : v > max ? max : v;
}

/// <summary>
/// The evolution parameters that actually bred one generation (PLAN §3.7: stored in every GenerationRecord),
/// with the stagnation boost already applied to the mutation chance and σ.
/// </summary>
public readonly record struct EvolutionParams(
    float ElitismFraction,
    int TournamentSize,
    float CrossoverRate,
    float MutationChance,
    float MutationSigma,
    float RandomFraction,
    bool Boost)
{
    /// <summary>The effective parameters for <paramref name="settings"/> (clamped), with or without the boost.</summary>
    public static EvolutionParams From(EvolutionSettings settings, bool boost)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var s = settings.Clamped();
        float chance = boost ? MathF.Min(1f, s.MutationChance * EvolutionSettings.BoostFactor) : s.MutationChance;
        float sigma = boost ? s.MutationSigma * EvolutionSettings.BoostFactor : s.MutationSigma;
        return new(s.ElitismFraction, s.TournamentSize, s.CrossoverRate, chance, sigma, s.RandomFraction, boost);
    }

    /// <summary>
    /// Elites in a population of <paramref name="size"/>: the fraction rounded half up, at least 1 when the
    /// fraction is above 0 (so the best genome always survives), and never more than the population.
    /// </summary>
    public int EliteCount(int size) => Count(ElitismFraction, size, 0);

    /// <summary>Fresh random genomes: the fraction rounded half up, limited to what the elites leave.</summary>
    public int RandomCount(int size)
    {
        int elites = EliteCount(size);
        return Count(RandomFraction, size, elites);
    }

    private static int Count(float fraction, int size, int taken)
    {
        if (!(fraction > 0f)) return 0;
        int n = Math.Max(1, (int)(fraction * (double)size + 0.5));
        return Math.Min(n, size - taken);
    }
}
