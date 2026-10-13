using System.Diagnostics;
using Nitrogenesis.Sim.Map;
using Nitrogenesis.Sim.Racing;

public class EditorAnalysisRunnerTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(20);
    private static readonly Track Small = TestMaps.Parse("#####", "#.S.#", "#..F#", "#####");

    private static EditorAnalysisResult Fake(Track t) => new() { Width = t.Grid.Width, Height = t.Grid.Height };

    [Fact]
    public void OnlyTheNewestOfSeveralQuickRequestsProducesAResult()
    {
        int calls = 0;
        using var runner = new EditorAnalysisRunner(analyzer: (t, s, ct) => { Interlocked.Increment(ref calls); return Fake(t); });
        for (long rev = 1; rev <= 6; rev++) runner.Request(Small, rev);
        Assert.True(runner.WaitForResult(Generous, out var r));
        Assert.Equal(6, r.Revision);
        Assert.False(runner.WaitForResult(TimeSpan.FromMilliseconds(500), out _)); // nothing else follows
        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.False(runner.TryTakeResult(out _));
    }

    [Fact]
    public void AnalysisStartsOnlyAfterTheDebounce()
    {
        long started = 0;
        using var runner = new EditorAnalysisRunner(analyzer: (t, s, ct) => { Interlocked.Exchange(ref started, Stopwatch.GetTimestamp()); return Fake(t); });
        long t0 = Stopwatch.GetTimestamp();
        runner.Request(Small, 1);
        Assert.True(runner.WaitForResult(Generous, out _));
        Assert.True(Stopwatch.GetElapsedTime(t0, Interlocked.Read(ref started)).TotalMilliseconds >= EditorAnalysisRunner.DebounceMilliseconds - 1);
    }

    [Fact]
    public void ANewRequestRestartsTheDebounce()
    {
        long started = 0;
        using var runner = new EditorAnalysisRunner(analyzer: (t, s, ct) => { Interlocked.Exchange(ref started, Stopwatch.GetTimestamp()); return Fake(t); });
        runner.Request(Small, 1);
        Thread.Sleep(100);
        long t1 = Stopwatch.GetTimestamp();
        runner.Request(Small, 2);
        Assert.True(runner.WaitForResult(Generous, out var r));
        Assert.Equal(2, r.Revision);
        Assert.True(Stopwatch.GetElapsedTime(t1, Interlocked.Read(ref started)).TotalMilliseconds >= EditorAnalysisRunner.DebounceMilliseconds - 1);
    }

    [Fact]
    public void ANewerRequestCancelsTheRunningAnalysis()
    {
        var started = new ManualResetEventSlim();
        var sawCancel = new ManualResetEventSlim();
        int calls = 0;
        using var runner = new EditorAnalysisRunner(debounceMilliseconds: 10, analyzer: (t, s, ct) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                started.Set();
                ct.WaitHandle.WaitOne(Generous); // runs "forever" until cancelled
                if (ct.IsCancellationRequested) sawCancel.Set();
                ct.ThrowIfCancellationRequested();
            }
            return Fake(t);
        });
        runner.Request(Small, 1);
        Assert.True(started.Wait(Generous));
        runner.Request(Small, 2);
        Assert.True(sawCancel.Wait(Generous));
        Assert.True(runner.WaitForResult(Generous, out var r));
        Assert.Equal(2, r.Revision);
        Assert.False(runner.WaitForResult(TimeSpan.FromMilliseconds(300), out _));
    }

    [Fact]
    public void AResultOfASupersededRequestIsNeverDelivered()
    {
        var started = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        int calls = 0;
        using var runner = new EditorAnalysisRunner(debounceMilliseconds: 10, analyzer: (t, s, ct) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                started.Set();
                release.Wait(Generous); // ignores the cancellation and returns a result anyway
            }
            return Fake(t);
        });
        runner.Request(Small, 1);
        Assert.True(started.Wait(Generous));
        runner.Request(Small, 2);
        release.Set();
        Assert.True(runner.WaitForResult(Generous, out var r));
        Assert.Equal(2, r.Revision);
        Assert.False(runner.WaitForResult(TimeSpan.FromMilliseconds(300), out _));
    }

    [Fact]
    public void ARequestMakesAnOlderUntakenResultDisappear()
    {
        using var runner = new EditorAnalysisRunner(debounceMilliseconds: 10, analyzer: (t, s, ct) => Fake(t));
        runner.Request(Small, 1);
        SpinWait.SpinUntil(() => !runner.IsBusy, Generous);
        runner.Request(Small, 2);
        Assert.False(runner.TryTakeResult(out _), "revision 1 must not be shown while revision 2 is pending");
        Assert.True(runner.WaitForResult(Generous, out var r));
        Assert.Equal(2, r.Revision);
    }

    [Fact]
    public void TryTakeResultReturnsAResultOnce()
    {
        using var runner = new EditorAnalysisRunner(debounceMilliseconds: 10);
        runner.Request(Small, 7);
        EditorAnalysisResult? got = null;
        Assert.True(SpinWait.SpinUntil(() => runner.TryTakeResult(out got), Generous));
        Assert.Equal(7, got!.Revision);
        Assert.False(runner.TryTakeResult(out _));
    }

    [Fact]
    public void AFailingAnalysisIsRecordedNotThrown()
    {
        using var runner = new EditorAnalysisRunner(debounceMilliseconds: 10, analyzer: (t, s, ct) => throw new InvalidOperationException("boom"));
        runner.Request(Small, 1);
        Assert.True(SpinWait.SpinUntil(() => runner.LastError is not null, Generous));
        Assert.False(runner.TryTakeResult(out _));
    }

    [Fact]
    public void DisposeStopsTheWorkerAndCancelsARunningAnalysis()
    {
        var started = new ManualResetEventSlim();
        var cancelled = new ManualResetEventSlim();
        var runner = new EditorAnalysisRunner(debounceMilliseconds: 10, analyzer: (t, s, ct) =>
        {
            started.Set();
            ct.WaitHandle.WaitOne(Generous);
            if (ct.IsCancellationRequested) cancelled.Set();
            ct.ThrowIfCancellationRequested();
            return Fake(t);
        });
        runner.Request(Small, 1);
        Assert.True(started.Wait(Generous));
        runner.Dispose();
        Assert.True(cancelled.IsSet);
        runner.Request(Small, 2); // ignored, no throw
        Assert.False(runner.TryTakeResult(out _));
        runner.Dispose(); // idempotent
    }
}
