using Nitrogenesis.Sim.Racing;

/// <summary>Runs alone, so the process-wide allocation counter sees only this test's threads.</summary>
[CollectionDefinition(nameof(ParallelAllocationTests), DisableParallelization = true)]
public class ParallelAllocationCollection;

[Collection(nameof(ParallelAllocationTests))]
public class ParallelAllocationTests
{
    /// <summary>
    /// The tick loop allocates nothing on any thread (PLAN §2.1), workers included: measured with the process-wide
    /// counter around RunTicks windows on 7 threads. As in <see cref="Allocations"/>, one clean window out of a few
    /// passes, so a one-off runtime allocation (JIT tiering, the test runner) does not fail it.
    /// </summary>
    [Fact]
    public void RunTicksOnSevenThreadsDoesNotAllocateOnAnyThread()
    {
        using var t = new RacingTraining(TestTracks.Load("s_curve.track"), new RacingSettings(), 10, populationSize: 200, threads: 7);
        t.Runner.RunGeneration(); // warm-up (JIT on every worker)
        t.Runner.RunGeneration();
        t.Runner.BeginGeneration();
        t.Runner.RunTicks(5);
        long least = long.MaxValue;
        for (int attempt = 0; attempt < 5 && least > 0; attempt++)
        {
            long before = GC.GetTotalAllocatedBytes(precise: true);
            t.Runner.RunTicks(120);
            least = Math.Min(least, GC.GetTotalAllocatedBytes(precise: true) - before);
        }
        Assert.Equal(0, least);
    }
}
