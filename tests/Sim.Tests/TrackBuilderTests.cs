using Nitrogenesis.Sim.Map;

public class TrackBuilderTests
{
    private static int Count(Grid g, CellType t) => g.Cells.Count(c => c == (byte)t);

    [Fact]
    public void NewGridIsAllWallAndOutsideIsWall()
    {
        var g = new Grid(8, 4);
        Assert.Equal(32, Count(g, CellType.Wall));
        Assert.Equal(CellType.Wall, g[-1, 0]);
        Assert.Equal(CellType.Wall, g[8, 3]);
        Assert.True(g.IsBlocking(0, -1));
    }

    [Fact]
    public void RectIsInclusiveAnyCornerOrderAndClipped()
    {
        var g = new Grid(10, 10);
        new TrackBuilder(g).Rect(5, 6, 2, 3, CellType.Road).Rect(8, 8, 20, 20, CellType.Grass);
        Assert.Equal(16, Count(g, CellType.Road));
        Assert.Equal(CellType.Road, g[2, 3]);
        Assert.Equal(CellType.Road, g[5, 6]);
        Assert.Equal(4, Count(g, CellType.Grass));
    }

    [Fact]
    public void CirclePaintsCellsWhoseCentreIsInside()
    {
        var g = new Grid(20, 20);
        new TrackBuilder(g).Circle(10, 10, 3, CellType.Road);
        // Centres (x+0.5, y+0.5) within 3 of (10,10): count by brute force.
        int expected = 0;
        for (int y = 0; y < 20; y++)
            for (int x = 0; x < 20; x++)
            {
                double dx = x + 0.5 - 10, dy = y + 0.5 - 10;
                bool inside = dx * dx + dy * dy <= 9;
                if (inside) expected++;
                Assert.Equal(inside ? CellType.Road : CellType.Wall, g[x, y]);
            }
        Assert.Equal(expected, Count(g, CellType.Road));
    }

    [Fact]
    public void ThickLineIsACapsule()
    {
        var g = new Grid(30, 11);
        new TrackBuilder(g).Line(5, 5.5, 25, 5.5, 2, CellType.Road);
        Assert.Equal(CellType.Road, g[15, 3]);  // centre y 3.5: distance 2 → inside
        Assert.Equal(CellType.Wall, g[15, 2]);  // distance 3
        Assert.Equal(CellType.Road, g[3, 5]);   // round cap: (3.5, 5.5) is 1.5 from (5, 5.5)
        Assert.Equal(CellType.Wall, g[2, 5]);   // 2.5 from the end point
    }

    [Fact]
    public void OnlyOverRestrictsWhichCellsChange()
    {
        var g = new Grid(20, 5);
        var b = new TrackBuilder(g);
        b.Polyline([(2, 2.5), (18, 2.5)], 1, CellType.Road);
        b.Polyline([(2, 2.5), (18, 2.5)], 2, CellType.Grass, onlyOver: CellType.Wall);
        Assert.Equal(CellType.Road, g[10, 2]);
        Assert.Equal(CellType.Road, g[10, 1]);
        Assert.Equal(CellType.Grass, g[10, 0]);
        b.Fill(CellType.Finish, onlyOver: CellType.Grass);
        Assert.Equal(0, Count(g, CellType.Grass));
        Assert.Equal(CellType.Road, g[10, 2]);
    }

    [Fact]
    public void GridRejectsInvalidCellBytesAndWrongLength()
    {
        Assert.Throws<ArgumentException>(() => new Grid(2, 2, new byte[3]));
        Assert.Throws<ArgumentException>(() => new Grid(2, 2, [0, 1, 2, 99]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Grid(0, 5));
    }
}
