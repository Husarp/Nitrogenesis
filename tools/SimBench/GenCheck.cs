using System.Diagnostics;
using Nitrogenesis.Sim.Gen;
using Nitrogenesis.Sim.Map;

/// <summary>
/// M5 acceptance (PLAN §8): for every template, seeds first … first + N − 1 with the default parameters: how many are valid on the
/// first try (≥ 90 % each), the reference times, the problems of the others, and how many tries <c>GenerateValid</c> needs from
/// each failing seed. Exits 1 when a template is under 90 %.
/// </summary>
internal static class GenCheck
{
    public const double Required = 0.9;

    public static int Run(int seeds, ulong first, string? only, MapSize? size, bool verbose)
    {
        bool ok = true;
        var total = Stopwatch.StartNew();
        foreach (var template in Templates.All)
        {
            if (only is not null && !template.ToString().Equals(only, StringComparison.OrdinalIgnoreCase)) continue;
            var p = GeneratorParams.Defaults(template);
            if (size is { } s) p = p with { Size = s };
            int valid = 0;
            var times = new List<float>();
            var problems = new Dictionary<GenProblem, int>();
            var retries = new List<int>();
            double ms = 0;
            for (ulong seed = first; seed < first + (ulong)seeds; seed++)
            {
                var watch = Stopwatch.StartNew();
                var track = TrackGenerator.Generate(template, p, seed);
                var check = track is null ? new Nitrogenesis.Sim.Gen.GenCheck(GenProblem.NoRoom, 0, "the route did not fit on the map") : TrackGenerator.Validate(track, p);
                ms += watch.Elapsed.TotalMilliseconds;
                if (check.Valid)
                {
                    valid++;
                    times.Add(check.ReferenceSeconds);
                }
                else
                {
                    problems[check.Problem] = problems.GetValueOrDefault(check.Problem) + 1;
                    retries.Add(TrackGenerator.GenerateValid(template, p, seed).Attempts);
                }
                if (verbose) Console.WriteLine($"  {template} seed {seed}: {(check.Valid ? "ok  " : "FAIL")} {check.Text}");
            }
            times.Sort();
            double share = (double)valid / seeds;
            ok &= share >= Required;
            string timeText = times.Count > 0 ? $"times {times[0]:F1} / {times[times.Count / 2]:F1} / {times[^1]:F1} s (min/median/max)" : "no valid track";
            string problemText = problems.Count == 0 ? "" : "  problems: " + string.Join(", ", problems.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}"));
            string retryText = retries.Count == 0 ? "" : $"  failing seeds needed {string.Join(", ", retries)} tries";
            Console.WriteLine($"{(share >= Required ? "ok  " : "FAIL")} {Templates.Name(template),-15} {p.Size} first try {valid,2}/{seeds} ({share,4:P0})  {timeText}  " +
                              $"{ms / seeds,4:F0} ms per seed{problemText}{retryText}");
        }
        Console.WriteLine($"{(ok ? "gen-check passed" : "gen-check FAILED")} in {total.Elapsed.TotalSeconds:F1} s");
        return ok ? 0 : 1;
    }
}
