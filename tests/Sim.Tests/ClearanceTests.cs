using Nitrogenesis.Sim.Map;
using Nitrogenesis.Sim.Racing;
using Nitrogenesis.Sim.Rng;

public class ClearanceTests
{
    [Fact]
    public void MeasuresToTheNearestBlockingSquareIncludingTheMapEdge()
    {
        var c = Clearance.Compute(TestMaps.Parse(
            ".........",
            ".........",
            "....#....",
            ".........",
            ".........").Grid);
        Assert.Equal(0f, c[4, 2]);                  // the wall itself
        Assert.Equal(0.5f, c[0, 0]);                // map edge: outside counts as wall
        Assert.Equal(0.5f, c[3, 2]);                // orthogonal neighbour of the wall
        Assert.Equal(0.5f, c[4, 1]);
        Assert.Equal(MathF.Sqrt(0.5f), c[3, 1], 6); // diagonal neighbour: distance to the wall's corner
        Assert.Equal(1.5f, c[2, 2]);                // two cells left of the wall
    }

    [Fact]
    public void DangerBlocksButGrassAndFinishDoNot()
    {
        var c = Clearance.Compute(TestMaps.Parse(
            ".......",
            ".x.g.F.",
            ".......").Grid);
        Assert.Equal(0f, c[1, 1]);   // danger
        Assert.Equal(1.5f, c[3, 1]); // grass: limited by the danger cell and the map edge, not by itself
        Assert.Equal(1.5f, c[5, 1]); // finish: likewise
        Assert.Equal(0.5f, c[2, 1]); // next to danger
    }

    [Fact]
    public void MatchesBruteForceOnRandomMaps()
    {
        var rng = new Xoshiro128StarStar(2024);
        for (int round = 0; round < 20; round++)
        {
            int w = 5 + rng.NextInt(30), h = 5 + rng.NextInt(30);
            var grid = new Grid(w, h);
            for (int i = 0; i < grid.CellCount; i++)
                grid.Cells[i] = (byte)(rng.NextInt(10) == 0 ? CellType.Wall : rng.NextInt(10) == 0 ? CellType.Danger : CellType.Road);
            var c = Clearance.Compute(grid);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    Assert.Equal(BruteForce(grid, 2 * x + 1, 2 * y + 1), c[x, y]);
                    float best = 0f;
                    for (int j = 2 * y; j <= 2 * y + 2; j++)
                        for (int i = 2 * x; i <= 2 * x + 2; i++)
                            best = MathF.Max(best, BruteForce(grid, i, j));
                    Assert.Equal(best, c.Best[y * w + x]);
                }
        }
    }

    [Fact]
    public void TwoCellCorridorIsPassableAndOneCellIsNot()
    {
        // The car centre fits on a 2-wide corridor's midline (clearance 1.0 ≥ 0.9), although no cell centre does.
        var grid = TestMaps.Parse(
            "##########",
            "..........", // width 2 (rows 1-2)
            "..........",
            "##########",
            "..........", // width 1 (row 4)
            "##########").Grid;
        var c = Clearance.Compute(grid);
        for (int x = 0; x < 10; x++)
        {
            Assert.Equal(0.5f, c[x, 1]);
            Assert.True(c.IsPassable(grid.Index(x, 1), CarSize.HalfWidth));
            Assert.True(c.IsPassable(grid.Index(x, 2), CarSize.HalfWidth));
            Assert.False(c.IsPassable(grid.Index(x, 4), CarSize.HalfWidth));
        }
        Assert.Equal(1f, c.Best[grid.Index(5, 1)]);
        Assert.Equal(0.5f, c.Best[grid.Index(5, 4)]);
    }

    [Fact]
    public void CentreClearanceNeedsThreeCellsForTheCar()
    {
        var c = Clearance.Compute(TestMaps.Parse(
            "##########",
            "..........", // width 2 corridor (rows 1-2)
            "..........",
            "##########",
            "..........", // width 3 corridor (rows 4-6)
            "..........",
            "..........",
            "##########").Grid);
        for (int x = 0; x < 10; x++)
        {
            Assert.False(c[x, 1] >= CarSize.HalfWidth);
            Assert.False(c[x, 2] >= CarSize.HalfWidth);
        }
        Assert.True(c[5, 5] >= CarSize.HalfWidth);
        Assert.Equal(1.5f, c[5, 5]);
    }

    [Fact]
    public void IsPassableRejectsBlockingCellsEvenWithZeroHalfWidth()
    {
        var grid = TestMaps.Parse("..#..").Grid;
        var c = Clearance.Compute(grid);
        Assert.False(c.IsPassable(2, 0f));
        Assert.True(c.IsPassable(1, 0f));
        Assert.False(c.IsPassable(1, CarSize.HalfWidth));
    }

    /// <summary>
    /// Minimum distance from lattice point (i/2, j/2) to any blocking square, the outside counted as blocking.
    /// In half cells, the square of cell b spans [2b, 2b + 2], so the gap along one axis is max(2b − p, p − 2b − 2, 0).
    /// </summary>
    private static float BruteForce(Grid grid, int i, int j)
    {
        int best = int.MaxValue;
        for (int by = -1; by <= grid.Height; by++)
            for (int bx = -1; bx <= grid.Width; bx++)
                if (grid.IsBlocking(bx, by)) // includes the one-cell ring outside the map
                {
                    int ex = Math.Max(Math.Max(2 * bx - i, i - 2 * bx - 2), 0);
                    int ey = Math.Max(Math.Max(2 * by - j, j - 2 * by - 2), 0);
                    best = Math.Min(best, ex * ex + ey * ey);
                }
        return (float)(Math.Sqrt(best) * 0.5);
    }
}
