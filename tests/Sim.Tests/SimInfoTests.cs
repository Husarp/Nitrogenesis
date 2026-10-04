using Nitrogenesis.Sim;
using Xunit;

public class SimInfoTests
{
    [Fact]
    public void SimVersionIsPositive() => Assert.True(SimInfo.SimVersion > 0);
}
