using Nitrogenesis.Sim.Core;
using Nitrogenesis.Sim.Racing;

public class AgentSchedulerTests
{
    /// <summary>Full throttle, steer by agent; counts its calls per agent (each agent is only touched by one worker at a time).</summary>
    private sealed class CountingPolicy(int agents) : IAgentPolicy
    {
        public readonly int[] Calls = new int[agents];
        public int ThrowAt = -1;

        public void Act(int agent, ReadOnlySpan<float> inputs, Span<float> outputs)
        {
            if (agent == ThrowAt) throw new InvalidOperationException("boom");
            Calls[agent]++;
            outputs[0] = 1f;
            outputs[1] = (agent % 7 - 3) / 3f;
        }
    }

    private static RacingMode Sprint(int agents) =>
        new(new RacingTrack(TestTracks.Load("sprint.track"), new RacingSettings()), agents, 1200);

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    public void EveryRunningAgentGetsExactlyOneCallPerTick(int workers)
    {
        var mode = Sprint(150);
        var policy = new CountingPolicy(150);
        using var scheduler = new AgentScheduler(mode, workers);
        int ran = scheduler.Run(policy, 130); // 60 + 60 + 10
        Assert.Equal(130, ran);
        for (int i = 0; i < mode.AgentCount; i++) Assert.Equal(mode.Tick(i), policy.Calls[i]);
        Assert.Contains(policy.Calls, c => c == 130); // some cars are still running
    }

    [Fact]
    public void StopsEarlyWhenEveryAgentIsDone()
    {
        var mode = Sprint(40);
        using var scheduler = new AgentScheduler(mode, 4);
        int ran = scheduler.Run(new CountingPolicy(40), int.MaxValue);
        Assert.Equal(0, scheduler.CountAlive());
        // The ticks actually run: how far the last car got, not rounded up to the end of its phase.
        Assert.Equal(Enumerable.Range(0, 40).Max(mode.Tick), ran);
        Assert.True(ran <= 1200);
        Assert.Equal(0, scheduler.Run(new CountingPolicy(40), 100));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void RunAlongsideRunsBothJobsAndReportsFailures(int workers)
    {
        using var scheduler = new AgentScheduler(Sprint(10), workers);
        int side = 0, main = 0;
        scheduler.RunAlongside(() => side++, () => main++);
        Assert.Equal((1, 1), (side, main));

        var e = Assert.ThrowsAny<Exception>(() => scheduler.RunAlongside(() => throw new InvalidOperationException("side"), () => main++));
        Assert.True(e is InvalidOperationException || e.InnerException is InvalidOperationException);
        Assert.Equal(2, main);
        Assert.Throws<InvalidOperationException>(() => scheduler.RunAlongside(() => side++, () => throw new InvalidOperationException("main")));
        Assert.Equal(2, side); // the side job still ran to the end

        // The scheduler keeps working afterwards.
        Assert.Equal(10, scheduler.Run(new CountingPolicy(10), 10));
    }

    [Fact]
    public void ResultsAreIdenticalForAnyWorkerCount()
    {
        string Run(int workers)
        {
            var mode = Sprint(100);
            using var scheduler = new AgentScheduler(mode, workers);
            scheduler.Run(new CountingPolicy(100), int.MaxValue);
            return string.Join(";", Enumerable.Range(0, 100).Select(i =>
            {
                var c = mode.Car(i);
                return $"{BitConverter.SingleToUInt32Bits(c.X)},{BitConverter.SingleToUInt32Bits(c.Y)},{c.Heading},{mode.Status(i)},{BitConverter.SingleToUInt32Bits(mode.Score(i))}";
            }));
        }
        string one = Run(1);
        Assert.Equal(one, Run(2));
        Assert.Equal(one, Run(7));
    }

    [Fact]
    public void WorkerExceptionsReachTheCaller()
    {
        var mode = Sprint(200);
        using var scheduler = new AgentScheduler(mode, 4);
        var policy = new CountingPolicy(200) { ThrowAt = 190 };
        var e = Assert.ThrowsAny<Exception>(() => scheduler.Run(policy, 10));
        Assert.True(e is InvalidOperationException || e.InnerException is InvalidOperationException);
    }

    [Fact]
    public void RejectsBadWorkerCounts()
    {
        var mode = Sprint(1);
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentScheduler(mode, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentScheduler(mode, AgentScheduler.MaxWorkers + 1));
        var s = new AgentScheduler(mode, 2);
        s.Dispose();
        Assert.Throws<ObjectDisposedException>(() => s.Run(new CountingPolicy(1), 1));
    }

    [Fact]
    public void SnapshotBufferKeepsPrevAndCurr()
    {
        var mode = Sprint(5);
        var buffer = new SnapshotBuffer(5);
        var prev = new AgentSnapshot(5);
        var curr = new AgentSnapshot(5);
        Assert.Equal(0, buffer.CopyTo(prev, curr));

        buffer.Publish(mode, 0, 3);
        Assert.Equal(1, buffer.CopyTo(prev, curr));
        Assert.Equal(curr.X, prev.X);
        Assert.Equal(3, prev.GenerationId);

        using var scheduler = new AgentScheduler(mode, 1);
        scheduler.Run(new CountingPolicy(5), 30);
        buffer.Publish(mode, 30, 3);
        Assert.Equal(2, buffer.CopyTo(prev, curr));
        Assert.Equal(0, prev.Tick);
        Assert.Equal(30, curr.Tick);
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(mode.Car(i).X, curr.X[i]);
            Assert.Equal(mode.Status(i), curr.Status[i]);
            Assert.Equal(mode.Track.Track.Start.X, prev.X[i]);
        }
    }
}
