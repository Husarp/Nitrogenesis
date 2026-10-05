using Nitrogenesis.Sim.Racing;

public class RacingFitnessTests
{
    [Fact]
    public void ProgressIsClampedToZeroToOne()
    {
        Assert.Equal(0.25f, RacingFitness.ProgressFraction(75f, 100f));
        Assert.Equal(0f, RacingFitness.ProgressFraction(120f, 100f));
        Assert.Equal(1f, RacingFitness.ProgressFraction(-1f, 100f));
        Assert.Equal(0f, RacingFitness.ProgressFraction(float.PositiveInfinity, 100f));
        Assert.Equal(0f, RacingFitness.ProgressFraction(5f, float.PositiveInfinity));
        Assert.Equal(0f, RacingFitness.ProgressFraction(0f, 0f));
    }

    [Fact]
    public void ScoresFollowThePlanFormulas()
    {
        Assert.Equal(0.5f - 0.01f * 300 / 1200, RacingFitness.UnfinishedScore(50f, 100f, 300, 1200), 6);
        Assert.Equal(2f + (1200f - 600.25f) / 1200f, RacingFitness.FinishedScore(600.25f, 1200), 6);
        // Reaching the same point sooner is better.
        Assert.True(RacingFitness.UnfinishedScore(50f, 100f, 100, 1200) > RacingFitness.UnfinishedScore(50f, 100f, 900, 1200));
        // The slowest possible finisher beats the best non-finisher.
        Assert.True(RacingFitness.FinishedScore(1200f, 1200) > RacingFitness.UnfinishedScore(0f, 100f, 0, 1200));
        // Faster finish, higher score.
        Assert.True(RacingFitness.FinishedScore(500.5f, 1200) > RacingFitness.FinishedScore(500.6f, 1200));
    }

    [Fact]
    public void StallClockStartsAfterGrace()
    {
        var p = RacingFitness.Progress.Start(100f);
        int stall = 180, grace = RacingFitness.StartGraceTicks;
        Assert.Equal(90, grace);
        for (int t = 1; t < grace + stall; t++)
        {
            p.Update(100f, t);
            Assert.False(p.IsStalled(t, stall));
        }
        Assert.True(p.IsStalled(grace + stall, stall));
    }

    [Fact]
    public void OnlyImprovementsAboveQuarterCellResetTheStallClock()
    {
        var p = RacingFitness.Progress.Start(100f);
        p.Update(99.8f, 200); // best improves, but by less than 0.25
        Assert.Equal(99.8f, p.BestDistance);
        Assert.Equal(200, p.BestTick);
        Assert.Equal(0, p.StallTick);
        Assert.True(p.IsStalled(270, 180));

        p.Update(99.7f, 260); // now 0.3 below the reference
        Assert.Equal(260, p.StallTick);
        Assert.Equal(99.7f, p.StallDistanceRef);
        Assert.False(p.IsStalled(439, 180));
        Assert.True(p.IsStalled(440, 180));
    }

    [Fact]
    public void TimeLimitResolvesFromSettingReferenceOrEstimate()
    {
        var auto = new RacingSettings();
        Assert.Equal(1500, RacingFitness.ResolveTimeLimitTicks(auto, 10f, 999f)); // 10 s × 2.5 × 60
        Assert.Equal(RacingFitness.AutoTimeLimitTicks(180f / (0.6f * 18f)), RacingFitness.ResolveTimeLimitTicks(auto, null, 180f));
        Assert.Equal(45 * 60, RacingFitness.ResolveTimeLimitTicks(auto with { TimeLimitSeconds = 45 }, 10f, 999f));
    }
}
