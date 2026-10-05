using Nitrogenesis.Sim.Brain;
using Nitrogenesis.Sim.Evolution;
using Nitrogenesis.Sim.History;
using Nitrogenesis.Sim.Rng;

namespace Nitrogenesis.Sim.Core;

/// <summary>
/// The training loop (PLAN §1, §2.1, §3.7): evaluates every genome of a population in a mode, records the
/// best car, then breeds the next generation. Every genome, elites included, is evaluated every generation.
/// </summary>
/// <remarks>
/// <para>One generation: <see cref="BeginGeneration"/> → <see cref="RunTicks"/> (as often as the caller likes,
/// e.g. once per frame; each call ends with a snapshot publish) → <see cref="EndGeneration"/>.
/// <see cref="RunGeneration"/> does all three without stopping. The simulation runs on the
/// <see cref="AgentScheduler"/>; scoring, ranking and breeding are single-threaded, so results are bit-identical
/// for any thread count.</para>
/// <para>Seeds: generation g uses <c>SeedHash.Derive(seed, g)</c> (initialisation for g = 0, breeding
/// otherwise), and each child its own stream under that (see <see cref="Breeder"/>).</para>
/// <para>The best car's recording comes from re-simulating only that agent after the generation (agents never
/// interact, so this reproduces its run exactly; the scores are compared bit for bit as a check). It runs on a
/// worker while the calling thread breeds, since the two jobs share no written state.</para>
/// </remarks>
public sealed class GenerationRunner : IDisposable
{
    private readonly IAgentMode _mode;
    private readonly AgentScheduler _scheduler;
    private readonly Mlp _brain;
    private readonly Breeder _breeder;
    private readonly float[] _outputBias;
    private readonly float[] _scores;
    private readonly ulong _seed;
    private Population _population, _spare;
    private EvolutionSettings _settings;
    private EvolutionParams _params;
    private bool _running;
    private long _elapsed;

    /// <param name="mode">The agents; their count is the population size.</param>
    /// <param name="shape">Brain shape; must match the mode's input and output counts.</param>
    /// <param name="outputBias">Added to each output's bias in fresh random genomes (racing: +0.3 throttle).</param>
    /// <param name="seed">Training seed.</param>
    /// <param name="settings">Evolution settings (clamped).</param>
    /// <param name="threads">Simulation worker threads W.</param>
    /// <param name="population">Population to start from (e.g. a resumed session), or null for a random generation <paramref name="generation"/>.</param>
    /// <param name="generation">Generation number of the starting population.</param>
    /// <param name="populationParams">Parameters that bred <paramref name="population"/> (for its record); default: the settings without boost.</param>
    public GenerationRunner(IAgentMode mode, BrainShape shape, ReadOnlySpan<float> outputBias, ulong seed, EvolutionSettings settings,
        int threads = 1, Population? population = null, int generation = 0, EvolutionParams? populationParams = null)
    {
        ArgumentNullException.ThrowIfNull(mode);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentOutOfRangeException.ThrowIfNegative(generation);
        if (shape.Inputs != mode.InputCount || shape.Outputs != mode.OutputCount)
            throw new ArgumentException($"Brain {shape.Inputs}→{shape.Outputs} does not fit the mode ({mode.InputCount} inputs, {mode.OutputCount} outputs).", nameof(shape));
        if (outputBias.Length != shape.Outputs) throw new ArgumentException("One bias offset per output is needed.", nameof(outputBias));
        if (population is not null && (population.Shape != shape || population.Size != mode.AgentCount))
            throw new ArgumentException("The population does not match the brain shape and agent count.", nameof(population));

        _mode = mode;
        _seed = seed;
        _outputBias = outputBias.ToArray();
        _settings = settings.Clamped();
        _params = populationParams ?? EvolutionParams.From(_settings, boost: false);
        Generation = generation;
        int n = mode.AgentCount;
        if (population is null)
        {
            population = new Population(shape, n);
            population.Randomize(GenerationSeed(generation), _outputBias);
        }
        _population = population;
        _spare = new Population(shape, n);
        _brain = new Mlp(shape, _population.Weights);
        _breeder = new Breeder(n);
        _scores = new float[n];
        _scheduler = new AgentScheduler(mode, threads);
        Snapshots = new SnapshotBuffer(n);
    }

    public IAgentMode Mode => _mode;
    public BrainShape Shape => _brain.Shape;
    public int PopulationSize => _mode.AgentCount;
    public int Threads => _scheduler.Workers;

    /// <summary>The generation being (or about to be) evaluated.</summary>
    public int Generation { get; private set; }

    /// <summary>Genomes of <see cref="Generation"/>. Replaced (not changed in place) when the next one is bred.</summary>
    public Population Population => _population;

    /// <summary>The brain driving the agents (genome i → agent i).</summary>
    public Mlp Brain => _brain;

    /// <summary>The renderer's view (published at every <see cref="RunTicks"/> end and at the generation start).</summary>
    public SnapshotBuffer Snapshots { get; }

    /// <summary>Stagnation detection and the mutation boost (reset it when a new segment starts).</summary>
    public StagnationTracker Stagnation { get; } = new();

    /// <summary>
    /// Evolution settings. A change takes effect at the next generation boundary (PLAN §3.7); the effective values
    /// end up in that generation's record. Safe to set from another thread.
    /// </summary>
    public EvolutionSettings Settings
    {
        get => Volatile.Read(ref _settings);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            Volatile.Write(ref _settings, value.Clamped());
        }
    }

    /// <summary>Whether the population snapshot goes into each record (thinning is the history store's job).</summary>
    public bool KeepPopulationSnapshots { get; set; } = true;

    /// <summary>True between <see cref="BeginGeneration"/> and <see cref="EndGeneration"/>.</summary>
    public bool IsRunning => _running;

    /// <summary>Ticks run in the current generation.</summary>
    public long ElapsedTicks => _elapsed;

    /// <summary>The seed of generation <paramref name="generation"/>.</summary>
    public ulong GenerationSeed(int generation) => SeedHash.Derive(_seed, (ulong)generation);

    /// <summary>Puts every agent at the start and publishes the first snapshot of the generation.</summary>
    public void BeginGeneration()
    {
        if (_running) throw new InvalidOperationException("The generation is already running.");
        _mode.Reset(AgentRange.All(_mode.AgentCount));
        _elapsed = 0;
        _running = true;
        Snapshots.Publish(_mode, 0, Generation);
    }

    /// <summary>
    /// Runs up to <paramref name="maxTicks"/> ticks, then publishes a snapshot. Returns true when every agent is
    /// done (the generation can end). Does not allocate.
    /// </summary>
    public bool RunTicks(int maxTicks)
    {
        if (!_running) throw new InvalidOperationException("Call BeginGeneration first.");
        ArgumentOutOfRangeException.ThrowIfNegative(maxTicks);
        _elapsed += _scheduler.Run(_brain, maxTicks);
        Snapshots.Publish(_mode, _elapsed, Generation);
        return _scheduler.CountAlive() == 0;
    }

    /// <summary>
    /// Scores the agents where they are (normally all done), records the best car, updates the stagnation check
    /// and breeds the next generation. Returns the record of the generation that just ended.
    /// </summary>
    public GenerationRecord EndGeneration()
    {
        if (!_running) throw new InvalidOperationException("Call BeginGeneration first.");
        _running = false;

        GenerationStats stats = ComputeStats();
        int generation = Generation;
        EvolutionParams madeWith = _params;

        EvolutionSettings settings = Settings;
        Stagnation.Observe(stats.BestProgress, stats.BestFinishTicks, settings);
        _params = EvolutionParams.From(settings, Stagnation.ConsumeBoost(settings));
        Generation++;

        // Two independent jobs, overlapped so the other workers are not idle for both: recording the best car
        // (reads the population, writes only that agent's mode state) and copying the population on a worker,
        // breeding (reads the population and scores, writes the spare) on this thread.
        Population parents = _population;
        bool keepSnapshot = KeepPopulationSnapshots;
        Recording? recording = null;
        float[]? snapshot = null;
        _scheduler.RunAlongside(
            () =>
            {
                recording = RecordAgent(stats.BestIndex);
                snapshot = keepSnapshot ? (float[])parents.Weights.Clone() : null;
            },
            () => _breeder.Breed(parents, _scores, _spare, _params, GenerationSeed(Generation), _outputBias));
        (_population, _spare) = (_spare, _population);
        _brain.Weights = _population.Weights;

        return new GenerationRecord
        {
            Generation = generation,
            Seed = GenerationSeed(generation),
            Params = madeWith,
            Stats = stats,
            BestGenome = parents.CopyGenome(stats.BestIndex),
            BestRecording = recording!,
            PopulationSnapshot = snapshot,
        };
    }

    /// <summary>Evaluates the current generation to the end and breeds the next one.</summary>
    public GenerationRecord RunGeneration()
    {
        BeginGeneration();
        while (!RunTicks(int.MaxValue)) { }
        return EndGeneration();
    }

    private GenerationStats ComputeStats()
    {
        int n = _mode.AgentCount, best = 0, finished = 0;
        double sum = 0;
        float bestTime = float.PositiveInfinity, bestProgress = 0f;
        for (int i = 0; i < n; i++)
        {
            float s = _mode.Score(i);
            _scores[i] = s;
            sum += s;
            if (s > _scores[best] || float.IsNaN(_scores[best]) && !float.IsNaN(s)) best = i;
            float progress = _mode.ProgressDistance(i);
            if (progress > bestProgress) bestProgress = progress;
            if (_mode.Status(i) == AgentStatus.Finished)
            {
                finished++;
                float t = _mode.FinishTicks(i);
                if (t < bestTime) bestTime = t;
            }
        }
        return new GenerationStats(_scores[best], (float)(sum / n), best, finished, bestTime, bestProgress);
    }

    /// <summary>Re-runs one agent alone for the ticks of this generation, recording its pose before the first tick and after each.</summary>
    private Recording RecordAgent(int agent)
    {
        var range = new AgentRange(agent, 1);
        var recording = new Recording((int)Math.Min(_elapsed + 1, Recording.MaxSamples));
        _mode.Reset(range);
        _mode.Record(agent, recording);
        for (long t = 0; t < _elapsed && !_mode.IsDone(agent); t++)
        {
            _mode.Step(range, 1, _brain);
            _mode.Record(agent, recording);
        }
        float replayed = _mode.Score(agent);
        if (BitConverter.SingleToUInt32Bits(replayed) != BitConverter.SingleToUInt32Bits(_scores[agent]))
            throw new InvalidOperationException($"Re-simulating agent {agent} gave score {replayed}, not {_scores[agent]}: the simulation is not deterministic.");
        return recording;
    }

    public void Dispose() => _scheduler.Dispose();
}
