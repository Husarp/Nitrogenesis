using System.Diagnostics;
using Nitrogenesis.Sim;
using Nitrogenesis.Sim.Core;
using Nitrogenesis.Sim.Io;
using Nitrogenesis.Sim.Map;
using Nitrogenesis.Sim.Racing;

// SimBench: headless tools for the simulation (PLAN §2, §9).
//   make-tracks [dir]   write the bundled hand-made tracks (default: ./tracks) and validate them
//   reference [dir]     run the reference driver on every .track in dir (default: ./tracks)
//   train --track X --gens N [--pop P] [--threads T] [--seed S]
//                       train with default settings and print every generation
//   bench [--track X] [--seconds S] [--repeats R]
//                       agent-ticks/s: 200 cars on 7 threads on every bundled track (or X), R runs of S seconds
//                       each (default 5 × 10 s), median/min and the worst track; then 200 and 300 cars on 1, 3 and
//                       7 threads on s_curve (or X); then the cost of rays, sensors and brain alone
//   view [--track X] [--pop P] [--threads T] [--speed S|max] [--seconds N] [--warm G]
//                       the training view's simulation side (TrainingHost) under a 60 Hz frame loop, no rendering:
//                       actual speed and simulation time per frame (defaults: s_curve, 300 cars, 3 threads, 100×);
//                       --warm G trains G generations at MAX first
//   learn-suite [--only NAME] [--threads T]
//                       the §9 learning test on the bundled tracks (or one of them); exits 1 on any failure
// A track argument is a .track path or the name of a bundled track (looked up in tracks/).

/// <summary>Grass cost for map checks: roadTopSpeed / grassTopSpeed with the default 45 % grass speed (PLAN §3.3).</summary>
const double GrassCost = 1 / 0.45;

Console.OutputEncoding = System.Text.Encoding.UTF8; // "×" and "—" in a Windows console
Console.WriteLine($"SimBench — SimVersion {SimInfo.SimVersion}");
string command = args.Length > 0 ? args[0] : "";
try
{
    var options = Options.Parse(args.Skip(1));
    switch (command)
    {
        case "make-tracks":
            return MakeTracks(options.Positional ?? "tracks");
        case "reference":
            return Reference(options.Positional ?? "tracks");
        case "train":
            return Train(options.Track(null), options.Int("gens", 30), options.Int("pop", 200),
                options.Int("threads", Environment.ProcessorCount - 1), options.ULong("seed", 1));
        case "bench":
            return Bench.Run(options.Has("track") ? [options.Track(null)] : TrackPaths.Bundled("s_curve"),
                options.Int("seconds", 10), options.Int("repeats", 5));
        case "view":
            return ViewBench.Run(options.Track("s_curve"), options.Int("pop", 300), options.Int("threads", 3),
                options.Double("speed", 100), options.Int("seconds", 10), options.Has("warm") ? options.Int("warm", 0) : 0);
        case "learn-suite":
            return LearnSuite.Run(options.Int("threads", Environment.ProcessorCount - 1), options.Text("only"));
    }
}
catch (ArgumentException e)
{
    Console.Error.WriteLine(e.Message);
}
Console.Error.WriteLine("Usage: SimBench make-tracks [dir] | reference [dir] | train --track X --gens N [--pop P] [--threads T] [--seed S]" +
                        " | bench [--track X] [--seconds S] [--repeats R] | view [--track X] [--pop P] [--threads T] [--speed S|max] [--seconds N] [--warm G] | learn-suite [--only NAME] [--threads T]");
return 2;

static int MakeTracks(string dir)
{
    Directory.CreateDirectory(dir);
    bool ok = true;
    foreach (var (file, track) in BundledTracks.All())
    {
        var watch = Stopwatch.StartNew();
        var clearance = Clearance.Compute(track.Grid);
        var field = DistanceField.ForRacing(track.Grid, clearance, CarSize.HalfWidth, GrassCost);
        double ms = watch.Elapsed.TotalMilliseconds;

        var solvable = Solvability.Check(track, clearance, field, CarSize.HalfWidth);
        var shortcut = GrassShortcut.Check(track, clearance, CarSize.HalfWidth, GrassCost);
        bool valid = solvable == SolvabilityResult.Solvable && !shortcut.IsShortcut;
        ok &= valid;

        string path = Path.Combine(dir, file);
        TrackFile.Save(track, path);
        Console.WriteLine($"{(valid ? "ok  " : "FAIL")} {file,-22} {solvable,-17} start→finish {shortcut.WithGrass,6:F1} " +
                          $"(road only {shortcut.RoadOnly,6:F1})  clearance+field {ms,5:F1} ms  hash {TrackHash.Compute(track)[..12]}");
    }
    return ok ? 0 : 1;
}

static int Reference(string dir)
{
    bool ok = true;
    foreach (string path in Directory.GetFiles(dir, "*" + TrackFile.Extension).Order(StringComparer.Ordinal))
    {
        var track = new RacingTrack(TrackFile.Load(path), new RacingSettings());
        var watch = Stopwatch.StartNew();
        var result = ReferenceDriver.Run(track);
        double ms = watch.Elapsed.TotalMilliseconds;
        ok &= result.Finished;
        int limit = RacingFitness.ResolveTimeLimitTicks(track.Settings, result.Finished ? result.TimeSeconds : null, track.StartDistance);
        Console.WriteLine($"{(result.Finished ? "ok  " : "FAIL")} {Path.GetFileName(path),-22} {result.EndStatus,-9} " +
                          $"time {result.TimeSeconds,6:F2} s  auto limit {limit / 60f,6:F2} s  path {track.StartDistance,6:F1}  ({ms:F0} ms)");
    }
    return ok ? 0 : 1;
}

static int Train(string path, int generations, int population, int threads, ulong seed)
{
    using var training = new RacingTraining(TrackFile.Load(path), new RacingSettings(), seed, populationSize: population, threads: threads);
    var runner = training.Runner;
    Console.WriteLine($"{Path.GetFileName(path)}: {runner.PopulationSize} cars, brain {training.Shape.Inputs}-{training.Shape.Hidden1}-{training.Shape.Outputs}, " +
                      $"time limit {training.TimeLimitTicks / 60f:F1} s, {threads} threads, seed {seed}");
    int firstFinish = -1;
    long agentTicks = 0;
    var total = Stopwatch.StartNew();
    for (int g = 0; g < generations; g++)
    {
        var watch = Stopwatch.StartNew();
        var record = runner.RunGeneration();
        for (int i = 0; i < runner.PopulationSize; i++) agentTicks += training.Mode.Tick(i);
        var s = record.Stats;
        if (s.FinishedCount > 0 && firstFinish < 0) firstFinish = g;
        string grass = s.FinishedCount > 0 && training.Mode.Status(s.BestIndex) == AgentStatus.Finished
            ? $"  grass {LearnTest.GrassShare(record.BestRecording, training.Track.Grid),5:P1}" : "";
        Console.WriteLine($"gen {g,4}  best {s.BestScore,7:F4}  avg {s.AverageScore,7:F4}  progress {s.BestProgress,6:F1}  " +
                          $"finished {s.FinishedCount,3}  best time {(s.FinishedCount > 0 ? $"{s.BestFinishTicks / 60f,6:F2} s" : "     —  ")}" +
                          $"{grass}{(record.Params.Boost ? "  boost" : "")}  {watch.Elapsed.TotalMilliseconds,6:F0} ms");
    }
    double seconds = total.Elapsed.TotalSeconds;
    Console.WriteLine($"first finish: {(firstFinish >= 0 ? $"generation {firstFinish}" : "none")}; {generations / seconds:F2} gens/s, " +
                      $"{agentTicks / seconds / 1e6:F2} M agent-ticks/s; population hash 0x{runner.Population.ContentHash():X16}");
    return 0;
}

/// <summary>Minimal "--name value" option parsing, plus one optional positional argument.</summary>
internal sealed class Options
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public string? Positional { get; private set; }

    public static Options Parse(IEnumerable<string> args)
    {
        var o = new Options();
        using var e = args.GetEnumerator();
        while (e.MoveNext())
        {
            string a = e.Current;
            if (!a.StartsWith("--", StringComparison.Ordinal))
            {
                if (o.Positional is not null) throw new ArgumentException($"Unexpected argument '{a}'.");
                o.Positional = a;
                continue;
            }
            if (!e.MoveNext()) throw new ArgumentException($"Option {a} needs a value.");
            o._values[a[2..]] = e.Current;
        }
        return o;
    }

    public int Int(string name, int fallback) => _values.TryGetValue(name, out string? v)
        ? int.TryParse(v, out int i) && i > 0 ? i : throw new ArgumentException($"--{name} must be a positive whole number.")
        : fallback;

    public double Double(string name, double fallback) => _values.TryGetValue(name, out string? v)
        ? v.Equals("max", StringComparison.OrdinalIgnoreCase) ? double.PositiveInfinity
        : double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d) && d > 0 ? d
        : throw new ArgumentException($"--{name} must be a positive number or 'max'.")
        : fallback;

    public string? Text(string name) => _values.TryGetValue(name, out string? v) ? v : null;

    public bool Has(string name) => _values.ContainsKey(name);

    public ulong ULong(string name, ulong fallback) => _values.TryGetValue(name, out string? v)
        ? ulong.TryParse(v, out ulong u) ? u : throw new ArgumentException($"--{name} must be a whole number ≥ 0.")
        : fallback;

    /// <summary>The --track option as a file path: an existing path, or a bundled track name.</summary>
    public string Track(string? fallback)
    {
        string name = _values.TryGetValue("track", out string? v) ? v : fallback ?? throw new ArgumentException("--track is required.");
        return TrackPaths.Resolve(name);
    }
}

internal static class TrackPaths
{
    /// <summary>An existing file as given, else <c>tracks/&lt;name&gt;.track</c> in the current folder or above the tool.</summary>
    public static string Resolve(string name)
    {
        if (File.Exists(name)) return name;
        string file = name.EndsWith(TrackFile.Extension, StringComparison.Ordinal) ? name : name + TrackFile.Extension;
        foreach (string dir in Candidates())
        {
            string path = Path.Combine(dir, file);
            if (File.Exists(path)) return path;
        }
        throw new ArgumentException($"Track '{name}' not found (neither a file nor a bundled track in tracks/).");
    }

    /// <summary>Every bundled track (the folder that holds <paramref name="first"/>), with <paramref name="first"/> first.</summary>
    public static string[] Bundled(string first)
    {
        string firstPath = Resolve(first);
        string dir = Path.GetDirectoryName(Path.GetFullPath(firstPath))!;
        return Directory.GetFiles(dir, "*" + TrackFile.Extension)
            .OrderBy(p => Path.GetFullPath(p) == Path.GetFullPath(firstPath) ? 0 : 1).ThenBy(p => p, StringComparer.Ordinal).ToArray();
    }

    private static IEnumerable<string> Candidates()
    {
        yield return "tracks";
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            yield return Path.Combine(dir.FullName, "tracks");
    }
}
