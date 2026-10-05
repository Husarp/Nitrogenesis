using Nitrogenesis.Sim.Map;

public class TrackHashTests
{
    private static Track Sample() => TestMaps.Parse(
        "#####",
        "#S.F#",
        "#####");

    [Fact]
    public void HashIsPinnedForAKnownTrack()
    {
        // Layout: width, height (int32 LE), cells, start x, y, angle (float32 LE). Must never change.
        Assert.Equal(ExpectedSampleHash, TrackHash.Compute(Sample()));
    }

    [Fact]
    public void HashIgnoresNameAndMetaButSeesCellsAndStart()
    {
        var a = Sample();
        var b = Sample();
        b.Name = "renamed";
        b.Meta = new TrackMeta("Sprint", null, 5);
        Assert.Equal(TrackHash.Compute(a), TrackHash.Compute(b));

        b.Grid[2, 1] = CellType.Grass;
        Assert.NotEqual(TrackHash.Compute(a), TrackHash.Compute(b));

        var c = Sample();
        c.Start = c.Start with { AngleDeg = 15 };
        Assert.NotEqual(TrackHash.Compute(a), TrackHash.Compute(c));
    }

    [Fact]
    public void HashDistinguishesShapesWithTheSameCells()
    {
        var wide = new Grid(4, 2);
        var tall = new Grid(2, 4);
        var start = new TrackStart(0, 0, 0);
        Assert.NotEqual(TrackHash.Compute(wide, start), TrackHash.Compute(tall, start));
    }

    private const string ExpectedSampleHash = "987a5ab84aa22ddea28f1fd5ad81384e11dea83bc29abe4083575bb681084703";
}
