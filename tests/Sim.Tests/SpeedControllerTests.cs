using Nitrogenesis.Sim.Core;

public class SpeedControllerTests
{
    [Fact]
    public void StepsMatchThePlanAndStartAtOne()
    {
        // PLAN §4: 0.01, 0.05, 0.1, 0.25, 0.5, 1, 2, 5, 10, 25, 50, 100, MAX.
        double[] expected = [0.01, 0.05, 0.1, 0.25, 0.5, 1, 2, 5, 10, 25, 50, 100];
        Assert.Equal(expected.Length, SpeedController.StepCount);
        for (int i = 0; i < expected.Length; i++) Assert.Equal(expected[i], SpeedController.StepValue(i));
        Assert.True(double.IsPositiveInfinity(SpeedController.StepValue(SpeedController.MaxIndex)));
        var c = new SpeedController();
        Assert.Equal(1, c.TargetSpeed);
        Assert.True(c.Interpolates);
    }

    [Fact]
    public void FasterAndSlowerStopAtTheEnds()
    {
        var c = new SpeedController();
        for (int i = 0; i < 30; i++) c.Faster();
        Assert.True(c.IsMax);
        Assert.False(c.Interpolates);
        for (int i = 0; i < 30; i++) c.Slower();
        Assert.Equal(0.01, c.TargetSpeed);
    }

    [Theory]
    [InlineData(1.0, 5)]
    [InlineData(0.3, 3)]
    [InlineData(100.0, 11)]
    [InlineData(150.0, 12)]
    [InlineData(double.PositiveInfinity, 12)]
    [InlineData(0.001, 0)]
    public void IndexForSnapsToTheNearestStep(double speed, int index) => Assert.Equal(index, SpeedController.IndexFor(speed));

    [Theory]
    [InlineData(0.01, "0.01×")]
    [InlineData(0.25, "0.25×")]
    [InlineData(1, "1×")]
    [InlineData(100, "100×")]
    [InlineData(64.3, "64.3×")]
    [InlineData(double.PositiveInfinity, "MAX")]
    public void Labels(double speed, string label) => Assert.Equal(label, SpeedController.Label(speed));

    [Fact]
    public void TicksDueGrowsWithRealTimeTimesSpeed()
    {
        var c = new SpeedController { Index = SpeedController.IndexFor(10) };
        c.Advance(1.0 / 60);
        Assert.Equal(10, c.WholeTicks);
        c.Consume(10);
        Assert.Equal(0, c.WholeTicks);
        Assert.Equal(0f, c.Alpha, 1e-6f);
    }

    [Fact]
    public void AlphaIsTheFractionLeftAfterTheWholeTicks()
    {
        // 0.25× at 60 FPS: a quarter tick per frame; a tick every 4th frame, alpha 0, .25, .5, .75, 0, …
        var c = new SpeedController { Index = SpeedController.IndexFor(0.25) };
        float[] alphas = new float[8];
        int ticks = 0;
        for (int f = 0; f < 8; f++)
        {
            c.Advance(1.0 / 60);
            int n = c.WholeTicks;
            ticks += n;
            c.Consume(n);
            alphas[f] = c.Alpha;
        }
        Assert.Equal(2, ticks);
        float[] expected = [0.25f, 0.5f, 0.75f, 0f, 0.25f, 0.5f, 0.75f, 0f];
        for (int f = 0; f < 8; f++) Assert.Equal(expected[f], alphas[f], 1e-6f);
    }

    [Fact]
    public void SlowSpeedIsSmooth()
    {
        // 0.01×: a hundredth of a tick per frame, so 6000 frames make 60 ticks, and the alpha rises evenly in between.
        var c = new SpeedController { Index = 0 };
        int ticks = 0;
        float last = 0;
        for (int f = 0; f < 6000; f++)
        {
            c.Advance(1.0 / 60);
            int n = c.WholeTicks;
            ticks += n;
            c.Consume(n);
            float step = c.Alpha - last;
            if (n == 0) Assert.InRange(step, 0.0099f, 0.0101f);
            last = c.Alpha;
        }
        Assert.InRange(ticks, 59, 60);
    }

    [Fact]
    public void BacklogIsCappedWhenTheSimFallsBehind()
    {
        var c = new SpeedController { Index = SpeedController.IndexFor(100) };
        for (int f = 0; f < 100; f++)
        {
            c.Advance(1.0 / 60);
            c.Consume(Math.Min(c.WholeTicks, 64)); // the sim manages only 64 ticks per frame
        }
        Assert.True(c.TicksDue <= 60 * 100 * SpeedController.MaxBacklogSeconds + 1e-9, $"backlog {c.TicksDue}");

        // A long hitch at 1× does not make it race ahead afterwards.
        var d = new SpeedController();
        d.Advance(2.0);
        Assert.True(d.WholeTicks <= Math.Max(2, 60 * SpeedController.MaxBacklogSeconds));
    }

    [Fact]
    public void PauseFreezesAndStepAddsExactlyOneTick()
    {
        var c = new SpeedController { Index = SpeedController.IndexFor(0.5) };
        c.Advance(1.0 / 60);
        float alpha = c.Alpha;
        c.Paused = true;
        c.Advance(1.0);
        Assert.Equal(0, c.WholeTicks);
        Assert.Equal(alpha, c.Alpha);
        c.StepOnce();
        Assert.Equal(1, c.WholeTicks);
        c.Consume(1);
        Assert.Equal(alpha, c.Alpha);
        c.Paused = false;
        c.StepOnce(); // ignored while running
        Assert.Equal(0, c.WholeTicks);
    }

    [Fact]
    public void MaxAsksForEverythingAndLeavingItStartsFresh()
    {
        var c = new SpeedController { Index = SpeedController.MaxIndex };
        c.Advance(1.0 / 60);
        Assert.Equal(int.MaxValue, c.WholeTicks);
        c.Consume(12345);
        Assert.Equal(int.MaxValue, c.WholeTicks);
        c.Paused = true;
        Assert.Equal(0, c.WholeTicks);
        c.StepOnce();
        Assert.Equal(1, c.WholeTicks);
        c.Consume(1);
        c.Paused = false;
        c.Advance(1.0 / 60);
        Assert.Equal(int.MaxValue, c.WholeTicks);
        c.Slower();
        Assert.Equal(100, c.TargetSpeed);
        Assert.Equal(0, c.WholeTicks);
    }

    [Fact]
    public void PauseDropsTheBacklogSoStepRunsOneTick()
    {
        // 100× that has fallen behind: a backlog of whole ticks is owed when Space is pressed.
        var c = new SpeedController { Index = SpeedController.IndexFor(100) };
        c.Advance(0.1);
        c.Consume(100);
        Assert.True(c.WholeTicks > 100);
        c.Paused = true;
        Assert.Equal(0, c.WholeTicks);
        c.Advance(1.0);
        Assert.Equal(0, c.WholeTicks);
        c.StepOnce();
        Assert.Equal(1, c.WholeTicks);

        // A tick that was already running when the pause dropped it keeps the render fraction.
        var d = new SpeedController { Index = SpeedController.IndexFor(0.5) };
        for (int f = 0; f < 3; f++) d.Advance(1.0 / 60); // 1.5 ticks due
        Assert.Equal(1, d.WholeTicks);
        d.Paused = true;
        d.Consume(1);
        Assert.Equal(0, d.WholeTicks);
        Assert.Equal(0.5f, d.Alpha, 1e-6f);
    }

    [Fact]
    public void AlphaNeverGoesBackBeforeTheTicksAreConsumed()
    {
        // At 0.25× a frame can be drawn before the sim has run the tick that became due (the bounded wait timed
        // out): the pair is still the old one, so the alpha must not wrap back to the small fraction.
        var c = new SpeedController { Index = SpeedController.IndexFor(0.25) };
        float last = 0;
        for (int f = 0; f < 40; f++)
        {
            c.Advance(1.0 / 60);
            Assert.True(c.Alpha >= last, $"frame {f}: alpha {c.Alpha} < {last}");
            last = c.Alpha;
            if (f % 5 == 4) // the sim catches up only every 5th frame
            {
                c.Consume(c.WholeTicks);
                last = c.Alpha; // a new pair: the alpha restarts from the fraction
            }
        }
        c.Advance(5.0 / 60); // a whole tick owed, pair stale: curr is drawn
        Assert.Equal(1f, c.Alpha);
    }

    [Fact]
    public void MeterMeasuresTicksPerRealSecond()
    {
        var m = new SpeedMeter();
        m.AddTime(0, paused: false, 100);
        for (int f = 0; f < 60; f++)
        {
            m.AddTicks(64);
            m.AddTime(1.0 / 60, paused: false, 100);
        }
        Assert.Equal(64, m.Actual, 1);
        m.AddTime(1.0 / 60, paused: true, 100);
        Assert.Equal(0, m.Actual);
    }

    [Theory]
    [InlineData(0.01)]
    [InlineData(0.05)]
    [InlineData(0.1)]
    [InlineData(0.25)]
    [InlineData(1)]
    [InlineData(100)]
    public void MeterReadsOnTimeSlowSpeedsAsOnTime(double speed)
    {
        // A sim that is exactly on time at 60 FPS must never read below 90 % of the target (the HUD's warning).
        var c = new SpeedController { Index = SpeedController.IndexFor(speed) };
        var m = new SpeedMeter();
        for (int f = 0; f < 60 * 120; f++)
        {
            c.Advance(1.0 / 60);
            m.AddTime(1.0 / 60, paused: false, c.TargetSpeed);
            int n = c.WholeTicks;
            c.Consume(n);
            m.AddTicks(n);
            if (m.Actual > 0) Assert.True(m.Actual >= 0.9 * speed, $"{speed}× at frame {f}: read {m.Actual}");
        }
        Assert.True(m.Actual > 0);
    }

    [Fact]
    public void MeterStartsAFreshWindowWhenTheTargetChanges()
    {
        var m = new SpeedMeter();
        m.AddTime(0, paused: false, 1);
        for (int f = 0; f < 31; f++)
        {
            m.AddTicks(1);
            m.AddTime(1.0 / 60, paused: false, 1);
        }
        Assert.Equal(1, m.Actual, 2);
        m.AddTicks(1);
        m.AddTime(0, paused: false, 100); // the old window's 1× ticks are not mixed into the 100× reading
        Assert.Equal(0, m.Actual);
        for (int f = 0; f < 31; f++)
        {
            m.AddTicks(100);
            m.AddTime(1.0 / 60, paused: false, 100);
        }
        Assert.Equal(100, m.Actual, 0);
    }
}
