/// <summary>Zero-allocation checks (PLAN §2.1) that are robust against one-off runtime work.</summary>
internal static class Allocations
{
    /// <summary>
    /// Runs <paramref name="window"/> up to <paramref name="attempts"/> times on this thread and passes as soon as
    /// one run allocates nothing. Code that allocates per call fails every run; a one-off allocation by the
    /// runtime (e.g. tiered-JIT promotion landing inside a run while parallel tests keep the JIT busy) does not.
    /// </summary>
    public static void AssertSteadyStateFree(Action window, int attempts = 5)
    {
        long least = long.MaxValue;
        for (int i = 0; i < attempts; i++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            window();
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            if (bytes == 0) return;
            least = Math.Min(least, bytes);
        }
        Assert.Fail($"Every run allocated; the least was {least} bytes.");
    }
}
