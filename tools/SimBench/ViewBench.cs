using System.Diagnostics;
using Nitrogenesis.Sim.Core;
using Nitrogenesis.Sim.Io;
using Nitrogenesis.Sim.Racing;

/// <summary>
/// `view`: the app's simulation side without the renderer (PLAN §4, §9 "visuals on"). A <see cref="TrainingHost"/>
/// is driven by a 60 Hz frame loop exactly as the training view drives it (Frame, the short wait at 1× and slower,
/// a snapshot copy), and the actual speed and the simulation time per frame are reported.
/// </summary>
internal static class ViewBench
{
    public static int Run(string path, int population, int threads, double speed, int seconds, int warm)
    {
        var track = TrackFile.Load(path);
        using var host = new TrainingHost(track, new TrainingHostOptions
        {
            PopulationSize = population, Threads = threads, Seed = 1, Speed = speed,
        });
        TrainingStats s;
        do
        {
            Thread.Sleep(5);
            host.ReadStats(out s);
        } while (!s.Ready && s.Error is null);

        if (warm > 0)
        {
            // Train a few generations first (cars that drive further make heavier frames), then measure.
            host.SetSpeed(double.PositiveInfinity);
            while (s.Generation < warm && s.Error is null)
            {
                host.Frame(1.0 / 60);
                host.WaitForFrame(TimeSpan.FromSeconds(1));
                host.ReadStats(out s);
            }
            host.SetSpeed(speed);
            host.ReadStats(out s);
        }
        long ticksBefore = s.TotalTicks;
        var prev = new AgentSnapshot(population);
        var curr = new AgentSnapshot(population);
        const double frame = 1.0 / 60;
        long frequency = Stopwatch.Frequency, start = Stopwatch.GetTimestamp(), next = start;
        long lastTick = -1;
        int lastGeneration = s.Generation, frames = 0, late = 0, generations = 0;
        var simMs = new List<double>();
        double maxMainMs = 0;
        while (Stopwatch.GetTimestamp() - start < seconds * frequency)
        {
            long frameStart = Stopwatch.GetTimestamp();
            if (host.Frame(frame)) host.WaitForFrame(TimeSpan.FromMilliseconds(4));
            host.Snapshots!.CopyTo(prev, curr);
            host.ReadStats(out s);
            if (s.Error is not null) throw new InvalidOperationException(s.Error);
            maxMainMs = Math.Max(maxMainMs, (Stopwatch.GetTimestamp() - frameStart) * 1000.0 / frequency);
            if (s.Generation != lastGeneration)
            {
                generations += s.Generation - lastGeneration;
                lastGeneration = s.Generation;
            }
            if (curr.Tick != lastTick && s.TicksLastFrame > 0) simMs.Add(s.SimSecondsLastFrame * 1000);
            lastTick = curr.Tick;
            frames++;
            next += (long)(frame * frequency);
            long wait = next - Stopwatch.GetTimestamp();
            if (wait > 0) Thread.Sleep(TimeSpan.FromSeconds((double)wait / frequency));
            else late++;
        }
        host.ReadStats(out s);
        double actual = (s.TotalTicks - ticksBefore) / (60.0 * (Stopwatch.GetTimestamp() - start) / frequency);
        simMs.Sort();
        double P(double q) => simMs.Count == 0 ? 0 : simMs[Math.Min(simMs.Count - 1, (int)(q * simMs.Count))];
        Console.WriteLine($"{Path.GetFileNameWithoutExtension(path),-15} {population} cars, {threads} threads, target {SpeedController.Label(SpeedController.StepValue(SpeedController.IndexFor(speed)))}: " +
                          $"actual {actual:F1}×, {generations} generations in {seconds} s{(warm > 0 ? $" (from generation {warm})" : "")}, " +
                          $"sim per frame median {P(0.5):F2} ms, p95 {P(0.95):F2} ms, max {P(1):F2} ms; render thread max {maxMainMs:F2} ms; {late} late frames of {frames}");
        return 0;
    }
}
