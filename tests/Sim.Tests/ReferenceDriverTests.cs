using Nitrogenesis.Sim.Core;
using Nitrogenesis.Sim.Map;
using Nitrogenesis.Sim.Racing;

public class ReferenceDriverTests
{
    public static TheoryData<string> Tracks => new(TestTracks.Files);

    [Theory]
    [MemberData(nameof(Tracks))]
    public void FinishesEveryBundledTrackAtAPlausibleTime(string file)
    {
        var rt = new RacingTrack(TestTracks.Load(file), new RacingSettings());
        var r = ReferenceDriver.Run(rt);
        Assert.True(r.Finished, $"{file}: {r.EndStatus} after {r.TimeSeconds:F2} s");
        Assert.Equal(AgentStatus.Finished, r.EndStatus);
        // Never faster than the field's path at top speed (plus the start acceleration), and not much slower.
        float ideal = rt.StartDistance / rt.Settings.MaxSpeed;
        Assert.InRange(r.TimeSeconds, ideal, ideal * 1.25f);
        Assert.Equal((rt.Track.Start.X, rt.Track.Start.Y), r.Path[0]);
        Assert.Equal((int)MathF.Ceiling(r.TimeSeconds * 60), r.Path.Count - 1);
        var (lx, ly) = r.Path[^1];
        Assert.Equal(CellType.Finish, rt.Grid[(int)lx, (int)ly]);
    }

    [Fact]
    public void IsDeterministic()
    {
        var rt = new RacingTrack(TestTracks.Load("hairpins.track"), new RacingSettings());
        var a = ReferenceDriver.Run(rt);
        var b = ReferenceDriver.Run(rt);
        Assert.Equal(a.TimeSeconds, b.TimeSeconds);
        Assert.Equal(a.Path, b.Path);
    }

    [Fact]
    public void FinishesWithTheFastestAndSlowestPhysics()
    {
        foreach (var s in new[]
                 {
                     new RacingSettings { MaxSpeed = 60, Acceleration = 60, TurnRateDeg = 400 },
                     new RacingSettings { MaxSpeed = 5, Acceleration = 2, TurnRateDeg = 60 },
                     new RacingSettings { MaxSpeed = 40, TurnRateDeg = 120 },
                 })
        {
            var r = ReferenceDriver.Run(new RacingTrack(TestTracks.Load("wrong_turn.track"), s));
            Assert.True(r.Finished, $"{s.MaxSpeed} cells/s, {s.TurnRateDeg}°/s: {r.EndStatus}");
        }
    }

    /// <summary>
    /// A U track whose start faces away from the route, close to the dividing wall: turning round presses the
    /// nose into the wall, where the physics refuses the turn. The driver must back off and still finish.
    /// </summary>
    [Theory]
    [InlineData(180f)]
    [InlineData(270f)] // facing straight at the divider
    public void BacksOffWhenItsNoseIsJammedAgainstAWall(float startAngle)
    {
        var b = new TrackBuilder(new Grid(80, 50));
        b.Rect(2, 2, 77, 47, CellType.Road);
        b.Rect(2, 20, 60, 21, CellType.Wall);   // divider; the lane below it is y 22…47
        b.Rect(2, 2, 8, 12, CellType.Finish);   // top left, reached round the divider's right end
        var t = new Track("u", b.Grid, new TrackStart(30.5f, 24.5f, startAngle));
        var rt = new RacingTrack(t, new RacingSettings());
        Assert.True(rt.IsFinishable);
        var r = ReferenceDriver.Run(rt);
        Assert.True(r.Finished, $"{r.EndStatus} after {r.TimeSeconds:F2} s");
    }

    /// <summary>Corridors only just wide enough for the car (no cell centre fits): the driver aims along their midline.</summary>
    [Theory]
    [InlineData(1.0, 0.0)]   // 2 cells wide, straight
    [InlineData(1.75, 30.0)] // diagonal, painted with a 3.5-cell brush
    public void FinishesANarrowCorridor(double radius, double angleDeg)
    {
        var b = new TrackBuilder(new Grid(100, 70));
        double a = angleDeg * Math.PI / 180, x0 = 5, y0 = 10, x1 = x0 + 85 * Math.Cos(a), y1 = y0 + 85 * Math.Sin(a);
        b.Line(x0, y0, x1, y1, radius, CellType.Road);
        b.Line(x0 + 75 * Math.Cos(a), y0 + 75 * Math.Sin(a), x1, y1, radius, CellType.Finish, onlyOver: CellType.Road);
        var start = new TrackStart((float)(x0 + 5 * Math.Cos(a)), (float)(y0 + 5 * Math.Sin(a)), (float)angleDeg);
        var rt = new RacingTrack(new Track("corridor", b.Grid, start), new RacingSettings());
        Assert.True(rt.IsFinishable);
        var r = ReferenceDriver.Run(rt);
        Assert.True(r.Finished, $"{r.EndStatus} after {r.TimeSeconds:F2} s");
    }

    [Fact]
    public void FailsWhenTheFinishIsWalledOff()
    {
        var t = TestTracks.Load("sprint.track");
        new TrackBuilder(t.Grid).Rect(120, 0, 122, 143, CellType.Wall);
        var r = ReferenceDriver.Run(new RacingTrack(t, new RacingSettings()));
        Assert.False(r.Finished);
        Assert.Equal(AgentStatus.Stalled, r.EndStatus);
    }
}
