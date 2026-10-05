using Nitrogenesis.Sim.Brain;
using Nitrogenesis.Sim.Core;
using Nitrogenesis.Sim.Map;
using Nitrogenesis.Sim.Racing;

public class RacingModeTests
{
    /// <summary>Fixed outputs for every agent and tick.</summary>
    private sealed class ConstantPolicy(float throttle, float steer) : IAgentPolicy
    {
        public void Act(int agent, ReadOnlySpan<float> inputs, Span<float> outputs)
        {
            outputs[0] = throttle;
            outputs[1] = steer;
        }
    }

    /// <summary>
    /// A deterministic scripted driver that depends on the agent, its tick and its inputs (so sensor values feed
    /// back into the trajectory and the hash pins them too). Integer patterns only; allocation-free.
    /// </summary>
    private sealed class ScriptedPolicy(RacingMode mode) : IAgentPolicy
    {
        public void Act(int agent, ReadOnlySpan<float> inputs, Span<float> outputs)
        {
            int t = mode.Tick(agent);
            int phase = (t / (23 + 7 * agent)) % 6;
            outputs[0] = t % 150 < 120 ? 1f : -0.6f;
            // Steer towards the side with more room (ray 1 = left-front, ray 5 = right-front), plus a wobble
            // that grows with the agent index, so the later agents spin off onto the grass and into walls.
            float lean = inputs[3] - inputs[11];
            outputs[1] = Math.Clamp(lean * -2f + (phase - 2.5f) * (0.1f + 0.12f * agent), -1f, 1f);
        }
    }

    private static RacingTrack Sprint(RacingSettings? s = null) => new(TestTracks.Load("sprint.track"), s ?? new RacingSettings());

    private static ulong Mix(ulong h, uint v) => (h ^ v) * 0x100000001B3UL;
    private static ulong Mix(ulong h, float v) => Mix(h, BitConverter.SingleToUInt32Bits(v));

    /// <summary>
    /// FNV-1a over every agent's full state and inputs after every tick: x, y, vx, vy, heading, yaw rate,
    /// status, and all sensor values.
    /// </summary>
    private static ulong RunAndHash(RacingMode mode, IAgentPolicy policy, int ticks)
    {
        ulong h = 0xCBF29CE484222325UL;
        var all = AgentRange.All(mode.AgentCount);
        for (int t = 0; t < ticks; t++)
        {
            mode.Step(all, 1, policy);
            for (int i = 0; i < mode.AgentCount; i++)
            {
                var c = mode.Car(i);
                h = Mix(Mix(Mix(Mix(Mix(Mix(Mix(h, c.X), c.Y), c.Vx), c.Vy), (uint)c.Heading), c.YawRate), (uint)mode.Status(i));
                for (int k = 0; k < mode.InputCount; k++) h = Mix(h, mode.Inputs[i * mode.InputCount + k]);
            }
        }
        for (int i = 0; i < mode.AgentCount; i++) h = Mix(Mix(h, mode.Score(i)), mode.FinishTicks(i));
        return h;
    }

    /// <summary>
    /// The trajectory hash of the scripted run on sprint.track (PLAN §2.1, §9). It must be identical on Linux and
    /// Windows: SimVersion 1's value was checked on 2026-10-04 with a win-x64 self-contained build of the same run
    /// under Wine; for SimVersion 2 the population hash and reference times (which run the same tick math) were
    /// checked the same way on 2026-10-05, and the packaged app's --selftest will check it under the Windows runtime
    /// in M8. If it changes, the tick math changed: bump SimVersion and update this constant on purpose.
    /// </summary>
    public const ulong ExpectedSprintTrajectoryHash = 0xB683C7A9FE4FD406UL;

    [Fact]
    public void TrajectoryHashMatchesPinnedConstant()
    {
        var mode = new RacingMode(Sprint(), 6, 1800);
        ulong hash = RunAndHash(mode, new ScriptedPolicy(mode), 1800);
        Assert.Equal($"0x{ExpectedSprintTrajectoryHash:X16}", $"0x{hash:X16}");
    }

    [Fact]
    public void ScriptedRunCoversRealDriving()
    {
        // Guards the pinned hash against testing nothing: the script drives, finishes, spins onto grass and stalls.
        var mode = new RacingMode(Sprint(), 6, 1800);
        RunAndHash(mode, new ScriptedPolicy(mode), 1800);
        for (int i = 0; i < 6; i++) Assert.True(mode.DistanceDriven(i) > 20f, $"agent {i} drove {mode.DistanceDriven(i)}");
        Assert.Equal(AgentStatus.Finished, mode.Status(0));
        Assert.Equal(AgentStatus.Stalled, mode.Status(5));
    }

    [Fact]
    public void SameInputsTwiceGiveIdenticalTrajectories()
    {
        var a = new RacingMode(Sprint(), 4, 900);
        var b = new RacingMode(Sprint(), 4, 900);
        Assert.Equal(RunAndHash(a, new ScriptedPolicy(a), 900), RunAndHash(b, new ScriptedPolicy(b), 900));
        // Reset brings everything back: a second run on the same object matches too.
        a.Reset(AgentRange.All(4));
        var c = new RacingMode(Sprint(), 4, 900);
        Assert.Equal(RunAndHash(c, new ScriptedPolicy(c), 900), RunAndHash(a, new ScriptedPolicy(a), 900));
    }

    [Fact]
    public void ChunkingAndThreadsDoNotChangeResults()
    {
        // One range for all ticks vs. agents split over threads stepping K ticks at a time with no barrier.
        const int n = 64, ticks = 600;
        var serial = new RacingMode(Sprint(), n, ticks);
        serial.Step(AgentRange.All(n), ticks, new ScriptedPolicy(serial));

        var parallel = new RacingMode(Sprint(), n, ticks);
        var policy = new ScriptedPolicy(parallel);
        for (int done = 0; done < ticks; done += 37)
        {
            int k = Math.Min(37, ticks - done);
            Parallel.For(0, n / 8, w => parallel.Step(new AgentRange(w * 8, 8), k, policy));
        }

        for (int i = 0; i < n; i++)
        {
            Assert.Equal(serial.Car(i), parallel.Car(i));
            Assert.Equal(serial.Status(i), parallel.Status(i));
            Assert.Equal(serial.Score(i), parallel.Score(i));
        }
    }

    [Fact]
    public void StepDoesNotAllocate()
    {
        var mode = new RacingMode(Sprint(new RacingSettings { DirectionHint = true }), 32, 2000);
        var policy = new ScriptedPolicy(mode);
        mode.Step(AgentRange.All(32), 5, policy); // warm up (JIT, static tables)
        Allocations.AssertSteadyStateFree(() =>
        {
            mode.Step(AgentRange.All(32), 120, policy);
            mode.Sense(AgentRange.All(32));
            mode.Advance(AgentRange.All(32));
        });
    }

    [Fact]
    public void ResetPutsCarsAtTheStart()
    {
        var rt = Sprint();
        var mode = new RacingMode(rt, 3, 600);
        mode.Step(AgentRange.All(3), 100, new ConstantPolicy(1f, 0.2f));
        mode.Reset(new AgentRange(1, 2));
        Assert.Equal(100, mode.Tick(0));
        for (int i = 1; i < 3; i++)
        {
            var c = mode.Car(i);
            Assert.Equal((22.5f, 72.5f, 0, 0f, 0f), (c.X, c.Y, c.Heading, c.Vx, c.Vy));
            Assert.Equal(0, mode.Tick(i));
            Assert.Equal(AgentStatus.Running, mode.Status(i));
            Assert.Equal(0f, mode.Score(i));
            Assert.Equal(0f, mode.DistanceDriven(i));
        }
    }

    [Fact]
    public void ACarThatDoesNotMoveStallsAfterGraceAndStallTime()
    {
        var mode = new RacingMode(Sprint(), 1, 6000);
        mode.Step(AgentRange.All(1), 10_000, new ConstantPolicy(0f, 0f));
        Assert.Equal(AgentStatus.Stalled, mode.Status(0));
        Assert.Equal(90 + 180, mode.Tick(0));
    }

    [Fact]
    public void TimeLimitStopsTheRun()
    {
        var mode = new RacingMode(Sprint(), 1, 100);
        mode.Step(AgentRange.All(1), 1000, new ConstantPolicy(1f, 0f));
        Assert.Equal(AgentStatus.TimedOut, mode.Status(0));
        Assert.Equal(100, mode.Tick(0));
        float score = mode.Score(0);
        Assert.True(score > 0f && score < 1f);
        Assert.Equal(mode.Progress(0) - 0.01f * 100 / 100, score, 5); // improved on the last tick
    }

    [Fact]
    public void DrivingIntoDangerCrashes()
    {
        var t = TestMaps.Parse(
            "###########",
            "#.........#",
            "#.........#",
            "#..S.....x#",
            "#.........#",
            "#........F#",
            "###########");
        var mode = new RacingMode(new RacingTrack(t, new RacingSettings()), 1, 600);
        mode.Step(AgentRange.All(1), 600, new ConstantPolicy(1f, 0f));
        Assert.Equal(AgentStatus.Crashed, mode.Status(0));
    }

    [Fact]
    public void FinishingScoresAboveTwoWithSubTickTime()
    {
        var g = new TrackBuilder(new Grid(80, 20)).Fill(CellType.Road).Rect(60, 0, 79, 19, CellType.Finish).Grid;
        var mode = new RacingMode(new RacingTrack(new Track("strip", g, new TrackStart(10.5f, 10.5f, 0)), new RacingSettings()), 1, 1200);
        mode.Step(AgentRange.All(1), 1200, new ConstantPolicy(1f, 0f));
        Assert.Equal(AgentStatus.Finished, mode.Status(0));
        float ft = mode.FinishTicks(0);
        Assert.Equal(mode.Tick(0) - 1, (int)MathF.Floor(ft));
        Assert.True(ft % 1f != 0f);
        Assert.Equal(2f + (1200f - ft) / 1200f, mode.Score(0), 6);
        Assert.Equal(1f, mode.Progress(0));
    }

    [Fact]
    public void MemoryInputsReportLastOutputsElapsedTimeAndDistance()
    {
        var rt = Sprint();
        var mode = new RacingMode(rt, 1, 600);
        mode.Step(AgentRange.All(1), 60, new ConstantPolicy(0.8f, 0f));
        mode.Sense(AgentRange.All(1));
        var inputs = mode.Inputs;
        Assert.Equal(0.8f, inputs[18]);
        Assert.Equal(0f, inputs[19]);
        Assert.Equal(60f / 600f, inputs[20], 6);
        Assert.Equal(mode.DistanceDriven(0) / rt.StartDistance, inputs[21], 6);
        Assert.Equal(mode.Car(0).X - 22.5f, mode.DistanceDriven(0), 3); // straight line from the start
    }

    [Fact]
    public void SnapshotCopiesPositionHeadingAndStatus()
    {
        var mode = new RacingMode(Sprint(), 4, 600);
        mode.Step(new AgentRange(0, 2), 30, new ConstantPolicy(1f, 0.5f));
        var snap = new AgentSnapshot(4);
        mode.WriteSnapshot(AgentRange.All(4), snap);
        for (int i = 0; i < 4; i++)
        {
            var c = mode.Car(i);
            Assert.Equal((c.X, c.Y), (snap.X[i], snap.Y[i]));
            Assert.Equal(c.Heading * FastMath.RadiansPerUnit, snap.Angle[i]);
            Assert.Equal(AgentStatus.Running, snap.Status[i]);
        }
        Assert.NotEqual(snap.X[0], snap.X[3]);
    }
}
