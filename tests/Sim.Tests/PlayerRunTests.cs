using Nitrogenesis.Sim.Core;
using Nitrogenesis.Sim.Racing;

public class PlayerControlsTests
{
    [Fact]
    public void KeysRampToFullIn6TicksAndBackIn6()
    {
        var c = new PlayerControls();
        var seen = new List<float>();
        for (int t = 0; t < 6; t++)
        {
            c.Tick(1, -1, 0f, 0f, 0f);
            seen.Add(c.Throttle);
            Assert.Equal(-c.Throttle, c.Steer);
        }
        Assert.Equal(1f, seen[^1]);
        Assert.True(seen[^2] < 1f);
        Assert.Equal(1f / 6, seen[0], 6);
        for (int t = 0; t < 5; t++) c.Tick(0, 0, 0f, 0f, 0f);
        Assert.True(c.Throttle > 0f);
        c.Tick(0, 0, 0f, 0f, 0f);
        Assert.Equal(0f, c.Throttle);
        Assert.Equal(0f, c.Steer);
        // Full reverse from +1 to −1 is 0.2 s at the same rate.
        for (int t = 0; t < 6; t++) c.Tick(1, 0, 0f, 0f, 0f);
        for (int t = 0; t < 12; t++) c.Tick(-1, 0, 0f, 0f, 0f);
        Assert.Equal(-1f, c.Throttle);
    }

    [Fact]
    public void GamepadIsUsedDirectlyOutsideTheDeadZone()
    {
        var c = new PlayerControls();
        c.Tick(0, 0, 0.1f, 0.03f, 0f); // inside both dead zones
        Assert.Equal((0f, 0f), (c.Throttle, c.Steer));
        c.Tick(0, 0, -1f, 1f, 0f);
        Assert.Equal((1f, -1f), (c.Throttle, c.Steer));
        c.Tick(0, 0, 0.6f, 0.525f, 0f);
        Assert.Equal(0.5f, c.Steer, 5);    // (0.6 − 0.2) / 0.8
        Assert.Equal(0.5f, c.Throttle, 5); // (0.525 − 0.05) / 0.95
        c.Tick(0, 0, 0f, 0f, 1f);          // left trigger = brake / reverse
        Assert.Equal(-1f, c.Throttle);
        c.Tick(1, 1, 0f, 1f, 1f);          // both triggers cancel out: the keys decide (ramp from −1)
        Assert.Equal(-1f + PlayerControls.RampPerTick, c.Throttle, 5);
        c.Tick(1, 1, -0.5f, 0f, 0f);       // the stick wins over the keys
        Assert.Equal(-0.375f, c.Steer, 5);
        Assert.Equal(0f, PlayerControls.Analog(float.NaN, 0.2f));
    }
}

public class PlayerRunTests
{
    private static RacingTrack Track(string file = "sprint.track") => new(TestTracks.Load(file), new RacingSettings());

    [Fact]
    public void CountdownHoldsTheCarThenItDrives()
    {
        var run = new PlayerRun(Track());
        Assert.Equal(PlayerPhase.Countdown, run.Phase);
        Assert.Equal(1, run.Attempts);
        var start = run.Car;
        for (int t = 0; t < PlayerRun.CountdownTicks; t++)
        {
            Assert.Equal(PlayerPhase.Countdown, run.Phase);
            Assert.Equal(0f, run.TimeSeconds(0.5f));
            run.Tick(1, 0, 0f, 0f, 0f);
            Assert.Equal(start, run.Car);
        }
        Assert.Equal(PlayerPhase.Driving, run.Phase);
        Assert.Equal(1f, run.Controls.Throttle); // the held key is fully on at the start signal
        for (int t = 0; t < 60; t++) run.Tick(1, 0, 0f, 0f, 0f);
        Assert.Equal(60, run.Ticks);
        Assert.Equal(60.5f / 60, run.TimeSeconds(0.5f), 5);
        Assert.True(run.Car.ForwardSpeed > 5f);
        Assert.NotEqual(run.PreviousCar, run.Car);
        Assert.InRange(run.Progress, 0.01f, 0.99f);
    }

    [Theory]
    [InlineData("sprint.track")]
    [InlineData("s_curve.track")]
    public void DrivenByTheReferencePilotItMatchesTheReferenceTime(string file)
    {
        // The player's car runs the same physics and finish rule as a training car: driven by the reference
        // driver's control law it finishes in exactly the reference time.
        var track = Track(file);
        var reference = ReferenceDriver.Run(track);
        Assert.True(reference.Finished);
        var run = new PlayerRun(track);
        run.Restart(0);
        var mirror = new RacingMode(track, 1, ReferenceDriver.MaxSeconds * RacingSettings.TicksPerSecond);
        var pilot = ReferenceDriver.CreatePilot(mirror);
        var outputs = new float[2];
        while (run.Phase == PlayerPhase.Driving && run.Ticks < ReferenceDriver.MaxSeconds * RacingSettings.TicksPerSecond)
        {
            mirror.SetCar(0, run.Car);
            pilot.Act(0, ReadOnlySpan<float>.Empty, outputs);
            run.Drive(outputs[0], outputs[1]);
        }
        Assert.Equal(PlayerPhase.Finished, run.Phase);
        Assert.Equal(reference.TimeSeconds, run.TimeSeconds());
        Assert.Equal(1f, run.Progress);
        Assert.True(run.NewBest);
        Assert.Equal(1, run.Finishes);
        Assert.Equal(run.FinishTicks, run.BestFinishTicks);
        // The route ends in the finish and has a point per tick (short run: no thinning).
        Assert.Equal(run.Ticks + 1, run.TrailCount);
        Assert.Equal(run.Car.X, run.TrailX[run.TrailCount - 1]);
    }

    [Fact]
    public void DangerCrashesAndRestartStartsAgainKeepingTheBest()
    {
        var map = TestMaps.Parse(
            "############",
            "#..........#",
            "#.S......xx#",
            "#..........#",
            "############");
        var run = new PlayerRun(new RacingTrack(map, new RacingSettings()));
        run.Restart(0);
        for (int t = 0; t < 600 && run.Phase == PlayerPhase.Driving; t++) run.Tick(1, 0, 0f, 0f, 0f);
        Assert.Equal(PlayerPhase.Crashed, run.Phase);
        int ticks = run.Ticks;
        float time = run.TimeSeconds();
        run.Tick(1, 0, 0f, 0f, 0f); // nothing moves after the crash
        Assert.Equal(ticks, run.Ticks);
        Assert.Equal(time, run.TimeSeconds(0.9f));
        Assert.Equal(run.Car, run.PreviousCar);

        run.Restart();
        Assert.Equal(PlayerPhase.Countdown, run.Phase);
        Assert.Equal(PlayerRun.RestartCountdownTicks, run.CountdownLeft);
        Assert.Equal(3, run.Attempts); // made, Restart(0), Restart()
        Assert.Equal((2.5f, 2.5f), (run.Car.X, run.Car.Y));
        Assert.Equal((0f, 0f), (run.Controls.Throttle, run.Controls.Steer));
        Assert.Equal(1, run.TrailCount);
        Assert.Equal(float.PositiveInfinity, run.BestFinishTicks);
    }

    [Fact]
    public void LongRunsKeepABoundedRoute()
    {
        // Circling on an open map for 5 minutes: the route thins out instead of growing past its room.
        var g = new Nitrogenesis.Sim.Map.TrackBuilder(new Nitrogenesis.Sim.Map.Grid(80, 80)).Fill(Nitrogenesis.Sim.Map.CellType.Road).Grid;
        var run = new PlayerRun(new RacingTrack(new Nitrogenesis.Sim.Map.Track("open", g, new Nitrogenesis.Sim.Map.TrackStart(40, 30, 0)), new RacingSettings()));
        run.Restart(0);
        const int ticks = 5 * 60 * 60;
        for (int t = 0; t < ticks; t++) run.Tick(1, 1, 0f, 0f, 0f);
        Assert.Equal(PlayerPhase.Driving, run.Phase);
        Assert.InRange(run.TrailCount, PlayerRun.MaxTrail / 2, PlayerRun.MaxTrail);
        Assert.Equal(8, run.TrailStride); // 18 000 ticks → 4096 × 8 > 18 000 > 4096 × 4
        Assert.Equal(ticks / run.TrailStride + 1, run.TrailCount);
        Assert.Equal((40f, 30f), (run.TrailX[0], run.TrailY[0]));
        Allocations.AssertSteadyStateFree(() =>
        {
            for (int t = 0; t < 100; t++) run.Tick(1, 1, 0f, 0f, 0f);
        });
    }
}
