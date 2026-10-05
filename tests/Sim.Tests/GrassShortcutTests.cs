using Nitrogenesis.Sim.Map;

public class GrassShortcutTests
{
    private const double GrassCost = 1 / 0.45;

    /// <summary>
    /// A U-shaped road around a block. The block is either wall (no shortcut) or grass (a short cut across).
    /// </summary>
    private static Track UTrack(CellType block)
    {
        var grid = new Grid(60, 30);
        var b = new TrackBuilder(grid);
        b.Polyline([(6, 6), (50, 6), (50, 24), (6, 24)], 3, CellType.Road);
        b.Rect(2, 9, 47, 20, block, onlyOver: CellType.Wall);
        b.Rect(3, 21, 7, 27, CellType.Finish, onlyOver: CellType.Road);
        return new Track("u", grid, new TrackStart(8, 6, 0));
    }

    [Fact]
    public void WalledBlockIsNoShortcut()
    {
        var r = GrassShortcut.Check(UTrack(CellType.Wall), GrassCost);
        Assert.False(r.IsShortcut);
        Assert.Equal(r.RoadOnly, r.WithGrass);
    }

    [Fact]
    public void GrassBlockIsAShortcut()
    {
        var r = GrassShortcut.Check(UTrack(CellType.Grass), GrassCost);
        Assert.True(r.IsShortcut, $"road {r.RoadOnly}, grass {r.WithGrass}");
    }

    [Fact]
    public void FinishOnlyReachableOverGrassCountsAsShortcut()
    {
        var t = TestMaps.Parse(
            "#############",
            "#...........#",
            "#...........#",
            "#S..ggggg.FF#",
            "#...........#",
            "#...........#",
            "#############");
        for (int y = 1; y < 6; y++) t.Grid[6, y] = CellType.Grass; // a grass band across the whole room
        var r = GrassShortcut.Check(t, GrassCost);
        Assert.Equal(float.PositiveInfinity, r.RoadOnly);
        Assert.True(r.IsShortcut);
    }
}
