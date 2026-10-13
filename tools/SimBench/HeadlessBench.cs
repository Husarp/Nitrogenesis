using System.Diagnostics;
using Nitrogenesis.Sim.History;
using Nitrogenesis.Sim.Io;
using Nitrogenesis.Sim.Racing;

/// <summary>
/// `headless`: "Train to…" without visuals through the training host (PLAN §4), the path the app's progress screen
/// uses: generations/s and agent-ticks/s including the history (population copies, encoded recordings, thinning) and,
/// with --save, a session saved all the time (autosave interval 0, the worst case) in a temp folder.
/// </summary>
internal static class HeadlessBench
{
    public static int Run(string path, int population, int threads, int generations, bool save)
    {
        var track = TrackFile.Load(path);
        string? dir = save ? Path.Combine(Path.GetTempPath(), "nitrogenesis-headless-" + Guid.NewGuid().ToString("N")) : null;
        try
        {
            using var host = new TrainingHost(track, new TrainingHostOptions
            {
                PopulationSize = population, Threads = threads, HeadlessThreads = threads, Seed = 1,
                Folder = dir is null ? null : new SessionFolder(dir), AutosaveInterval = TimeSpan.Zero,
            });
            TrainingStats s;
            do
            {
                Thread.Sleep(2);
                host.ReadStats(out s);
            } while (!s.Ready && s.Error is null);
            long ticksBefore = s.TotalTicks;
            var watch = Stopwatch.StartNew();
            host.TrainTo(TrainToCondition.ToGeneration(generations));
            do
            {
                Thread.Sleep(5);
                host.ReadStats(out s);
            } while (s.Error is null && (s.TrainToRuns == 0 || s.Headless));
            double seconds = watch.Elapsed.TotalSeconds;
            if (s.Error is not null) throw new InvalidOperationException(s.Error);
            var session = host.Session!;
            Console.WriteLine($"{Path.GetFileName(path)}: {population} cars, {threads} threads, {generations} generations{(save ? ", saving continuously" : "")}: " +
                              $"{generations / seconds:F1} gens/s, {(s.TotalTicks - ticksBefore) / seconds / 60:F0}× game speed; " +
                              $"history {session.History.Bytes / 1e6:F1} MB, {session.History.SnapshotCount} snapshots; saved {(save ? s.SavedAtUtc.ToString("HH:mm:ss.fff") : "—")}");
            return 0;
        }
        finally
        {
            if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
