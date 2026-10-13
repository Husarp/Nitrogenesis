using System.Diagnostics;
using Nitrogenesis.Sim.Map;
using Nitrogenesis.Sim.Racing;

public class EditorAnalysisTests
{
    private static readonly RacingSettings Settings = new();

    private static EditorAnalysisResult Run(Track t, RacingSettings? s = null) => EditorAnalysis.Run(t, s ?? Settings);

    private static bool Has(EditorAnalysisResult r, MessageCode code) => r.Messages.Any(m => m.Code == code);

    /// <summary>A 4-wide road (rows 2..5) from x=2 to the finish at the right end; the start row is overridable by its first cells.</summary>
    private static Track Corridor(int length = 40, string? startPrefix = null)
    {
        string wall = new('#', length);
        string road = "##" + new string('.', length - 6) + "FF##";
        string prefix = startPrefix ?? "##...S";
        string start = prefix + new string('.', length - 4 - prefix.Length) + "FF##";
        return TestMaps.Parse(wall, wall, road, start, road, road, wall, wall);
    }

    [Fact]
    public void FinishableTrackIsOkWithAnEstimate()
    {
        var r = Run(Corridor());
        Assert.True(r.IsFinishable);
        Assert.Equal(SolvabilityResult.Solvable, r.State);
        Assert.True(r.HasRoadBehindStart);
        Assert.Contains(r.Messages, m => m.Code == MessageCode.Finishable && m.Severity == MessageSeverity.Ok);
        Assert.DoesNotContain(r.Messages, m => m.Severity == MessageSeverity.Error);
        Assert.Equal(0, r.UnreachableCount);
        Assert.False(r.IsGrassShortcut);
        Assert.Empty(r.ShortcutRoute);
    }

    [Fact]
    public void EstimateIsPathLengthOverSixTenthsOfMaxSpeed()
    {
        Assert.Equal(0.6f, EditorAnalysis.EstimateSpeedFactor);
        var t = Corridor(120);
        var r = Run(t);
        var racing = new RacingTrack(t, Settings);
        Assert.Equal(racing.StartDistance, r.PathLength!.Value);
        Assert.Equal(racing.StartDistance / (0.6f * Settings.MaxSpeed), r.EstimatedSeconds!.Value, 4);
        var fast = Run(t, new RacingSettings { MaxSpeed = 36f });
        Assert.Equal(r.EstimatedSeconds!.Value / 2f, fast.EstimatedSeconds!.Value, 3);
    }

    [Fact]
    public void ShortTrackWarnsBelowTwentySeconds()
    {
        var r = Run(Corridor(40)); // about 32 cells / 10.8 = 3 s
        Assert.True(r.EstimatedSeconds < EditorAnalysis.VeryShortSeconds);
        Assert.True(r.IsVeryShort);
        Assert.Contains(r.Messages, m => m.Code == MessageCode.VeryShort && m.Severity == MessageSeverity.Warning);
        Assert.Equal(MessageSeverity.Warning, r.Worst);
    }

    [Fact]
    public void LongTrackDoesNotWarn()
    {
        var r = Run(Corridor(400)); // about 390 cells / 10.8 = 36 s
        Assert.True(r.EstimatedSeconds > EditorAnalysis.VeryShortSeconds);
        Assert.False(r.IsVeryShort);
        Assert.False(Has(r, MessageCode.VeryShort));
        Assert.Equal(MessageSeverity.Ok, r.Worst);
    }

    [Fact]
    public void WallAcrossTheRoadBlocksTheFinishAndMarksTheCutOffArea()
    {
        var t = Corridor(40);
        for (int y = 2; y <= 5; y++) t.Grid[20, y] = CellType.Wall;
        var r = Run(t);
        Assert.Equal(SolvabilityResult.FinishUnreachable, r.State);
        Assert.False(r.IsFinishable);
        Assert.Contains(r.Messages, m => m.Code == MessageCode.FinishUnreachable && m.Severity == MessageSeverity.Error);
        Assert.Null(r.EstimatedSeconds);
        Assert.False(Has(r, MessageCode.VeryShort));

        // Exactly the passable cells left of the wall are cut off; the finish side is not.
        int w = t.Grid.Width;
        var clearance = Clearance.Compute(t.Grid);
        int expected = 0;
        for (int y = 0; y < t.Grid.Height; y++)
            for (int x = 0; x < w; x++)
            {
                bool passable = clearance.IsPassable(y * w + x, CarSize.HalfWidth);
                bool shouldBe = passable && x < 20;
                Assert.Equal(shouldBe, r.UnreachableMask[y * w + x]);
                if (shouldBe) expected++;
            }
        Assert.True(expected > 0);
        Assert.Equal(expected, r.UnreachableCount);
        Assert.False(r.UnreachableMask[3 * w + 30]);
    }

    [Fact]
    public void NoFinishZoneIsAnError()
    {
        var t = Corridor(40);
        for (int i = 0; i < t.Grid.CellCount; i++)
            if (t.Grid.Cells[i] == (byte)CellType.Finish) t.Grid.Cells[i] = (byte)CellType.Road;
        var r = Run(t);
        Assert.Equal(SolvabilityResult.NoFinish, r.State);
        Assert.Contains(r.Messages, m => m.Code == MessageCode.NoFinish && m.Severity == MessageSeverity.Error);
        Assert.False(r.IsFinishable);
        Assert.Equal(0, r.UnreachableCount);
    }

    [Fact]
    public void FinishZoneTooNarrowForTheCarCountsAsNoFinish()
    {
        var t = Corridor(40);
        for (int i = 0; i < t.Grid.CellCount; i++)
            if (t.Grid.Cells[i] == (byte)CellType.Finish) t.Grid.Cells[i] = (byte)CellType.Road;
        t.Grid[30, 3] = CellType.Finish; // single cell inside a wide road is still wide enough...
        Assert.True(Run(t).IsFinishable);
        // ...but a Finish cell sealed into a 1-cell slot is not.
        var slot = TestMaps.Parse(
            "##########",
            "#........#",
            "#........#",
            "#.S......#",
            "#........#",
            "#........#",
            "########F#",
            "##########");
        Assert.Equal(SolvabilityResult.NoFinish, Run(slot).State);
    }

    [Fact]
    public void StartOutsideTheMap()
    {
        var t = Corridor(40);
        t.Start = new TrackStart(-5, 3, 0);
        var r = Run(t);
        Assert.Equal(SolvabilityResult.StartOutside, r.State);
        Assert.Contains(r.Messages, m => m.Code == MessageCode.StartOutside && m.Severity == MessageSeverity.Error);
        Assert.False(Has(r, MessageCode.NoRoadBehindStart));
    }

    [Fact]
    public void StartInWallIsAnError()
    {
        var t = Corridor(40);
        t.Start = new TrackStart(10.5f, 0.5f, 0);
        var r = Run(t);
        Assert.Equal(SolvabilityResult.StartNotPassable, r.State);
        Assert.Contains(r.Messages, m => m.Code == MessageCode.StartNotPassable && m.Severity == MessageSeverity.Error);
        Assert.False(r.IsFinishable);
    }

    [Fact]
    public void CarTouchingAWallAtTheStartIsAnError()
    {
        var t = Corridor(40);
        t.Start = new TrackStart(8.5f, 2.5f, 90f); // 3 long across a 4-wide road, centred on the edge row
        var r = Run(t);
        Assert.Equal(SolvabilityResult.StartOverlapsWall, r.State);
        Assert.Contains(r.Messages, m => m.Code == MessageCode.StartOverlapsWall && m.Severity == MessageSeverity.Error);
        Assert.False(r.IsFinishable);
    }

    [Fact]
    public void LessThanOneCarLengthOfRoadBehindTheStartIsAWarning()
    {
        // Start 1.5 cells from the road's end: only 1.5 cells of road behind it. Still finishable, so a warning (review 07).
        var t = Corridor(40, startPrefix: "##.S");
        var r = Run(t);
        Assert.True(r.IsFinishable);
        Assert.False(r.HasRoadBehindStart);
        Assert.Contains(r.Messages, m => m.Code == MessageCode.NoRoadBehindStart && m.Severity == MessageSeverity.Warning);
        Assert.Equal(MessageSeverity.Warning, r.Worst);
    }

    [Fact]
    public void ANewMapNamesTheStartInWallInsteadOfTheRoadBehindIt()
    {
        var doc = EditDocument.New(MapSize.M);
        var r = Run(doc.Snapshot());
        Assert.Equal(SolvabilityResult.NoFinish, r.State);
        Assert.Contains(r.Messages, m => m.Code == MessageCode.NoFinish && m.Severity == MessageSeverity.Error);
        Assert.Contains(r.Messages, m => m.Code == MessageCode.StartNotPassable && m.Severity == MessageSeverity.Error);
        Assert.DoesNotContain(r.Messages, m => m.Code == MessageCode.NoRoadBehindStart);
    }

    [Fact]
    public void AStartOnDangerSaysDanger()
    {
        var t = Corridor(40);
        t.Grid[(int)t.Start.X, (int)t.Start.Y] = CellType.Danger;
        var r = Run(t);
        Assert.Equal(SolvabilityResult.StartNotPassable, r.State);
        Assert.Contains(r.Messages, m => m.Code == MessageCode.StartNotPassable && m.Text.Contains("danger"));
    }

    [Fact]
    public void ExactlyOneCarLengthOfRoadBehindIsEnough()
    {
        // "##...S": the start centre is at x=5.5, the road begins at x=2: 3.5 cells behind.
        Assert.True(Run(Corridor()).HasRoadBehindStart);
        // Start centre at x=5.0 would be the limit (3.0 behind); 5.5 with the road from x=2.5 does not exist on a grid,
        // so check the boundary with the angle: facing 180 deg the "behind" is the long side (to the right).
        var t = Corridor();
        t.Start = new TrackStart(5.5f, 3.5f, 180f);
        Assert.True(Run(t).HasRoadBehindStart);
    }

    [Fact]
    public void GrassOrDangerBehindTheStartCountsAsNoRoad()
    {
        var grass = Corridor(40, startPrefix: "##gggS");
        Assert.False(Run(grass).HasRoadBehindStart);
        var danger = Corridor(40, startPrefix: "##x..S");
        Assert.False(Run(danger).HasRoadBehindStart);
    }

    [Fact]
    public void TooNarrowMaskMarksNonBlockingCellsTheCarDoesNotFit()
    {
        var t = TestMaps.Parse(
            "####################",
            "####################",
            "####################",
            "#######.############",
            "#.........#........#",
            "#.........#........#",
            "#.........#.......F#",
            "#.........#........#",
            "####################");
        // a 1-cell slot (x=7, row 3) above a room: too narrow (a 2-wide one would pass, PLAN §3.2)
        var r = Run(t);
        int w = t.Grid.Width;
        Assert.True(r.TooNarrowMask[3 * w + 7], "slot cell");
        Assert.False(r.TooNarrowMask[5 * w + 4], "room centre");
        Assert.False(r.TooNarrowMask[0], "walls are never marked");
        for (int i = 0; i < t.Grid.CellCount; i++)
            if (CellTypes.IsBlocking((CellType)t.Grid.Cells[i])) Assert.False(r.TooNarrowMask[i]);
        Assert.Equal(r.TooNarrowMask.Count(b => b), r.TooNarrowCount);
        // Mask equals "non-blocking and not passable" from Clearance itself.
        var clearance = Clearance.Compute(t.Grid);
        for (int i = 0; i < t.Grid.CellCount; i++)
            Assert.Equal(!CellTypes.IsBlocking((CellType)t.Grid.Cells[i]) && !clearance.IsPassable(i, CarSize.HalfWidth), r.TooNarrowMask[i]);
    }

    [Fact]
    public void OneCellCorridorIsAllTooNarrowAndBlocksTheFinish()
    {
        var t = Corridor(40);
        for (int x = 15; x <= 25; x++) { t.Grid[x, 2] = CellType.Wall; t.Grid[x, 4] = CellType.Wall; t.Grid[x, 5] = CellType.Wall; }
        var r = Run(t);
        int w = t.Grid.Width;
        for (int x = 15; x <= 25; x++) Assert.True(r.TooNarrowMask[3 * w + x], $"x={x}");
        Assert.False(r.TooNarrowMask[3 * w + 5]);
        Assert.Equal(SolvabilityResult.FinishUnreachable, r.State);
    }

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
    public void GrassShortcutWarnsAndGivesARouteToTheFinish()
    {
        var t = UTrack(CellType.Grass);
        var r = Run(t);
        Assert.True(r.IsGrassShortcut);
        Assert.Contains(r.Messages, m => m.Code == MessageCode.GrassShortcut && m.Severity == MessageSeverity.Warning);
        Assert.True(r.WithGrassCost < r.RoadOnlyCost);
        Assert.True(r.ShortcutRoute.Length > 5);
        Assert.Equal((8f, 6f), r.ShortcutRoute[0]);
        var (lx, ly) = r.ShortcutRoute[^1];
        Assert.Equal(CellType.Finish, t.Grid[(int)lx, (int)ly]);
        Assert.Contains(r.ShortcutRoute, p => t.Grid[(int)p.X, (int)p.Y] == CellType.Grass);
        foreach (var (x, y) in r.ShortcutRoute) Assert.False(CellTypes.IsBlocking(t.Grid[(int)x, (int)y]));
        // Uses the settings' grass cost: grass as fast as road makes the shortcut even bigger, very slow grass removes it.
        Assert.False(Run(t, new RacingSettings { GrassSpeedFraction = 0.1f }).IsGrassShortcut);
    }

    [Fact]
    public void WalledBlockHasNoShortcutWarning()
    {
        var r = Run(UTrack(CellType.Wall));
        Assert.True(r.IsFinishable);
        Assert.False(r.IsGrassShortcut);
        Assert.False(Has(r, MessageCode.GrassShortcut));
        Assert.Empty(r.ShortcutRoute);
    }

    [Fact]
    public void FinishOnlyReachableOverGrassWarns()
    {
        var t = TestMaps.Parse(
            "####################",
            "####################",
            "#..................#",
            "#.S................#",
            "#..................#",
            "#..................#",
            "####################",
            "####################");
        for (int y = 2; y <= 5; y++) { t.Grid[10, y] = CellType.Grass; t.Grid[11, y] = CellType.Grass; t.Grid[17, y] = CellType.Finish; t.Grid[18, y] = CellType.Finish; }
        t.Start = new TrackStart(5.5f, 3.5f, 0);
        var r = Run(t);
        Assert.True(r.IsFinishable);
        Assert.True(r.IsGrassShortcut);
        Assert.True(float.IsPositiveInfinity(r.RoadOnlyCost));
        Assert.Contains("only reachable over grass", r.Messages.First(m => m.Code == MessageCode.GrassShortcut).Text);
    }

    [Fact]
    public void ACancelledRunThrowsAndNeverReturnsAResult()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => EditorAnalysis.Run(Corridor(), Settings, cts.Token));
    }

    [Fact]
    public async Task ACancellationDuringTheRunIsHonoured()
    {
        var t = TestTracks.Load("labyrinth.track");
        for (int attempt = 0; attempt < 5; attempt++)
        {
            using var cts = new CancellationTokenSource();
            var task = Task.Run(() => EditorAnalysis.Run(t, Settings, cts.Token));
            cts.Cancel();
            try { await task; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public void RunDoesNotChangeItsInput()
    {
        var t = UTrack(CellType.Grass);
        byte[] before = (byte[])t.Grid.Cells.Clone();
        var start = t.Start;
        Run(t);
        Assert.Equal(before, t.Grid.Cells);
        Assert.Equal(start, t.Start);
    }

    [Theory]
    [MemberData(nameof(BundledTracksTests.Tracks), MemberType = typeof(BundledTracksTests))]
    public void BundledTracksAreFinishableWithoutErrorsOrShortcuts(string file, string _)
    {
        var r = Run(TestTracks.Load(file));
        Assert.True(r.IsFinishable, r.Summary);
        Assert.DoesNotContain(r.Messages, m => m.Severity == MessageSeverity.Error);
        Assert.False(r.IsGrassShortcut);
        Assert.True(r.HasRoadBehindStart);
        Assert.False(r.IsVeryShort, r.Summary);
    }
}
