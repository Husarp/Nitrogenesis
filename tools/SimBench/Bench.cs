using System.Diagnostics;
using Nitrogenesis.Sim.Brain;
using Nitrogenesis.Sim.History;
using Nitrogenesis.Sim.Io;
using Nitrogenesis.Sim.Racing;

/// <summary>
/// Speed benchmark (PLAN §9) of the full training loop (simulation, brain, recording, breeding):
/// <list type="number">
/// <item>the §9 headless target: 200 cars on 7 threads on every bundled track, each repeated, reporting the median
/// and the minimum per track and the worst track;</item>
/// <item>scaling: 200 and 300 cars on 1, 3 and 7 threads on one track;</item>
/// <item>the single-threaded cost of the parts of one agent-tick: rays alone, all sensors and the brain.</item>
/// </list>
/// </summary>
/// <remarks>
/// Every run trains from the same seed, skips <see cref="WarmupGenerations"/> (JIT, first learning) and then times
/// whole generations until the run has lasted the given number of seconds. Results do not depend on the thread
/// count, so runs of one configuration do the same work and differ only by machine noise. For a laptop-like
/// machine, run it under <c>taskset -c 0-3</c> (7 threads on 4 cores).
/// </remarks>
internal static class Bench
{
    private const int WarmupGenerations = 3;
    private const ulong Seed = 1;
    private const int TargetPopulation = 200, TargetThreads = 7;
    private static readonly int[] Populations = [200, 300];
    private static readonly int[] ThreadCounts = [1, 3, 7];

    /// <summary>One timed run.</summary>
    private readonly record struct Measurement(double AgentTicksPerSecond, double GenerationsPerSecond, double SecondsPerCar, GenerationRecord Last, double NsPerAgentTick);

    /// <param name="tracks">Bundled track paths for the headline; the first one is also used for scaling and parts.</param>
    /// <param name="seconds">Length of every timed run.</param>
    /// <param name="repeats">Headline runs per track.</param>
    public static int Run(IReadOnlyList<string> tracks, double seconds, int repeats)
    {
        Console.WriteLine($"bench: seed {Seed}, {WarmupGenerations} warm-up generations, then whole generations for ≥ {seconds:F0} s per run; " +
                          $"{Environment.ProcessorCount} logical processors available");

        Console.WriteLine($"§9 headless target: {TargetPopulation} cars, {TargetThreads} threads, {repeats} runs per track (target ≥ 2.50 M agent-ticks/s)");
        string worstTrack = "";
        double worstMedian = double.PositiveInfinity;
        foreach (string path in tracks)
        {
            var track = TrackFile.Load(path);
            var runs = new List<Measurement>();
            for (int r = 0; r < repeats; r++) runs.Add(Time(track, TargetPopulation, TargetThreads, seconds));
            var rates = runs.Select(x => x.AgentTicksPerSecond).Order().ToArray();
            double median = rates[rates.Length / 2], min = rates[0];
            string name = Path.GetFileNameWithoutExtension(path);
            Console.WriteLine($"  {name,-16} median {median / 1e6,5:F2} M  min {min / 1e6,5:F2} M  max {rates[^1] / 1e6,5:F2} M agent-ticks/s  " +
                              $"{runs.Select(x => x.GenerationsPerSecond).Order().ElementAt(runs.Count / 2),5:F1} gens/s  ({runs[0].SecondsPerCar:F1} s per car)");
            if (median < worstMedian)
            {
                worstMedian = median;
                worstTrack = name;
            }
        }
        Console.WriteLine($"  worst track: {worstTrack}, median {worstMedian / 1e6:F2} M agent-ticks/s — target {(worstMedian >= 2.5e6 ? "met" : "MISSED")}");

        var first = TrackFile.Load(tracks[0]);
        Console.WriteLine($"scaling on {Path.GetFileNameWithoutExtension(tracks[0])} (one run each):");
        Measurement? reference = null;
        foreach (int population in Populations)
        {
            double singleThread = 0;
            foreach (int threads in ThreadCounts)
            {
                var run = Time(first, population, threads, seconds);
                if (threads == 1) singleThread = run.AgentTicksPerSecond;
                if (population == Populations[0] && threads == 1) reference = run;
                Console.WriteLine($"  {population} cars, {threads} thread{(threads == 1 ? " " : "s")}: {run.AgentTicksPerSecond / 1e6,5:F2} M agent-ticks/s  " +
                                  $"{run.GenerationsPerSecond,5:F1} gens/s  (×{run.AgentTicksPerSecond / singleThread:F2} vs 1 thread)");
            }
        }
        Parts(first, reference!.Value.Last.BestRecording, reference.Value.Last.BestGenome.Weights, reference.Value.NsPerAgentTick);
        return 0;
    }

    /// <summary>Trains for ≥ <paramref name="seconds"/> after the warm-up and measures the rate.</summary>
    private static Measurement Time(Nitrogenesis.Sim.Map.Track track, int population, int threads, double seconds)
    {
        using var training = new RacingTraining(track, new RacingSettings(), Seed, populationSize: population, threads: threads);
        var runner = training.Runner;
        for (int g = 0; g < WarmupGenerations; g++) runner.RunGeneration();

        long agentTicks = 0;
        int generations = 0;
        GenerationRecord last;
        var watch = Stopwatch.StartNew();
        do
        {
            last = runner.RunGeneration();
            generations++;
            for (int i = 0; i < population; i++) agentTicks += training.Mode.Tick(i);
        } while (watch.Elapsed.TotalSeconds < seconds);
        double elapsed = watch.Elapsed.TotalSeconds;
        return new Measurement(agentTicks / elapsed, generations / elapsed, agentTicks / (double)(generations * population) / 60, last,
            elapsed * 1e9 / agentTicks);
    }

    /// <summary>Times rays, sensors and brain over the poses of a recorded best run, single-threaded.</summary>
    private static void Parts(Nitrogenesis.Sim.Map.Track track, Recording poses, float[] genome, double nsPerAgentTick)
    {
        var settings = new RacingSettings();
        var racing = new RacingTrack(track, settings);
        var physics = new CarPhysics(racing.Grid, racing.Clearance, settings);
        var sensors = new Sensors(racing, physics);
        var shape = new BrainShape(sensors.InputCount, BrainShape.DefaultHidden, 0, RacingSettings.OutputCount);
        int[] offsets = Sensors.RayOffsets(settings.RayCount, settings.RaySpreadDeg);
        var inputs = new float[sensors.InputCount];
        var hidden = new float[shape.HiddenCount];
        var outputs = new float[shape.Outputs];
        float sink = 0;

        double rays = Time(poses.Count, i =>
        {
            float x = poses.X(i), y = poses.Y(i);
            int heading = poses.Heading(i);
            foreach (int offset in offsets)
            {
                int a = heading + offset;
                sensors.CastRay(x, y, FastMath.CosUnits(a), FastMath.SinUnits(a), out float nonRoad, out float wall);
                sink += nonRoad + wall;
            }
        });
        double allSensors = Time(poses.Count, i =>
        {
            var car = new CarState { X = poses.X(i), Y = poses.Y(i), Heading = poses.Heading(i) };
            sensors.Write(car, 0.5f, 0f, 0.5f, 0.5f, inputs);
            sink += inputs[0];
        });
        double brain = Time(poses.Count, i =>
        {
            inputs[0] = i * 1e-3f;
            Mlp.Evaluate(shape, genome, inputs, hidden, outputs);
            sink += outputs[0];
        });
        Console.WriteLine($"parts per agent-tick (1 thread, {poses.Count} poses of the best run): " +
                          $"rays {rays,5:F0} ns ({settings.RayCount} rays, {rays / settings.RayCount:F0} ns each), sensors {allSensors,5:F0} ns, " +
                          $"brain {brain,5:F0} ns; whole agent-tick in training {nsPerAgentTick,5:F0} ns{(sink == 1234.5f ? "!" : "")}");
    }

    /// <summary>Nanoseconds per call of <paramref name="body"/> over indices 0…count−1, repeated for about a second after a warm-up.</summary>
    private static double Time(int count, Action<int> body)
    {
        for (int i = 0; i < count; i++) body(i);
        long calls = 0;
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 1000)
        {
            for (int i = 0; i < count; i++) body(i);
            calls += count;
        }
        return watch.Elapsed.TotalMilliseconds * 1e6 / calls;
    }
}
