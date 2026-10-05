using System.Diagnostics;
using Nitrogenesis.Sim.Io;
using Nitrogenesis.Sim.Racing;

/// <summary>
/// The §9 learn-suite on the bundled tracks: default settings, fixed seeds, a generation budget per track, and
/// the road check on grass_shortcut (see <see cref="LearnTest"/>). Every (track, seed) case must pass.
/// </summary>
/// <remarks>
/// Control cases check that the suite can tell working evolution from none, and must <em>fail</em>:
/// random search (a fresh random population every generation, same budget) on the hard tracks, and the road
/// check on grass_shortcut with grass as fast as road (then cutting across the grass really is shorter).
/// </remarks>
internal static class LearnSuite
{
    /// <summary>Training seeds; each track must pass with every one of them.</summary>
    private static readonly ulong[] Seeds = [1, 2, 3];

    private static readonly (string Track, int Budget, bool RoadRoute)[] Cases =
    [
        ("sprint", 30, false),
        ("s_curve", 100, false),
        ("hairpins", 100, false),
        ("wrong_turn", 150, false),
        ("labyrinth", 200, false),
        ("obstacle_field", 100, false),
        ("grass_shortcut", 100, true),
    ];

    /// <summary>Controls that must fail: (track, budget, road check, random search, grass speed fraction).</summary>
    private static readonly (string Track, int Budget, bool RoadRoute, bool RandomSearch, float GrassSpeed)[] Controls =
    [
        ("labyrinth", 200, false, true, 0.45f),
        ("obstacle_field", 100, false, true, 0.45f),
        ("grass_shortcut", 100, true, false, 1f),
    ];

    /// <param name="threads">Simulation threads.</param>
    /// <param name="only">Run only the cases and controls of this track (null = all).</param>
    public static int Run(int threads, string? only = null)
    {
        int failures = 0;
        var total = Stopwatch.StartNew();
        foreach (var (name, budget, roadRoute) in Cases.Where(c => only is null || c.Track == only))
            foreach (ulong seed in Seeds)
                if (!RunCase(name, budget, roadRoute, false, new RacingSettings(), seed, threads, expectPass: true)) failures++;

        Console.WriteLine("controls (must fail):");
        foreach (var (name, budget, roadRoute, randomSearch, grassSpeed) in Controls.Where(c => only is null || c.Track == only))
            foreach (ulong seed in Seeds)
                if (!RunCase(name, budget, roadRoute, randomSearch, new RacingSettings { GrassSpeedFraction = grassSpeed }, seed, threads, expectPass: false))
                    failures++;

        Console.WriteLine($"{(failures == 0 ? "learn-suite passed" : $"learn-suite FAILED ({failures} cases)")} in {total.Elapsed.TotalSeconds:F1} s");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>Runs one case and prints it; returns whether its outcome is the expected one.</summary>
    private static bool RunCase(string name, int budget, bool roadRoute, bool randomSearch, RacingSettings settings, ulong seed, int threads, bool expectPass)
    {
        var track = TrackFile.Load(TrackPaths.Resolve(name));
        var watch = Stopwatch.StartNew();
        var r = LearnTest.Run(track, budget, seed, threads, roadRoute, settings, randomSearch: randomSearch);
        double seconds = watch.Elapsed.TotalSeconds;
        bool ok = r.Passed == expectPass;
        string kind = randomSearch ? " random search" : settings.GrassSpeedFraction != new RacingSettings().GrassSpeedFraction
            ? $" grass speed {settings.GrassSpeedFraction:P0}" : "";
        string first = r.FirstFinish >= 0 ? $"first finish gen {r.FirstFinish,3}" : "no finish        ";
        string grass = r.GrassShare is { } g ? $"  grass {g,5:P1}" : "";
        string best = float.IsFinite(r.BestFinishSeconds) ? $"best {r.BestFinishSeconds,6:F2} s" : "best      —  ";
        Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name + kind,-28} seed {seed}  budget {budget,3}  {first}  {best}{grass}  " +
                          $"{r.GenerationsRun,3} gens in {seconds,5:F1} s ({r.GenerationsRun / seconds,5:F1} gens/s)" +
                          (r.Failure is null ? "" : $"  — {r.Failure}"));
        return ok;
    }
}
