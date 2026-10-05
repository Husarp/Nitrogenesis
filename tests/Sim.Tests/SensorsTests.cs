using Nitrogenesis.Sim.Brain;
using Nitrogenesis.Sim.Core;
using Nitrogenesis.Sim.Map;
using Nitrogenesis.Sim.Racing;

public class SensorsTests
{
    private static RacingTrack Prepare(Track t, RacingSettings? s = null) => new(t, s ?? new RacingSettings());

    private static Sensors For(RacingTrack rt) => new(rt, new CarPhysics(rt.Grid, rt.Clearance, rt.Settings));

    [Fact]
    public void DefaultInputCountIs22()
    {
        Assert.Equal(22, new RacingSettings().InputCount);
        Assert.Equal(23, new RacingSettings { DirectionHint = true }.InputCount);
        Assert.Equal(18, new RacingSettings { MemoryInputs = false }.InputCount);
        Assert.Equal(15 * 2 + 4 + 4, new RacingSettings { RayCount = 15 }.InputCount);
    }

    [Fact]
    public void RaysSpreadEvenlyAcrossTheFront()
    {
        int[] o = Sensors.RayOffsets(7, 180f);
        Assert.Equal([-16384, -10923, -5461, 0, 5461, 10923, 16384], o);
        // A full circle does not repeat the first ray at the end.
        Assert.Equal([-32768, -16384, 0, 16384], Sensors.RayOffsets(4, 360f));
    }

    [Fact]
    public void RayMeasuresDistanceToFirstNonRoadAndFirstWall()
    {
        // Road up to x = 11, grass at x = 11..12, wall from x = 13.
        var t = TestMaps.Parse(
            "####################",
            "#..........gg#######",
            "#....S.....gg#######",
            "#..........gg#######",
            "####################");
        var s = For(Prepare(t, new RacingSettings { RayRange = 10 }));
        s.CastRay(5.5f, 2.5f, 1f, 0f, out float nonRoad, out float wall);
        Assert.Equal(5.5f, nonRoad, 5); // 11 − 5.5
        Assert.Equal(7.5f, wall, 5);    // 13 − 5.5
        s.CastRay(5.5f, 2.5f, -1f, 0f, out nonRoad, out wall);
        Assert.Equal(4.5f, nonRoad, 5); // wall at x < 1 is also non-road
        Assert.Equal(4.5f, wall, 5);
        s.CastRay(5.5f, 2.5f, 0f, 1f, out nonRoad, out wall);
        Assert.Equal(1.5f, wall, 5);
    }

    [Fact]
    public void RayIsCappedAtRangeAndStartsAtZeroOnGrass()
    {
        var g = new TrackBuilder(new Grid(200, 20)).Fill(CellType.Road).Grid;
        g[10, 10] = CellType.Grass;
        var s = For(Prepare(new Track("t", g, new TrackStart(5, 10, 0)), new RacingSettings { RayRange = 30 }));
        s.CastRay(50.5f, 10.5f, 1f, 0f, out float nonRoad, out float wall);
        Assert.Equal((30f, 30f), (nonRoad, wall));
        s.CastRay(10.5f, 10.5f, 1f, 0f, out nonRoad, out wall);
        Assert.Equal(0f, nonRoad);
        Assert.Equal(30f, wall);
    }

    [Fact]
    public void RayCountsFinishAsRoadAndGivesZeroInsideAWall()
    {
        // Road, a finish strip at x = 6..7, road again, grass at x = 10, road, wall from x = 13.
        var t = TestMaps.Parse(
            "##############",
            "#..S..FF..g..#",
            "##############");
        var s = For(Prepare(t, new RacingSettings { RayRange = 20 }));
        s.CastRay(1.5f, 1.5f, 1f, 0f, out float nonRoad, out float wall);
        Assert.Equal(8.5f, nonRoad, 5); // the grass, not the finish
        Assert.Equal(11.5f, wall, 5);   // past the grass and the road behind it
        s.CastRay(0.5f, 1.5f, 1f, 0f, out nonRoad, out wall);
        Assert.Equal((0f, 0f), (nonRoad, wall));
    }

    [Fact]
    public void DdaMatchesADenseMarchInEveryDirection()
    {
        // A cluttered map; compare the DDA distances against marching the ray in 0.001-cell steps.
        var g = new TrackBuilder(new Grid(60, 60)).Fill(CellType.Road).Grid;
        var b = new TrackBuilder(g);
        b.Rect(40, 10, 42, 50, CellType.Wall).Rect(10, 40, 30, 41, CellType.Danger).Circle(20, 20, 4, CellType.Grass);
        b.Rect(30, 25, 33, 28, CellType.Grass);
        var s = For(Prepare(new Track("t", g, new TrackStart(30, 30, 0)), new RacingSettings { RayRange = 40 }));
        for (int a = 0; a < 360; a += 7)
        {
            int units = FastMath.DegreesToUnits(a);
            float dx = FastMath.CosUnits(units), dy = FastMath.SinUnits(units);
            s.CastRay(29.3f, 31.7f, dx, dy, out float nonRoad, out float wall);
            float expectNonRoad = 40f, expectWall = 40f;
            for (float d = 0; d < 40f; d += 0.001f)
            {
                CellType c = g[(int)MathF.Floor(29.3f + dx * d), (int)MathF.Floor(31.7f + dy * d)];
                if (c != CellType.Road && expectNonRoad == 40f) expectNonRoad = d;
                if (c is CellType.Wall or CellType.Danger) { expectWall = d; break; }
            }
            Assert.True(MathF.Abs(nonRoad - expectNonRoad) < 0.01f, $"angle {a}: non-road {nonRoad} vs {expectNonRoad}");
            Assert.True(MathF.Abs(wall - expectWall) < 0.01f, $"angle {a}: wall {wall} vs {expectWall}");
        }
    }

    [Fact]
    public void WritesBodyAndMemoryInputsInOrder()
    {
        var g = new TrackBuilder(new Grid(100, 40)).Fill(CellType.Grass).Grid;
        var s = For(Prepare(new Track("t", g, new TrackStart(50, 20, 0))));
        var car = new CarState(50.5f, 20.5f, FastMath.AngleUnitsPerTurn / 4) { Vx = -1f, Vy = 9f, YawRate = 1f };
        var dest = new float[22];
        s.Write(car, 0.25f, -0.5f, 0.3f, 0.7f, dest);
        Assert.Equal(9f / 18f, dest[14], 5);   // forward speed (heading +y)
        Assert.Equal(1f / 18f, dest[15], 5);   // lateral: −x is to the right of +y
        Assert.Equal(1f / (200f * FastMath.DegToRad), dest[16], 3);
        Assert.Equal(1f, dest[17]);            // on grass
        Assert.Equal([0.25f, -0.5f, 0.3f, 0.7f], dest[18..22]);
        for (int i = 0; i < 14; i += 2) Assert.Equal(0f, dest[i]); // every ray starts on grass
    }

    [Fact]
    public void DirectionHintPointsDownTheField()
    {
        var t = TestMaps.Parse(
            "##############",
            "#............#",
            "#............#",
            "#.....S....FF#",
            "#............#",
            "#............#",
            "##############");
        var rt = Prepare(t, new RacingSettings { DirectionHint = true });
        Assert.Equal(0f, Sensors.DirectionHint(rt.Field, 6.5f, 3.5f, 0), 3);
        // Heading +y (a quarter turn clockwise): the finish is a quarter turn back.
        Assert.Equal(-0.5f, Sensors.DirectionHint(rt.Field, 6.5f, 3.5f, FastMath.AngleUnitsPerTurn / 4), 2);
        var mode = new RacingMode(rt, 1, 600);
        mode.Sense(AgentRange.All(1));
        Assert.Equal(23, mode.InputCount);
        Assert.Equal(0f, mode.Inputs[22], 3);
    }
}
