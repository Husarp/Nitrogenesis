using Nitrogenesis.Sim.Map;
using Nitrogenesis.Sim.Racing;

public class DistanceFieldTests
{
    private const double Sqrt2 = 1.4142135623730951;

    /// <summary>Field with a zero half-width, so every non-blocking cell is passable (pure grid behaviour).</summary>
    private static DistanceField Field(Track t, double grassCost = 2.0, float halfWidth = 0f) =>
        DistanceField.ForRacing(t.Grid, Clearance.Compute(t.Grid), halfWidth, grassCost);

    [Fact]
    public void StraightCorridorCountsCells()
    {
        var f = Field(TestMaps.Parse("F......"));
        for (int x = 0; x < 7; x++) Assert.Equal(x, f[x, 0]);
    }

    [Fact]
    public void OpenFieldUsesOctileDistance()
    {
        var t = TestMaps.Parse(
            "F.........",
            "..........",
            "..........",
            "..........",
            "..........",
            "..........");
        var f = Field(t);
        Assert.Equal((float)(5 * Sqrt2), f[5, 5], 5);
        Assert.Equal((float)(2 * Sqrt2 + 3), f[5, 2], 5); // 2 diagonal + 3 straight
        Assert.Equal((float)(5 * Sqrt2 + 4), f[9, 5], 5);
    }

    [Fact]
    public void NoDiagonalStepBetweenTwoBlockingCells()
    {
        // (1,1) and (0,0) touch (0,1)/(1,0) walls only at corners: no squeezing through.
        var t = TestMaps.Parse(
            "F#...",
            "#....",
            ".....");
        var f = Field(t);
        Assert.False(f.IsReachable(1, 1));
        Assert.False(f.IsReachable(4, 2));

        // With one of the two side cells open, the diagonal is allowed.
        var open = TestMaps.Parse(
            "F#...",
            ".....",
            ".....");
        var g = Field(open);
        Assert.Equal((float)Sqrt2, g[1, 1]);
    }

    [Fact]
    public void DiagonalPastASingleCornerIsAllowed()
    {
        var f = Field(TestMaps.Parse(
            "F.",
            "#."));
        Assert.Equal((float)Sqrt2, f[1, 1]);
    }

    [Fact]
    public void GrassCostsTheMeanFactorOfBothCells()
    {
        var f = Field(TestMaps.Parse("F.gg."), grassCost: 3.0);
        Assert.Equal(0f, f[0, 0]);
        Assert.Equal(1f, f[1, 0]);
        Assert.Equal(1f + 2f, f[2, 0]);      // road→grass: (1 + 3) / 2
        Assert.Equal(1f + 2f + 3f, f[3, 0]); // grass→grass: 3
        Assert.Equal(1f + 2f + 3f + 2f, f[4, 0]);
    }

    [Fact]
    public void RouteAvoidsGrassWhenTheDetourIsCheaper()
    {
        // Straight up through the grass is 2 cells; the road detour (right, past the wall's corners, back) is 8 + 2√2.
        var t = TestMaps.Parse(
            "F.....",
            "gggg#.",
            "S....."); // start at (0,2)
        var cheap = Field(t, grassCost: 1.0);
        var dear = Field(t, grassCost: 10.0);
        Assert.Equal(2f, cheap[0, 2]);                 // straight up through grass
        Assert.Equal((float)(2 * Sqrt2 + 8), dear[0, 2], 4); // 10.83 on road beats 11 through grass
    }

    [Fact]
    public void InfiniteGrassCostMeansRoadOnly()
    {
        var f = Field(TestMaps.Parse("F.g."), grassCost: double.PositiveInfinity);
        Assert.True(f.IsReachable(1, 0));
        Assert.False(f.IsReachable(2, 0));
        Assert.False(f.IsReachable(3, 0));
    }

    [Fact]
    public void WallsAndDangerAreUnreachable()
    {
        var f = Field(TestMaps.Parse("F.#x."));
        Assert.False(f.IsReachable(2, 0));
        Assert.False(f.IsReachable(3, 0));
        Assert.False(f.IsReachable(4, 0));
        Assert.Equal(DistanceField.Unreachable, f[4, 0]);
        Assert.False(f.IsReachable(-1, 0));
    }

    [Fact]
    public void TooNarrowGapBlocksTheCarButAWideEnoughOnePasses()
    {
        // Two rooms joined by a gap in a 1-cell wall: 1 cell wide (blocked), then 2 cells wide (passes: the car
        // centre fits on the gap's midline, clearance 1.0 ≥ 0.9, and the 1.8-wide car really drives through).
        string[] Rooms(int gap)
        {
            var rows = new List<string>();
            for (int y = 0; y < 9; y++)
            {
                bool inGap = y >= 3 && y < 3 + gap;
                rows.Add("FFFFFFF" + (inGap ? "." : "#") + ".......");
            }
            return rows.ToArray();
        }
        var narrow = Field(TestMaps.Parse(Rooms(1)), halfWidth: CarSize.HalfWidth);
        var wide = Field(TestMaps.Parse(Rooms(2)), halfWidth: CarSize.HalfWidth);
        Assert.True(narrow.IsReachable(3, 4));
        Assert.False(narrow.IsReachable(11, 4));
        Assert.True(wide.IsReachable(11, 4));
    }

    [Fact]
    public void ACorridorTheCarCanDriveIsInTheField()
    {
        // Two rooms joined by a 2-cell-wide, 10-cell-long corridor: the field must see what the physics can drive.
        var rows = new List<string>();
        for (int y = 0; y < 20; y++)
        {
            string side = y is 9 or 10 ? ".........." : "##########";
            rows.Add(".........." + side + "....FFFFFF");
        }
        var t = TestMaps.Parse(rows.ToArray());
        t.Start = new TrackStart(4f, 10f, 0);
        var track = new RacingTrack(t, new RacingSettings());
        Assert.True(track.IsFinishable);
        Assert.True(track.Field.IsReachable(15, 9) && track.Field.IsReachable(15, 10));

        var physics = new CarPhysics(t.Grid, track.Clearance, track.Settings);
        var car = new CarState(t.Start.X, t.Start.Y, 0);
        TickResult r = default;
        for (int i = 0; i < 300 && r.Outcome == TickOutcome.Moving; i++) r = physics.Tick(ref car, 1f, 0f);
        Assert.Equal(TickOutcome.Finished, r.Outcome);
    }

    [Fact]
    public void SampleInterpolatesBilinearlyBetweenCellCentres()
    {
        var f = Field(TestMaps.Parse("F......"));
        Assert.Equal(3f, f.Sample(3.5f, 0.5f));
        Assert.Equal(3.25f, f.Sample(3.75f, 0.5f));
        Assert.Equal(0f, f.Sample(0.2f, 0.5f)); // off the outer half of the first cell: the outside corner is ignored
    }

    [Fact]
    public void SampleIgnoresUnreachableCornersAndReportsWhenAllAre()
    {
        var f = Field(TestMaps.Parse(
            "F...",
            "####"));
        Assert.Equal(2f, f.Sample(2.5f, 0.9f));  // lower corners are walls: only the road row counts
        Assert.Equal(2.5f, f.Sample(3.0f, 1.0f)); // between (2,0) and (3,0), walls below
        Assert.Equal(DistanceField.Unreachable, f.Sample(2.5f, 1.6f)); // centre of a wall row, outside below
    }

    [Fact]
    public void SameMapGivesBitIdenticalFields()
    {
        var t = TestMaps.Parse(
            "F...g....",
            "..#.g.##.",
            "..#......",
            "....x...S");
        Assert.Equal(Field(t).Values, Field(t).Values);
    }
}
