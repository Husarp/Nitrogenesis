using Nitrogenesis.Sim.Rng;

public class GaussianTests
{
    [Theory]
    [InlineData(1.0)]
    [InlineData(0.5)]
    [InlineData(2.0)]
    [InlineData(1.4142135)]
    [InlineData(0.70710678)]
    [InlineData(1e-300)]
    [InlineData(5.9604644775390625e-8)] // 2^-24, the smallest uniform Gaussian.Next uses
    [InlineData(0.9999999)]
    [InlineData(123456.789)]
    public void LnIsAccurate(double x)
    {
        // Math.Log is only the yardstick here (tests are not in the tick path).
        Assert.Equal(Math.Log(x), Gaussian.Ln(x), 1e-14 * Math.Max(1, Math.Abs(Math.Log(x))));
    }

    [Fact]
    public void LnRejectsBadInput()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Gaussian.Ln(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Gaussian.Ln(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Gaussian.Ln(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => Gaussian.Ln(double.PositiveInfinity));
    }

    [Fact]
    public void SamplesAreStandardNormal()
    {
        var rng = new Xoshiro128StarStar(7);
        const int n = 200_000;
        double sum = 0, sumSq = 0, sum4 = 0;
        int within1 = 0;
        for (int i = 0; i < n; i++)
        {
            double v = Gaussian.Next(rng);
            Assert.InRange(v, -5.8, 5.8);
            sum += v;
            sumSq += v * v;
            sum4 += v * v * v * v;
            if (Math.Abs(v) < 1) within1++;
        }
        double mean = sum / n, variance = sumSq / n - mean * mean;
        Assert.InRange(mean, -0.01, 0.01);
        Assert.InRange(variance, 0.98, 1.02);
        Assert.InRange(sum4 / n, 2.9, 3.1);            // kurtosis of a normal = 3
        Assert.InRange(within1 / (double)n, 0.678, 0.688); // 68.27 % within one σ
    }

    /// <summary>
    /// Pins the first samples of a fixed stream (bit patterns), so any change to the Gaussian or the RNG, or a
    /// platform difference, shows up. The runner's population hash checks the same on Windows.
    /// </summary>
    [Fact]
    public void SequenceIsPinned()
    {
        var rng = new Xoshiro128StarStar(42);
        ulong h = 0xCBF29CE484222325UL;
        for (int i = 0; i < 1000; i++) h = (h ^ BitConverter.SingleToUInt32Bits(Gaussian.Next(rng))) * 0x100000001B3UL;
        Assert.Equal("0xBE546455D1BB9612", $"0x{h:X16}"); // cross-checked with an independent Python implementation
    }
}
