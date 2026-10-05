namespace Nitrogenesis.Sim.Brain;

/// <summary>
/// Deterministic replacements for the transcendental functions the tick path needs (PLAN §2.1).
/// </summary>
/// <remarks>
/// <para>Why: <c>Math.Sin</c>, <c>Math.Tanh</c> etc. call the platform libm, which differs between Linux and
/// Windows, so they would break bit-exact replays. Everything here uses only IEEE +, −, ×, ÷ and
/// <c>Floor</c>, which are exactly specified, in a fixed order. RyuJIT never fuses a×b+c into an FMA on
/// its own, and float math runs in SSE/NEON single precision, so results are identical everywhere.</para>
/// <para>The tables are built at startup by our own double-precision Taylor series (never by libm) and
/// rounded to float. <see cref="TableChecksum"/> lets a test and the packaged app's self-test confirm
/// the tables match bit for bit on each runtime.</para>
/// </remarks>
public static class FastMath
{
    public const float Pi = 3.14159265f;
    public const float TwoPi = 6.28318531f;
    public const float HalfPi = 1.57079633f;
    public const float DegToRad = Pi / 180f;
    public const float RadToDeg = 180f / Pi;

    /// <summary>Entries in the sine table for one full turn (a power of two, so wrapping is a mask).</summary>
    public const int SinTableSize = 4096;
    private const int SinMask = SinTableSize - 1;
    private const float SinIndexPerRadian = SinTableSize / TwoPi;

    /// <summary>tanh is tabulated on [−TanhRange, TanhRange] and saturates to ±1 outside.</summary>
    public const float TanhRange = 8f;
    private const int TanhStepsPerUnit = 256;
    private const int TanhSteps = (int)(2 * TanhRange) * TanhStepsPerUnit; // 4096 intervals

    private static readonly float[] SinTable = BuildSinTable();
    private static readonly float[] TanhTable = BuildTanhTable();

    /// <summary>sin of an angle in radians: table lookup with linear interpolation (abs error &lt; 1e-6 for |x| ≤ 4π).</summary>
    public static float Sin(float radians) => SampleSin(radians * SinIndexPerRadian);

    /// <summary>cos of an angle in radians (a quarter turn ahead on the same table).</summary>
    public static float Cos(float radians) => SampleSin(radians * SinIndexPerRadian + SinTableSize / 4);

    /// <summary>Exact table value of sin(2π·index / <see cref="SinTableSize"/>); any int wraps around.</summary>
    public static float SinIndex(int index) => SinTable[index & SinMask];

    /// <summary>Exact table value of cos(2π·index / <see cref="SinTableSize"/>); any int wraps around.</summary>
    public static float CosIndex(int index) => SinTable[(index + SinTableSize / 4) & SinMask];

    // ---- fixed-point angles (car headings) ----

    /// <summary>
    /// Fixed-point angle units in one full turn. Car headings are kept as an <c>int</c> in these units
    /// (PLAN §2.1: "heading kept as an angle index"): turning is exact integer addition, wrapping is a mask,
    /// and nothing drifts. One unit is about 0.0055°.
    /// </summary>
    public const int AngleUnitsPerTurn = 65536;
    /// <summary>Mask that wraps an angle in units into [0, <see cref="AngleUnitsPerTurn"/>).</summary>
    public const int AngleUnitsMask = AngleUnitsPerTurn - 1;
    private const int UnitsPerSinStep = AngleUnitsPerTurn / SinTableSize; // 16
    private const float InverseUnitsPerSinStep = 1f / UnitsPerSinStep;     // exact (power of two)
    /// <summary>Radians per angle unit (for display and for comparing with <see cref="Atan2"/> results).</summary>
    public const float RadiansPerUnit = TwoPi / AngleUnitsPerTurn;

    /// <summary>sin of an angle in units: table lookup with linear interpolation between 16-unit steps.</summary>
    public static float SinUnits(int angle)
    {
        int i = angle >> 4; // arithmetic shift = floor division by 16, also for negative angles
        float f = (angle & (UnitsPerSinStep - 1)) * InverseUnitsPerSinStep;
        float a = SinTable[i & SinMask];
        float b = SinTable[(i + 1) & SinMask];
        return a + (b - a) * f;
    }

    /// <summary>cos of an angle in units (a quarter turn ahead).</summary>
    public static float CosUnits(int angle) => SinUnits(angle + AngleUnitsPerTurn / 4);

    /// <summary>Nearest angle in units (wrapped to [0, 65536)) for an angle in degrees, e.g. the track's start angle.</summary>
    public static int DegreesToUnits(float degrees) =>
        (int)MathF.Round(degrees * (AngleUnitsPerTurn / 360f)) & AngleUnitsMask;

    private static float SampleSin(float t)
    {
        float floor = MathF.Floor(t);
        float f = t - floor;
        int i = (int)floor; // .NET float→int conversion saturates (and maps NaN to 0) on every platform
        float a = SinTable[i & SinMask];
        float b = SinTable[(i + 1) & SinMask];
        return a + (b - a) * f;
    }

    /// <summary>Hyperbolic tangent: table with linear interpolation (abs error &lt; 2e-6). NaN maps to 0.</summary>
    public static float Tanh(float x)
    {
        if (x > -TanhRange && x < TanhRange)
        {
            float t = (x + TanhRange) * TanhStepsPerUnit;
            int i = (int)t; // t ≥ 0, so truncation is floor
            float f = t - i;
            float a = TanhTable[i];
            return a + (TanhTable[i + 1] - a) * f;
        }
        return x >= TanhRange ? 1f : x <= -TanhRange ? -1f : 0f;
    }

    /// <summary>
    /// atan2(y, x) in radians, in [−π, π]. Odd minimax polynomial on [0, 1] (abs error &lt; 2e-6) with
    /// octant reduction. atan2(0, 0) = 0.
    /// </summary>
    public static float Atan2(float y, float x)
    {
        float ax = MathF.Abs(x), ay = MathF.Abs(y);
        float max = ax > ay ? ax : ay;
        if (max == 0f) return 0f;
        float min = ax > ay ? ay : ax;
        float z = min / max;
        float s = z * z;
        float r = z * (0.99997726f + s * (-0.33262347f + s * (0.19354346f + s * (-0.11643287f
                  + s * (0.05265332f + s * -0.01172120f)))));
        if (ay > ax) r = HalfPi - r;
        if (x < 0f) r = Pi - r;
        return y < 0f ? -r : r;
    }

    /// <summary>Wraps an angle into [−π, π] (an input of exactly −π may come back as π after rounding).</summary>
    public static float WrapPi(float radians) => radians - TwoPi * MathF.Floor((radians + Pi) * (1f / TwoPi));

    /// <summary>Signed shortest turn from <paramref name="from"/> to <paramref name="to"/>, in [−π, π].</summary>
    public static float AngleDiff(float from, float to) => WrapPi(to - from);

    /// <summary>
    /// 64-bit FNV-1a over the bit patterns of both tables (sine first, then tanh, one 32-bit word at a time).
    /// Equal on every runtime iff the tables are bit-identical.
    /// </summary>
    public static ulong TableChecksum()
    {
        ulong h = 0xCBF29CE484222325UL;
        foreach (float v in SinTable) h = (h ^ BitConverter.SingleToUInt32Bits(v)) * 0x100000001B3UL;
        foreach (float v in TanhTable) h = (h ^ BitConverter.SingleToUInt32Bits(v)) * 0x100000001B3UL;
        return h;
    }

    // ---- table construction (startup only; double precision, fixed operation order) ----

    private static float[] BuildSinTable()
    {
        var table = new float[SinTableSize];
        const int quarter = SinTableSize / 4, half = SinTableSize / 2;
        for (int k = 0; k <= quarter; k++)
        {
            // First quadrant from the series; the rest by exact symmetry, so the table is exactly odd
            // and half-wave symmetric. "0f - v" keeps sin(π) and sin(2π) at +0 rather than −0.
            float v = (float)SinSeries(k * (Math.PI / half));
            table[k] = v;
            table[half - k] = v;
            table[half + k] = 0f - v;
            table[(SinTableSize - k) & SinMask] = 0f - v;
        }
        return table;
    }

    /// <summary>Taylor series of sin for x in [0, π/2]; 13 terms leave a truncation error below 1e-20.</summary>
    private static double SinSeries(double x)
    {
        double x2 = x * x, term = x, sum = 0;
        for (int n = 0; n < 13; n++)
        {
            sum += term;
            term *= -x2 / ((2 * n + 2) * (2 * n + 3));
        }
        return sum;
    }

    private static float[] BuildTanhTable()
    {
        // One guard entry past the end: when x is just below TanhRange, (x + TanhRange) can round up to
        // exactly 2·TanhRange, giving i = TanhSteps and reading entry i + 1.
        var table = new float[TanhSteps + 2];
        const int mid = TanhSteps / 2;
        for (int k = 0; k <= mid; k++)
        {
            double x = (double)k / TanhStepsPerUnit;
            double e = ExpNegative(2 * x);
            float v = (float)((1 - e) / (1 + e));
            table[mid + k] = v;
            table[mid - k] = 0f - v; // exactly odd
        }
        table[TanhSteps + 1] = table[TanhSteps];
        return table;
    }

    /// <summary>
    /// e^(−y) for y in [0, 16]: e^(−y/256) by a 12-term Taylor series, then squared 8 times.
    /// Relative error stays near 1e-13, far below float resolution.
    /// </summary>
    private static double ExpNegative(double y)
    {
        double r = -y / 256; // exact: power-of-two scaling
        double term = 1, sum = 0;
        for (int n = 1; n <= 12; n++)
        {
            sum += term;
            term *= r / n;
        }
        for (int i = 0; i < 8; i++) sum *= sum;
        return sum;
    }
}
