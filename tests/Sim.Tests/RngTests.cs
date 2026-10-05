using Nitrogenesis.Sim.Rng;

public class RngTests
{
    [Fact]
    public void SplitMix64MatchesReferenceVector()
    {
        // Reference outputs of splitmix64.c (Vigna) for seed 0.
        ulong state = 0;
        Assert.Equal(0xE220A8397B1DCDAFUL, SplitMix64.Next(ref state));
        Assert.Equal(0x6E789E6AA1B965F4UL, SplitMix64.Next(ref state));
        Assert.Equal(0x06C45D188009454FUL, SplitMix64.Next(ref state));
    }

    [Fact]
    public void XoshiroMatchesReferenceVector()
    {
        // Outputs of xoshiro128starstar.c from state {1, 2, 3, 4}.
        var rng = new Xoshiro128StarStar(0);
        rng.SetState(1, 2, 3, 4);
        uint[] expected = [11520, 0, 5927040, 70819200, 2031721883, 1637235492];
        foreach (uint e in expected) Assert.Equal(e, rng.NextUInt());
    }

    [Fact]
    public void SeedingUsesTwoSplitMixOutputsLowWordFirst()
    {
        // Seed 42 → SplitMix64 → state {3961340651, 227163567, 3295419526, 2811696203}; computed independently.
        var rng = new Xoshiro128StarStar(42);
        uint[] expected = [1776835114, 4165204688, 17111135, 2317295270];
        foreach (uint e in expected) Assert.Equal(e, rng.NextUInt());
    }

    [Fact]
    public void SameSeedSameStreamAndReseedRestarts()
    {
        var a = new Xoshiro128StarStar(123);
        var b = new Xoshiro128StarStar(123);
        var first = new uint[100];
        for (int i = 0; i < 100; i++)
        {
            first[i] = a.NextUInt();
            Assert.Equal(first[i], b.NextUInt());
        }
        a.Reseed(123);
        for (int i = 0; i < 100; i++) Assert.Equal(first[i], a.NextUInt());
    }

    [Fact]
    public void NextFloatIsInUnitIntervalWithSaneMean()
    {
        var rng = new Xoshiro128StarStar(7);
        double sum = 0;
        for (int i = 0; i < 100_000; i++)
        {
            float f = rng.NextFloat();
            Assert.InRange(f, 0f, 0.99999994f);
            sum += f;
        }
        Assert.InRange(sum / 100_000, 0.49, 0.51);
    }

    [Fact]
    public void NextIntIsInRangeAndCoversAllValues()
    {
        var rng = new Xoshiro128StarStar(9);
        var counts = new int[7];
        for (int i = 0; i < 70_000; i++) counts[rng.NextInt(7)]++;
        foreach (int c in counts) Assert.InRange(c, 9_000, 11_000);
        Assert.Equal(0, rng.NextInt(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => rng.NextInt(0));
    }

    [Fact]
    public void SeedHashIsStableAndSeparatesStreams()
    {
        // Pinned values (cross-checked with an independent Python implementation): a change here would
        // silently change every child's random stream.
        Assert.Equal(0x9E0160293A33AAF7UL, SeedHash.Derive(1, 0));
        Assert.Equal(0x6FF541D2EBC40217UL, SeedHash.Derive(0xDEADBEEF, 199));
        var seen = new HashSet<ulong>();
        for (ulong seed = 0; seed < 50; seed++)
            for (ulong index = 0; index < 200; index++)
                Assert.True(seen.Add(SeedHash.Derive(seed, index)));
        Assert.NotEqual(SeedHash.Derive(3, 5), SeedHash.Derive(5, 3));
    }
}
