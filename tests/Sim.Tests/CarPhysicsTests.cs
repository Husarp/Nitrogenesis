using Nitrogenesis.Sim.Brain;
using Nitrogenesis.Sim.Map;
using Nitrogenesis.Sim.Racing;

public class CarPhysicsTests
{
    /// <summary>The fastest car the settings allow (PLAN §9: tunnelling at the highest allowed max speed).</summary>
    private static readonly RacingSettings Fastest = new()
    {
        MaxSpeed = RacingSettings.MaxMaxSpeed,
        Acceleration = RacingSettings.MaxAcceleration,
    };

    private static Grid Arena(int w, int h, CellType fill = CellType.Road) => new TrackBuilder(new Grid(w, h)).Fill(fill).Grid;

    private static CarPhysics Physics(Grid g, RacingSettings? s = null) => new(g, Clearance.Compute(g), s ?? new RacingSettings());

    private static CarState Car(float x, float y, float headingDeg, float speed)
    {
        var car = new CarState(x, y, FastMath.DegreesToUnits(headingDeg));
        car.Vx = speed * car.DirX;
        car.Vy = speed * car.DirY;
        return car;
    }

    private static float Speed(in CarState c) => MathF.Sqrt(c.Vx * c.Vx + c.Vy * c.Vy);

    // ---- tunnelling (PLAN §9) ----

    /// <summary>Vertical 1-cell wall at x = 40; the car drives into it at full speed from the left.</summary>
    [Theory]
    [InlineData(0f)]    // head-on
    [InlineData(45f)]
    [InlineData(-45f)]
    [InlineData(80f)]   // shallow: 10° off parallel
    [InlineData(87f)]   // very shallow
    [InlineData(-85f)]
    public void DoesNotTunnelThroughOneCellWallAtTopSpeed(float headingDeg)
    {
        var g = Arena(80, 400);
        new TrackBuilder(g).Rect(40, 0, 40, 399, CellType.Wall);
        var p = Physics(g, Fastest);
        var car = Car(36f, 200f, headingDeg, RacingSettings.MaxMaxSpeed);
        bool touched = false;
        for (int t = 0; t < 240; t++)
        {
            var r = p.Tick(ref car, 1f, 0f);
            touched |= r.HitWall;
            Assert.Equal(TickOutcome.Moving, r.Outcome);
            Assert.True(car.X < 40f, $"tick {t}: centre crossed the wall at x = {car.X}");
            Assert.False(p.Overlaps(car), $"tick {t}: outline overlaps the wall at ({car.X}, {car.Y})");
        }
        Assert.True(touched);
    }

    /// <summary>A 45° wall made of single cells that only touch at their corners: the thinnest diagonal wall possible.</summary>
    [Theory]
    [InlineData(-45f)] // head-on into the diagonal
    [InlineData(-10f)]
    [InlineData(-80f)]
    [InlineData(-40f)]
    public void DoesNotTunnelThroughCornerTouchingDiagonalWall(float headingDeg)
    {
        var g = Arena(300, 300);
        for (int i = 0; i < 300; i++) g[i, i] = CellType.Wall;
        var p = Physics(g, Fastest);
        var car = Car(100f, 140f, headingDeg, RacingSettings.MaxMaxSpeed); // below the diagonal (y > x)
        bool touched = false;
        for (int t = 0; t < 240; t++)
        {
            touched |= p.Tick(ref car, 1f, 0f).HitWall;
            Assert.True(car.Y > car.X, $"tick {t}: crossed the diagonal at ({car.X}, {car.Y})");
            Assert.False(p.Overlaps(car), $"tick {t}: outline overlaps the wall");
        }
        Assert.True(touched);
    }

    [Fact]
    public void DoesNotTunnelThroughSingleWallCellWhileSteering()
    {
        // A lone wall pillar and a car circling hard around and into it at top speed.
        var g = Arena(120, 120);
        g[60, 60] = CellType.Wall;
        var p = Physics(g, Fastest);
        var car = Car(50f, 60.5f, 0f, RacingSettings.MaxMaxSpeed);
        for (int t = 0; t < 600; t++)
        {
            p.Tick(ref car, 1f, (t / 40) % 2 == 0 ? 0.3f : -0.3f);
            Assert.False(p.Overlaps(car), $"tick {t}: outline overlaps the pillar at ({car.X}, {car.Y})");
        }
    }

    // ---- wall modes ----

    /// <summary>
    /// Runs the car at 45° into a vertical wall and returns its velocity after the first contact tick, plus the
    /// velocity the same tick produces with no wall (= the velocity just before the contact).
    /// </summary>
    private static (CarState Hit, CarState Free, TickResult Result) FirstContact(WallMode mode)
    {
        var settings = new RacingSettings { Walls = mode };
        var walled = Arena(60, 60);
        new TrackBuilder(walled).Rect(40, 0, 59, 59, CellType.Wall);
        var p = Physics(walled, settings);
        var free = Physics(Arena(60, 60), settings);
        var car = Car(30f, 20f, 45f, 15f);
        for (int t = 0; t < 120; t++)
        {
            var before = car;
            var r = p.Tick(ref car, 1f, 0f);
            if (r.HitWall || r.Outcome != TickOutcome.Moving)
            {
                var reference = before;
                free.Tick(ref reference, 1f, 0f);
                return (car, reference, r);
            }
        }
        throw new Xunit.Sdk.XunitException("The car never reached the wall.");
    }

    [Fact]
    public void BlockRemovesNormalVelocityAndKeepsHalfTheTangential()
    {
        var (hit, free, r) = FirstContact(WallMode.Block);
        Assert.Equal(TickOutcome.Moving, r.Outcome);
        Assert.True(free.Vx > 1f && free.Vy > 1f);
        Assert.Equal(0f, hit.Vx);
        Assert.Equal(free.Vy * RacingSettings.WallKeepTangential, hit.Vy);
    }

    [Fact]
    public void BounceReversesNormalVelocity()
    {
        var (hit, free, r) = FirstContact(WallMode.Bounce);
        Assert.Equal(TickOutcome.Moving, r.Outcome);
        Assert.Equal(-free.Vx * RacingSettings.BounceRestitution, hit.Vx);
        Assert.Equal(free.Vy * RacingSettings.WallKeepTangential, hit.Vy);
    }

    [Fact]
    public void KillModeDestroysTheCarOnWallContact()
    {
        var (hit, _, r) = FirstContact(WallMode.Kill);
        Assert.Equal(TickOutcome.Crashed, r.Outcome);
        Assert.True(r.HitWall);
        Assert.Equal(0f, Speed(hit));
    }

    [Fact]
    public void BlockedCarKeepsSlidingAlongTheWall()
    {
        var g = Arena(60, 200);
        new TrackBuilder(g).Rect(40, 0, 59, 199, CellType.Wall);
        var p = Physics(g);
        var car = Car(37f, 20f, 70f, 10f); // 20° into the wall, about to touch it
        int contacts = 0;
        float lastY = car.Y;
        for (int t = 0; t < 60; t++)
        {
            if (p.Tick(ref car, 1f, 0f).HitWall) contacts++;
            Assert.True(car.Y > lastY, $"tick {t}: stopped sliding");
            Assert.False(p.Overlaps(car));
            lastY = car.Y;
        }
        Assert.True(contacts > 30);
    }

    [Fact]
    public void RotationIntoAWallIsRefused()
    {
        var g = Arena(40, 40);
        new TrackBuilder(g).Rect(0, 0, 39, 9, CellType.Wall); // wall above y = 10
        var p = Physics(g);
        // Parallel to the wall, 0.01 cell from it; steering towards it (−y) may only rotate until a corner
        // touches (about 0.4°), never into the wall.
        var car = Car(20f, 10f + CarSize.HalfWidth + 0.01f, 0f, 0f);
        Assert.False(p.Overlaps(car));
        for (int t = 0; t < 30; t++)
        {
            p.Tick(ref car, 1f, -1f);
            Assert.False(p.Overlaps(car));
        }
        int turned = FastMath.AngleUnitsPerTurn - car.Heading;
        Assert.InRange(turned, 1, 100);
        Assert.True(car.X > 20.5f);
    }

    // ---- danger, grass, driving ----

    [Fact]
    public void DangerKills()
    {
        var g = Arena(80, 20);
        new TrackBuilder(g).Rect(50, 0, 50, 19, CellType.Danger);
        var p = Physics(g, Fastest);
        var car = Car(10f, 10f, 0f, 0f);
        TickResult r = default;
        int t = 0;
        for (; t < 300 && r.Outcome == TickOutcome.Moving; t++) r = p.Tick(ref car, 1f, 0f);
        Assert.Equal(TickOutcome.Crashed, r.Outcome);
        Assert.True(car.X + CarSize.HalfLength <= 50.5f && car.X + CarSize.HalfLength > 49f, $"died at x = {car.X}");
    }

    [Fact]
    public void ReachesRoadTopSpeedAndNoMore()
    {
        var p = Physics(Arena(400, 20));
        var car = Car(10f, 10f, 0f, 0f);
        for (int t = 0; t < 180; t++) p.Tick(ref car, 1f, 0f);
        Assert.Equal(18f, car.Vx, 4);
        Assert.Equal(0f, car.Vy);
    }

    /// <summary>
    /// For every allowed acceleration and max speed, forward and reverse, on road and on grass: a tick never
    /// raises |forward speed| above max(cap, |forward speed| before the tick). Regression: the overspeed bleed was
    /// once a fixed 0.5 cells/s per tick, which accelerations above ~35 cells/s² outran without limit.
    /// </summary>
    [Theory]
    [InlineData(CellType.Road)]
    [InlineData(CellType.Grass)]
    public void TopSpeedAndReverseCapHoldForEveryAccelerationAndMaxSpeed(CellType surface)
    {
        var g = Arena(4000, 20, surface);
        var clearance = Clearance.Compute(g);
        foreach (float accel in new[] { 2f, 14f, 30f, 36f, 45f, 60f })
            foreach (float maxSpeed in new[] { 5f, 18f, 35f, 60f })
            {
                var s = new RacingSettings { Acceleration = accel, MaxSpeed = maxSpeed };
                var p = new CarPhysics(g, clearance, s);
                float top = surface == CellType.Grass ? maxSpeed * s.GrassSpeedFraction : maxSpeed;
                float reverseTop = MathF.Min(RacingSettings.ReverseCap, top);
                foreach (float throttle in new[] { 1f, -1f })
                {
                    // Start at road top speed so the grass case also covers bleeding down from overspeed.
                    var car = Car(2000f, 10f, 0f, throttle > 0 ? maxSpeed : -maxSpeed);
                    for (int t = 0; t < 300; t++)
                    {
                        float before = MathF.Abs(car.ForwardSpeed);
                        p.Tick(ref car, throttle, 0f);
                        float after = MathF.Abs(car.ForwardSpeed);
                        float cap = throttle > 0 ? top : reverseTop;
                        Assert.True(after <= MathF.Max(cap, before) + 1e-4f,
                            $"a={accel} max={maxSpeed} throttle={throttle} tick {t}: {before} → {after} (cap {cap})");
                    }
                    Assert.Equal(throttle > 0 ? top : -reverseTop, car.ForwardSpeed, 3);
                }
            }
    }

    [Fact]
    public void GrassSlowsTheCarToItsTopSpeed()
    {
        var p = Physics(Arena(400, 20, CellType.Grass));
        var car = Car(10f, 10f, 0f, 18f); // runs onto grass at full road speed
        p.Tick(ref car, 1f, 0f);
        Assert.True(car.Vx < 18f && car.Vx > 0.45f * 18f, "grass bleeds speed gradually");
        for (int t = 0; t < 120; t++) p.Tick(ref car, 1f, 0f);
        Assert.Equal(0.45f * 18f, car.Vx, 4);
        Assert.True(car.X < 400 - 10);
    }

    [Fact]
    public void GrassLetsTheCarDrift()
    {
        // Same hard turn on road and on grass: on road no lateral slip remains, on grass some does.
        foreach (var (surface, drifts) in new[] { (CellType.Road, false), (CellType.Grass, true) })
        {
            var p = Physics(Arena(200, 200, surface));
            var car = Car(100f, 100f, 0f, 8f);
            p.Tick(ref car, 1f, 1f);
            Assert.Equal(drifts, MathF.Abs(car.LateralSpeed) > 0.01f);
        }
    }

    [Fact]
    public void BrakesThenReversesUpToTheCap()
    {
        var p = Physics(Arena(400, 20));
        var car = Car(200f, 10f, 0f, 18f);
        p.Tick(ref car, -1f, 0f);
        Assert.Equal(18f - (RacingSettings.Brake + RacingSettings.RoadDragFraction * 14f) / 60f, car.Vx, 4);
        for (int t = 0; t < 300; t++) p.Tick(ref car, -1f, 0f);
        Assert.Equal(-RacingSettings.ReverseCap, car.Vx, 4);
    }

    [Fact]
    public void NoSteeringWhileStoppedAndFullRateAtSpeed()
    {
        var p = Physics(Arena(200, 200));
        var car = Car(100f, 100f, 0f, 0f);
        p.Tick(ref car, 0f, 1f);
        Assert.Equal(0, car.Heading);
        Assert.Equal(0f, car.YawRate);

        car = Car(100f, 100f, 0f, 18f);
        p.Tick(ref car, 1f, 1f);
        float expected = 200f * FastMath.DegToRad;
        Assert.Equal(expected, car.YawRate, 2);
        Assert.Equal(p.MaxTurnRate, car.YawRate, 2);
        Assert.Equal((int)MathF.Round(200f / 360f * 65536 / 60), car.Heading);
    }

    [Fact]
    public void OutputsAreClampedAndNaNIsZero()
    {
        var p = Physics(Arena(200, 20));
        var a = Car(10f, 10f, 0f, 5f);
        var b = a;
        p.Tick(ref a, 7f, float.NaN);
        p.Tick(ref b, 1f, 0f);
        Assert.Equal(a, b);
    }

    [Fact]
    public void OutlineSamplesAreAtMostHalfACellApart()
    {
        var xs = CarPhysics.OutlinePointsX;
        var ys = CarPhysics.OutlinePointsY;
        Assert.Equal(20, xs.Length);
        // Every point of the rectangle's perimeter has a sample within 0.25 (half the spacing).
        for (int i = 0; i <= 480; i++)
        {
            float u = i / 480f * 2 * (CarSize.Length + CarSize.Width);
            float px, py;
            if (u < CarSize.Length) (px, py) = (-1.5f + u, -0.9f);
            else if ((u -= CarSize.Length) < CarSize.Width) (px, py) = (1.5f, -0.9f + u);
            else if ((u -= CarSize.Width) < CarSize.Length) (px, py) = (1.5f - u, 0.9f);
            else (px, py) = (-1.5f, 0.9f - (u - CarSize.Length));
            float best = float.MaxValue;
            for (int k = 0; k < xs.Length; k++)
                best = MathF.Min(best, MathF.Max(MathF.Abs(xs[k] - px), MathF.Abs(ys[k] - py)));
            Assert.True(best <= CarPhysics.MaxOutlineSpacing / 2 + 1e-5f, $"gap at ({px}, {py})");
        }
    }

    // ---- finish ----

    [Fact]
    public void FinishIsDetectedWithSubTickTime()
    {
        var g = Arena(80, 20);
        new TrackBuilder(g).Rect(40, 0, 45, 19, CellType.Finish);
        var p = Physics(g);
        var car = Car(10f, 10.5f, 0f, 0f);
        for (int t = 0; t < 600; t++)
        {
            float x0 = car.X;
            var r = p.Tick(ref car, 1f, 0f);
            if (r.Outcome == TickOutcome.Moving) continue;
            Assert.Equal(TickOutcome.Finished, r.Outcome);
            // Velocity is constant within a tick, so the centre crossed x = 40 at (40 − x0) / (vx·dt) of it.
            float expected = (40f - x0) / (car.Vx / 60f);
            Assert.Equal(expected, r.FinishFraction, 4);
            Assert.True(r.FinishFraction is > 0f and <= 1f);
            Assert.Equal(CellType.Finish, g[(int)car.X, (int)car.Y]);
            return;
        }
        Assert.Fail("never finished");
    }

    [Fact]
    public void FinishFractionIsExactOnADiagonalMove()
    {
        var g = Arena(80, 80);
        new TrackBuilder(g).Rect(40, 0, 79, 79, CellType.Finish);
        var p = Physics(g);
        var car = Car(20f, 20f, 30f, 0f);
        for (int t = 0; t < 600; t++)
        {
            float x0 = car.X;
            var r = p.Tick(ref car, 1f, 0f);
            if (r.Outcome != TickOutcome.Finished) continue;
            float expected = (40f - x0) / (car.Vx / 60f);
            Assert.Equal(expected, r.FinishFraction, 3);
            return;
        }
        Assert.Fail("never finished");
    }
}
