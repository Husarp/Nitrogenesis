using Nitrogenesis.Sim.Map;

public class SolvabilityTests
{
    private const double GrassCost = 1 / 0.45;

    // Corridors are 5 cells wide so the car (half-width 0.9) fits comfortably.
    private static readonly string[] Open =
    [
        "##############",
        "#............#",
        "#............#",
        "#.S.......FFF#",
        "#............#",
        "#............#",
        "##############",
    ];

    [Fact]
    public void OpenRoomIsSolvable() => Assert.Equal(SolvabilityResult.Solvable, Solvability.Check(TestMaps.Parse(Open), GrassCost));

    [Fact]
    public void WalledOffFinishIsUnreachable()
    {
        var t = TestMaps.Parse(Open);
        for (int y = 0; y < 7; y++) t.Grid[7, y] = CellType.Wall;
        Assert.Equal(SolvabilityResult.FinishUnreachable, Solvability.Check(t, GrassCost));
    }

    [Fact]
    public void MissingFinishIsReported()
    {
        var t = TestMaps.Parse(Open);
        new TrackBuilder(t.Grid).Fill(CellType.Road, onlyOver: CellType.Finish);
        Assert.Equal(SolvabilityResult.NoFinish, Solvability.Check(t, GrassCost));
    }

    [Fact]
    public void StartInAWallOrOutsideIsReported()
    {
        var t = TestMaps.Parse(Open);
        t.Start = new TrackStart(0.5f, 0.5f, 0);
        Assert.Equal(SolvabilityResult.StartNotPassable, Solvability.Check(t, GrassCost));
        t.Start = new TrackStart(-3f, 2f, 0);
        Assert.Equal(SolvabilityResult.StartOutside, Solvability.Check(t, GrassCost));
    }

    [Fact]
    public void StartWhoseCarOutlineTouchesAWallIsReported()
    {
        // A passable cell (the car centre fits somewhere in it), but at this exact pose the rear is in the wall:
        // such a car can never move.
        var t = TestMaps.Parse(Open);
        t.Start = new TrackStart(1.5f, 3.5f, 0);
        Assert.Equal(SolvabilityResult.StartOverlapsWall, Solvability.Check(t, GrassCost));
        Assert.False(new Nitrogenesis.Sim.Racing.RacingTrack(t, new()).IsFinishable);
        t.Start = new TrackStart(1.5f, 3.5f, 90); // turned along the wall it still overlaps (the side reaches x = 0.6)
        Assert.Equal(SolvabilityResult.StartOverlapsWall, Solvability.Check(t, GrassCost));
        t.Start = new TrackStart(2.5f, 3.5f, 0);  // rear exactly on the wall's face: free
        Assert.Equal(SolvabilityResult.Solvable, Solvability.Check(t, GrassCost));
        Assert.True(new Nitrogenesis.Sim.Racing.RacingTrack(t, new()).IsFinishable);
    }
}
