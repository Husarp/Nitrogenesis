using Nitrogenesis.Sim.History;
using Xunit;

namespace Nitrogenesis.Sim.Tests;

public class ScoreGraphTests
{
    [Fact]
    public void Set_StoresBestAndAverageAndCount()
    {
        var g = new ScoreGraph();
        g.Set(0, 1f, 0.5f);
        g.Set(1, 2f, 1f);
        Assert.Equal(2, g.Count);
        Assert.Equal(2f, g.Best(1));
        Assert.Equal(0.5f, g.Average(0));
    }

    [Fact]
    public void Set_GapStaysNaN_AndGrowsPastInitialCapacity()
    {
        var g = new ScoreGraph();
        g.Set(1000, 3f, 2f);
        Assert.Equal(1001, g.Count);
        Assert.True(float.IsNaN(g.Best(500)));
        Assert.Equal(3f, g.Best(1000));
    }

    [Fact]
    public void Access_OutOfRange_Throws()
    {
        var g = new ScoreGraph();
        g.Set(0, 1f, 1f);
        Assert.Throws<ArgumentOutOfRangeException>(() => g.Best(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => g.Set(-1, 0f, 0f));
    }

    [Fact]
    public void VersionGoesUpOnEveryChange()
    {
        var g = new ScoreGraph();
        int v = g.Version;
        g.Set(0, 1f, 1f);
        Assert.True(g.Version > v);
        v = g.Version;
        g.SetDividers([0]);
        Assert.True(g.Version > v);
        v = g.Version;
        g.Clear();
        Assert.True(g.Version > v);
        Assert.Equal(0, g.Count);
        Assert.Equal(0, g.DividerCount);
    }

    [Fact]
    public void Sample_OnePointPerGeneration_WhenFewerThanBuckets()
    {
        var g = new ScoreGraph();
        for (int i = 0; i < 5; i++) g.Set(i, i, i / 2f);
        var best = new float[10];
        var avg = new float[10];
        int n = g.Sample(best, avg, out float low, out float high);
        Assert.Equal(5, n);
        Assert.Equal(3f, best[3]);
        Assert.Equal(1.5f, avg[3]);
        Assert.Equal(0f, low);
        Assert.Equal(4f, high);
    }

    [Fact]
    public void Sample_Buckets_TakeMaxBestAndMeanAverage()
    {
        var g = new ScoreGraph();
        for (int i = 0; i < 100; i++) g.Set(i, i, i);
        var best = new float[10];
        var avg = new float[10];
        int n = g.Sample(best, avg, out float low, out float high);
        Assert.Equal(10, n);
        Assert.Equal(9f, best[0]);
        Assert.Equal(4.5f, avg[0]);
        Assert.Equal(99f, best[9]);
        Assert.Equal(4.5f, low);
        Assert.Equal(99f, high);
    }

    [Fact]
    public void Sample_SkipsUnknownGenerations()
    {
        var g = new ScoreGraph();
        g.Set(0, 1f, 1f);
        g.Set(2, 3f, 3f);
        var best = new float[3];
        var avg = new float[3];
        g.Sample(best, avg, out float low, out float high);
        Assert.True(float.IsNaN(best[1]));
        Assert.True(float.IsNaN(avg[1]));
        Assert.Equal(1f, low);
        Assert.Equal(3f, high);
    }

    [Fact]
    public void Sample_Empty_ReturnsZeroAndNaNRange()
    {
        var g = new ScoreGraph();
        int n = g.Sample(new float[4], new float[4], out float low, out float high);
        Assert.Equal(0, n);
        Assert.True(float.IsNaN(low));
        Assert.True(float.IsNaN(high));
    }

    [Fact]
    public void Sample_ShortAverageBuffer_Throws()
    {
        var g = new ScoreGraph();
        Assert.Throws<ArgumentException>(() => g.Sample(new float[4], new float[2], out _, out _));
    }

    [Fact]
    public void Dividers_AreCopiedAndTruncatedToTarget()
    {
        var g = new ScoreGraph();
        var many = new int[20];
        for (int i = 0; i < many.Length; i++) many[i] = i * 5;
        g.SetDividers(many);
        Assert.Equal(20, g.DividerCount);
        var target = new int[3];
        Assert.Equal(3, g.CopyDividers(target));
        Assert.Equal([0, 5, 10], target);
        var all = new int[32];
        Assert.Equal(20, g.CopyDividers(all));
        Assert.Equal(95, all[19]);
    }

    [Fact]
    public void Clear_ThenSet_StartsFresh()
    {
        var g = new ScoreGraph();
        g.Set(5, 1f, 1f);
        g.Clear();
        g.Set(0, 2f, 2f);
        Assert.Equal(1, g.Count);
        Assert.Equal(2f, g.Best(0));
    }
}
