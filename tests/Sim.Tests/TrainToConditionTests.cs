using Nitrogenesis.Sim.History;

public class TrainToConditionTests
{
    private static GenerationStats Stats(int finished, float bestTicks) => new(1f, 0.5f, 0, finished, bestTicks, 100f);

    [Fact]
    public void ToGenerationN()
    {
        var c = TrainToCondition.ToGeneration(10);
        Assert.False(c.IsMetAt(9));
        Assert.True(c.IsMetAt(10));
        Assert.False(c.IsMet(Stats(5, 600), 9, 60));
        Assert.True(c.IsMet(Stats(0, float.PositiveInfinity), 10, 60));
        Assert.Equal("to generation 10", c.Describe());
    }

    [Fact]
    public void UntilACarFinishes()
    {
        var c = TrainToCondition.UntilFinish();
        Assert.False(c.IsMetAt(1_000_000));
        Assert.False(c.IsMet(Stats(0, float.PositiveInfinity), 3, 60));
        Assert.True(c.IsMet(Stats(1, 900), 3, 60));
    }

    [Fact]
    public void UntilBestTimeBelowXSeconds()
    {
        var c = TrainToCondition.UntilBestTimeBelow(15f);
        Assert.False(c.IsMet(Stats(0, float.PositiveInfinity), 3, 60));
        Assert.False(c.IsMet(Stats(3, 900f), 3, 60)); // exactly 15 s is not below
        Assert.True(c.IsMet(Stats(3, 899.5f), 3, 60));
        Assert.Equal("until best time < 15 s", c.Describe());
        Assert.Throws<ArgumentOutOfRangeException>(() => TrainToCondition.UntilBestTimeBelow(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => TrainToCondition.UntilBestTimeBelow(float.NaN));
    }
}
