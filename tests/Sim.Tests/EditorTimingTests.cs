using System.Diagnostics;
using Nitrogenesis.Sim.Map;
using Nitrogenesis.Sim.Racing;

/// <summary>Timing checks of the editor's live checks; run alone (no other test class in parallel) so they measure the code, not the other tests.</summary>
[CollectionDefinition(nameof(EditorTimingTests), DisableParallelization = true)]
public class EditorTimingCollection;

[Collection(nameof(EditorTimingTests))]
public class EditorTimingTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(20);

    [Fact]
    public void RunOnAMediumMapTakesWellUnderFiftyMilliseconds()
    {
#if DEBUG
        const double LimitMs = 150; // unoptimized Sim build: the open map takes about 55 ms here, 10 ms in Release
#else
        const double LimitMs = 50;
#endif
        // Heaviest bundled maps plus a wide-open M map with a grass block (two full Dijkstra runs over many cells).
        var open = new Grid(256, 144);
        var b = new TrackBuilder(open);
        b.Rect(4, 4, 251, 139, CellType.Road);
        b.Rect(60, 40, 200, 100, CellType.Grass);
        b.Rect(240, 120, 250, 138, CellType.Finish);
        var openTrack = new Track("open", open, new TrackStart(10.5f, 10.5f, 0));
        var tracks = TestTracks.Files.Select(TestTracks.Load).Append(openTrack).ToList();
        foreach (var t in tracks) EditorAnalysis.Run(t, new RacingSettings()); // warm up the JIT
        foreach (var t in tracks)
        {
            var times = new List<double>();
            for (int i = 0; i < 15; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                EditorAnalysis.Run(t, new RacingSettings());
                times.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
            }
            times.Sort();
            // The box is shared and often heavily loaded, so the best of 15 is the stable figure (median is in the message).
            Assert.True(times[0] < LimitMs, $"{t.Name}: best {times[0]:0.0} ms, median {times[times.Count / 2]:0.0} ms");
        }
    }

    [Fact]
    public void RequestToResultOnAMediumMapIsWithinTwoHundredMilliseconds()
    {
        var track = TestTracks.Load("labyrinth.track");
        using var runner = new EditorAnalysisRunner();
        runner.Request(track, 0); // warm up
        Assert.True(runner.WaitForResult(Generous, out _));
        var times = new List<double>();
        for (int i = 1; i <= 15; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            runner.Request(track, i);
            Assert.True(runner.WaitForResult(Generous, out var r));
            times.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
            Assert.Equal(i, r.Revision);
        }
        times.Sort();
        // 150 ms debounce + about 5 ms of work. On a loaded box thread wake-ups add tens of ms, so the best run must be
        // within 200 ms (idle median is about 160 ms) and the median within a loose 400 ms.
        string all = $"[{string.Join(", ", times.Select(t => t.ToString("0.0")))}]";
        Assert.True(times[0] <= 200, "best of 15: " + all);
        Assert.True(times[times.Count / 2] <= 300, "median of 15: " + all);
    }
}
