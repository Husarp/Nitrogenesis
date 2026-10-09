using Nitrogenesis.Sim.Core;

public class SnapshotInterpolationTests
{
    private static (AgentSnapshot prev, AgentSnapshot curr) Pair()
    {
        var prev = new AgentSnapshot(3) { Tick = 10, GenerationId = 4 };
        var curr = new AgentSnapshot(3) { Tick = 11, GenerationId = 4 };
        for (int i = 0; i < 3; i++)
        {
            prev.X[i] = i;
            prev.Y[i] = 10 + i;
            curr.X[i] = i + 1;
            curr.Y[i] = 12 + i;
        }
        prev.Angle[0] = 6.2f;   // just below 2π
        curr.Angle[0] = 0.1f;   // just above 0: the short way crosses 0
        prev.Angle[1] = 1f;
        curr.Angle[1] = 2f;
        curr.Status[2] = AgentStatus.Crashed; // alive flag changed
        return (prev, curr);
    }

    private static (float[] x, float[] y, float[] a) Run(AgentSnapshot prev, AgentSnapshot curr, float alpha, bool interpolate)
    {
        float[] x = new float[3], y = new float[3], a = new float[3];
        SnapshotInterpolation.Interpolate(prev, curr, 3, alpha, interpolate, x, y, a);
        return (x, y, a);
    }

    [Fact]
    public void LerpsPositionAndHeadingAlongTheShortestArc()
    {
        var (prev, curr) = Pair();
        var (x, y, a) = Run(prev, curr, 0.5f, true);
        Assert.Equal(0.5f, x[0], 1e-6f);
        Assert.Equal(11f, y[0], 1e-6f);
        Assert.Equal(1.5f, a[1], 1e-6f);
        // 6.2 → 0.1 + 2π: the midpoint is near 0 / 2π, not near π.
        float mid = a[0];
        float dist = MathF.Min(mid, 6.28318531f - mid);
        Assert.True(dist < 0.1f, $"heading {mid} is not on the short arc");
        Assert.InRange(mid, 0f, 6.28318531f);
    }

    [Fact]
    public void AgentsWhoseStatusChangedAreDrawnAtCurr()
    {
        var (prev, curr) = Pair();
        var (x, y, _) = Run(prev, curr, 0.5f, true);
        Assert.Equal(3f, x[2]);
        Assert.Equal(14f, y[2]);
    }

    [Fact]
    public void NoInterpolationAcrossGenerationsOrAboveOneX()
    {
        var (prev, curr) = Pair();
        var (x, _, _) = Run(prev, curr, 0.5f, interpolate: false);
        Assert.Equal(1f, x[0]);
        curr.GenerationId = 5;
        (x, _, _) = Run(prev, curr, 0.5f, interpolate: true);
        Assert.Equal(1f, x[0]);
    }

    [Fact]
    public void APairMoreThanOneTickApartIsDrawnAtCurr()
    {
        // Left over from a faster speed (paused at 100×, then slowed to 0.25×): 19 ticks apart.
        var (prev, curr) = Pair();
        prev.Tick = curr.Tick - 19;
        var (x, _, _) = Run(prev, curr, 0.5f, true);
        Assert.Equal(1f, x[0]);
    }

    [Theory]
    [InlineData(-1f, 0f)]
    [InlineData(0f, 0f)]
    [InlineData(2f, 1f)]
    [InlineData(float.NaN, 0f)]
    public void AlphaIsClampedSoItNeverExtrapolates(float alpha, float expectedX)
    {
        var (prev, curr) = Pair();
        var (x, _, _) = Run(prev, curr, alpha, true);
        Assert.Equal(expectedX, x[0], 1e-6f);
    }

    [Fact]
    public void DoesNotAllocate()
    {
        var (prev, curr) = Pair();
        float[] x = new float[3], y = new float[3], a = new float[3];
        Allocations.AssertSteadyStateFree(() =>
        {
            for (int i = 0; i < 100; i++) SnapshotInterpolation.Interpolate(prev, curr, 3, i / 100f, true, x, y, a);
        });
    }
}
