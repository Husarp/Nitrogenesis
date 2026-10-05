using Nitrogenesis.Sim.Evolution;

public class StagnationTests
{
    private static readonly EvolutionSettings Defaults = new();
    private const float None = float.PositiveInfinity;

    [Fact]
    public void ProgressUnderOneCellOverTheWindowTriggersABoostOfFiveGenerations()
    {
        var t = new StagnationTracker();
        // 15 generations with +0.06 cells each = 0.9 cells over the window: stagnant at the 16th observation.
        for (int g = 0; g < 15; g++)
        {
            t.Observe(100f + 0.06f * g, None, Defaults);
            Assert.False(t.JustTriggered);
        }
        t.Observe(100f + 0.06f * 15, None, Defaults);
        Assert.True(t.JustTriggered);
        Assert.Equal(5, t.BoostRemaining);
        for (int i = 0; i < 5; i++) Assert.True(t.ConsumeBoost(Defaults));
        Assert.False(t.ConsumeBoost(Defaults));
    }

    [Fact]
    public void ProgressOfOneCellOrMoreIsNotStagnant()
    {
        var t = new StagnationTracker();
        for (int g = 0; g < 40; g++)
        {
            t.Observe(10f + 0.07f * g, None, Defaults); // 1.05 cells per 15 generations
            Assert.False(t.JustTriggered);
        }
    }

    [Fact]
    public void AfterAFinishTheTimeRuleApplies()
    {
        var t = new StagnationTracker();
        // Finished from the start; best time 1000 ticks improving by 0.1 tick per generation: 1.5 ticks per window,
        // which is below 0.2 % of 1000 = 2 ticks → stagnant even though progress is maxed.
        for (int g = 0; g < 15; g++) t.Observe(200f, 1000f - 0.1f * g, Defaults);
        Assert.False(t.JustTriggered);
        t.Observe(200f, 1000f - 1.5f, Defaults);
        Assert.True(t.JustTriggered);
    }

    [Fact]
    public void TimeGainOfAtLeastMaxOneTickOrPointTwoPercentIsNotStagnant()
    {
        var t = new StagnationTracker();
        for (int g = 0; g < 60; g++)
        {
            t.Observe(200f, 1000f - 0.15f * g, Defaults); // 2.25 ticks per window ≥ 2
            Assert.False(t.JustTriggered);
        }
        var shortRun = new StagnationTracker();
        for (int g = 0; g < 60; g++)
        {
            shortRun.Observe(50f, 300f - 0.07f * g, Defaults); // 1.05 ticks per window ≥ max(1, 0.6)
            Assert.False(shortRun.JustTriggered);
        }
    }

    [Fact]
    public void AFirstFinishInsideTheWindowCountsAsImprovement()
    {
        var t = new StagnationTracker();
        for (int g = 0; g < 10; g++) t.Observe(50f, None, Defaults);
        for (int g = 10; g < 25; g++)
        {
            t.Observe(50f, 900f, Defaults);
            Assert.False(t.JustTriggered);
        }
        t.Observe(50f, 900f, Defaults); // now the window starts after the first finish: flat time → stagnant
        Assert.True(t.JustTriggered);
    }

    [Fact]
    public void WindowRestartsAfterATrigger()
    {
        var t = new StagnationTracker();
        int triggers = 0;
        for (int g = 0; g < 16 + 15; g++)
        {
            t.Observe(5f, None, Defaults);
            if (t.JustTriggered) triggers++;
            t.ConsumeBoost(Defaults);
        }
        Assert.Equal(1, triggers);
        t.Observe(5f, None, Defaults); // 16 observations since the trigger
        Assert.True(t.JustTriggered);
    }

    [Fact]
    public void TurningTheBoostOffStopsAndCancelsIt()
    {
        var off = Defaults with { StagnationBoost = false };
        var t = new StagnationTracker();
        for (int g = 0; g < 30; g++) t.Observe(5f, None, off);
        Assert.Equal(0, t.BoostRemaining);

        for (int g = 0; g < 16; g++) t.Observe(5f, None, Defaults);
        Assert.Equal(5, t.BoostRemaining);
        Assert.False(t.ConsumeBoost(off));
        t.Observe(5f, None, off);
        Assert.Equal(0, t.BoostRemaining);
    }

    [Fact]
    public void ResetForgetsTheSegment()
    {
        var t = new StagnationTracker();
        for (int g = 0; g < 10; g++) t.Observe(5f, 400f, Defaults);
        t.Reset();
        Assert.Equal(0f, t.BestProgress);
        Assert.Equal(None, t.BestTime);
        for (int g = 0; g < 15; g++) t.Observe(1f, None, Defaults);
        Assert.False(t.JustTriggered);
    }

    [Fact]
    public void ShorterWindowSettingTakesEffect()
    {
        var t = new StagnationTracker();
        var five = Defaults with { StagnationWindow = 5 };
        for (int g = 0; g < 5; g++) t.Observe(5f, None, five);
        Assert.False(t.JustTriggered);
        t.Observe(5f, None, five);
        Assert.True(t.JustTriggered);
    }
}
