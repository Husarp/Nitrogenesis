using Nitrogenesis.Sim.Brain;

namespace Nitrogenesis.Sim.Rng;

/// <summary>
/// Standard normal samples for weight initialisation and mutation (PLAN §3.5, §3.7), bit-identical on every
/// platform: Box–Muller with our own natural log (double precision, fixed operation order, never libm),
/// IEEE square root (correctly rounded, so deterministic) and the <see cref="FastMath"/> sine table.
/// </summary>
public static class Gaussian
{
    private const double Ln2 = 0.6931471805599453;     // nearest double to ln 2
    private const double Sqrt2 = 1.4142135623730951;   // nearest double to √2

    /// <summary>
    /// One N(0, 1) sample from two 32-bit draws: radius √(−2 ln u) with u in (0, 1] (24 bits), angle from
    /// 16 bits in <see cref="FastMath"/> angle units. |result| ≤ 5.8.
    /// </summary>
    public static float Next(Xoshiro128StarStar rng)
    {
        double u = ((rng.NextUInt() >> 8) + 1) * (1.0 / 16777216.0); // (0, 1], exact
        double radius = Math.Sqrt(-2.0 * Ln(u));
        int angle = (int)(rng.NextUInt() >> 16);
        return (float)(radius * FastMath.CosUnits(angle));
    }

    /// <summary>
    /// Natural log of a positive, finite, normal double. x = m·2^e with m in [√½, √2), then
    /// ln m = 2·atanh(s), s = (m − 1)/(m + 1), |s| ≤ 0.172, by its odd series to s^29 (truncation &lt; 1e-22).
    /// Abs error near 1e-16: far below what the float result can resolve.
    /// </summary>
    internal static double Ln(double x)
    {
        if (!(x > 0) || !double.IsNormal(x)) throw new ArgumentOutOfRangeException(nameof(x));
        long bits = BitConverter.DoubleToInt64Bits(x);
        int e = (int)((bits >> 52) & 0x7FF) - 1023;
        double m = BitConverter.Int64BitsToDouble((bits & 0x000FFFFFFFFFFFFFL) | 0x3FF0000000000000L); // [1, 2)
        if (m >= Sqrt2)
        {
            m *= 0.5; // exact
            e++;
        }
        double s = (m - 1) / (m + 1);
        double s2 = s * s, term = s, sum = 0;
        for (int k = 1; k <= 29; k += 2)
        {
            sum += term / k;
            term *= s2;
        }
        return 2 * sum + e * Ln2;
    }
}
