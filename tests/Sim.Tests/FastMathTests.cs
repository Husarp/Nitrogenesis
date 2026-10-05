using Nitrogenesis.Sim.Brain;

public class FastMathTests
{
    /// <summary>
    /// Bit pattern checksum of the sine and tanh tables. Cross-checked with an independent Python
    /// implementation of the same construction. If this changes, the tick math changed: bump SimVersion.
    /// The packaged app's self-test compares against the same value under the Windows runtime.
    /// </summary>
    public const ulong ExpectedTableChecksum = 0x4CE623DF13DDE839UL;

    [Fact]
    public void TableChecksumMatchesPinnedConstant() => Assert.Equal(ExpectedTableChecksum, FastMath.TableChecksum());

    [Fact]
    public void SinAndCosAreCloseToMath()
    {
        double maxErr = 0;
        for (int i = -200_000; i <= 200_000; i++)
        {
            float x = i * (4 * MathF.PI / 200_000f); // [-4π, 4π]
            maxErr = Math.Max(maxErr, Math.Abs(FastMath.Sin(x) - Math.Sin(x)));
            maxErr = Math.Max(maxErr, Math.Abs(FastMath.Cos(x) - Math.Cos(x)));
        }
        Assert.True(maxErr < 2e-6, $"max error {maxErr}");
    }

    [Fact]
    public void TableIndexValuesAreExactlySymmetric()
    {
        Assert.Equal(0f, FastMath.SinIndex(0));
        Assert.Equal(1f, FastMath.SinIndex(FastMath.SinTableSize / 4));
        Assert.Equal(-1f, FastMath.SinIndex(3 * FastMath.SinTableSize / 4));
        Assert.Equal(1f, FastMath.CosIndex(0));
        for (int i = 0; i < FastMath.SinTableSize; i++)
        {
            Assert.Equal(FastMath.SinIndex(i), -FastMath.SinIndex(-i) + 0f);
            Assert.Equal(FastMath.SinIndex(i), FastMath.SinIndex(i + FastMath.SinTableSize)); // wraps
            Assert.True(Math.Abs(FastMath.SinIndex(i) - Math.Sin(2 * Math.PI * i / FastMath.SinTableSize)) < 1e-7);
        }
    }

    [Fact]
    public void TanhIsCloseToMathAndSaturates()
    {
        double maxErr = 0;
        for (int i = -100_000; i <= 100_000; i++)
        {
            float x = i * 1e-4f; // [-10, 10]
            maxErr = Math.Max(maxErr, Math.Abs(FastMath.Tanh(x) - Math.Tanh(x)));
        }
        Assert.True(maxErr < 2e-6, $"max error {maxErr}");
        Assert.Equal(0f, FastMath.Tanh(0f));
        Assert.Equal(1f, FastMath.Tanh(1e30f));
        Assert.Equal(-1f, FastMath.Tanh(float.NegativeInfinity));
        Assert.Equal(0f, FastMath.Tanh(float.NaN));
        Assert.Equal(-FastMath.Tanh(0.731f), FastMath.Tanh(-0.731f)); // exactly odd
        // Just below the table's upper end (exercises the guard entry).
        Assert.InRange(FastMath.Tanh(BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(FastMath.TanhRange) - 1)), 0.9999990f, 1f);
    }

    [Fact]
    public void Atan2IsCloseToMathInAllQuadrants()
    {
        double maxErr = 0;
        for (int i = 0; i < 3600; i++)
        {
            double a = (i - 1800) * Math.PI / 1800 + 1e-3;
            foreach (double r in new[] { 0.01, 1.0, 250.0 })
            {
                float y = (float)(r * Math.Sin(a)), x = (float)(r * Math.Cos(a));
                maxErr = Math.Max(maxErr, Math.Abs(FastMath.Atan2(y, x) - Math.Atan2(y, x)));
            }
        }
        Assert.True(maxErr < 4e-6, $"max error {maxErr}");
        Assert.Equal(0f, FastMath.Atan2(0f, 0f));
        Assert.Equal(FastMath.HalfPi, FastMath.Atan2(5f, 0f), 6);
        Assert.Equal(FastMath.Pi, FastMath.Atan2(0f, -3f), 6);
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(4f, 4f - 2 * MathF.PI)]
    [InlineData(-4f, -4f + 2 * MathF.PI)]
    [InlineData(7f, 7f - 2 * MathF.PI)]
    [InlineData(-13f, -13f + 4 * MathF.PI)]
    public void WrapPiBringsAnglesIntoRange(float input, float expected) =>
        Assert.Equal(expected, FastMath.WrapPi(input), 5);

    [Fact]
    public void AngleDiffTakesTheShortestArc()
    {
        Assert.Equal(0.2f, FastMath.AngleDiff(3.0f, 3.0f + 0.2f - 2 * MathF.PI), 5);
        Assert.Equal(-0.5f, FastMath.AngleDiff(0.25f, -0.25f), 6);
    }
}

public class FastMathAngleUnitTests
{
    [Fact]
    public void UnitsAgreeWithTheTableAtStepsAndInterpolateBetween()
    {
        for (int i = -2 * FastMath.SinTableSize; i <= 2 * FastMath.SinTableSize; i++)
        {
            Assert.Equal(FastMath.SinIndex(i), FastMath.SinUnits(i * 16));
            Assert.Equal(FastMath.CosIndex(i), FastMath.CosUnits(i * 16));
        }
        double maxErr = 0;
        for (int a = -70_000; a <= 70_000; a += 3)
        {
            double r = a * 2 * Math.PI / 65536;
            maxErr = Math.Max(maxErr, Math.Abs(FastMath.SinUnits(a) - Math.Sin(r)));
            maxErr = Math.Max(maxErr, Math.Abs(FastMath.CosUnits(a) - Math.Cos(r)));
        }
        Assert.True(maxErr < 2e-6, $"max error {maxErr}");
    }

    [Fact]
    public void DegreesConvertToWrappedUnits()
    {
        Assert.Equal(0, FastMath.DegreesToUnits(0));
        Assert.Equal(16384, FastMath.DegreesToUnits(90));
        Assert.Equal(49152, FastMath.DegreesToUnits(-90));
        Assert.Equal(0, FastMath.DegreesToUnits(360));
        Assert.Equal(2731, FastMath.DegreesToUnits(15)); // 2730.67
    }
}
