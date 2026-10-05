using Nitrogenesis.Sim.Core;

namespace Nitrogenesis.Sim.Racing;

/// <summary>What touching a wall does to the car (PLAN §3.3; default <see cref="Block"/>).</summary>
public enum WallMode : byte
{
    /// <summary>Remove the velocity component along the wall normal and keep 50 % of the tangential speed.</summary>
    Block = 0,
    /// <summary>Reverse the normal component at <see cref="RacingSettings.BounceRestitution"/>; tangential as in Block.</summary>
    Bounce = 1,
    /// <summary>The car is destroyed, like on danger.</summary>
    Kill = 2,
}

/// <summary>
/// Racing settings: car physics (PLAN §3.3), sensors (§3.4) and timing (§3.6). Properties hold the start
/// values; <see cref="Clamped"/> forces every value into its hard limits. Fixed constants (no setting) are
/// <c>const</c> here so they are written into the <see cref="SimConfig"/> hash too.
/// </summary>
/// <remarks>Units: cells, seconds, degrees. One tick is 1/60 s (<see cref="TicksPerSecond"/>).</remarks>
public sealed record RacingSettings : IModeSettings
{
    public const int TicksPerSecond = 60;
    public const float Dt = 1f / TicksPerSecond;

    // ---- physics (§3.3) ----

    /// <summary>Top speed on road, cells/s. Limits 5…60.</summary>
    public float MaxSpeed { get; init; } = 18f;
    public const float MinMaxSpeed = 5f, MaxMaxSpeed = 60f;

    /// <summary>Forward (and reverse) acceleration at full throttle, cells/s². Limits 2…60.</summary>
    public float Acceleration { get; init; } = 14f;
    public const float MinAcceleration = 2f, MaxAcceleration = 60f;

    /// <summary>Turn rate at full steer once the car is fast enough, degrees/s. Limits 60…400.</summary>
    public float TurnRateDeg { get; init; } = 200f;
    public const float MinTurnRateDeg = 60f, MaxTurnRateDeg = 400f;

    /// <summary>Top speed on grass as a fraction of <see cref="MaxSpeed"/>. Limits 10…100 %.</summary>
    public float GrassSpeedFraction { get; init; } = 0.45f;

    /// <summary>
    /// Grass grip: the fraction of lateral (sideways) velocity removed each tick on grass (road grip is
    /// <see cref="RoadGrip"/> = 1, no drift). Limits 0.1…1.
    /// </summary>
    public float GrassGrip { get; init; } = 0.45f;
    public const float MinGrassFraction = 0.1f, MaxGrassFraction = 1f;

    public WallMode Walls { get; init; } = WallMode.Block;

    /// <summary>Braking deceleration at full negative throttle while rolling forward, cells/s² (fixed).</summary>
    public const float Brake = 30f;
    /// <summary>Highest reverse speed, cells/s (fixed).</summary>
    public const float ReverseCap = 4f;
    /// <summary>Road grip: all lateral velocity is removed every tick (fixed).</summary>
    public const float RoadGrip = 1f;
    /// <summary>
    /// Rolling drag on road: a constant deceleration towards standstill, as a share of <see cref="Acceleration"/>
    /// (fixed). Tied to the acceleration so that even the weakest allowed car (2 cells/s²) can still move:
    /// with the defaults it is 2.1 cells/s².
    /// </summary>
    public const float RoadDragFraction = 0.15f;
    /// <summary>Rolling drag on grass ("high drag"), as a share of <see cref="Acceleration"/> (fixed; 7 cells/s² by default).</summary>
    public const float GrassDragFraction = 0.5f;
    /// <summary>Deceleration while faster than the surface's top speed (e.g. after running onto grass), cells/s² (fixed).</summary>
    public const float OverspeedDecel = Brake;
    /// <summary>The turn rate grows linearly with forward speed up to this fraction of <see cref="MaxSpeed"/> (fixed).</summary>
    public const float FullSteerSpeedFraction = 0.3f;
    /// <summary>Share of the tangential speed kept after a wall contact (block and bounce; fixed).</summary>
    public const float WallKeepTangential = 0.5f;
    /// <summary>Share of the normal speed that bounces back in <see cref="WallMode.Bounce"/> (fixed).</summary>
    public const float BounceRestitution = 0.5f;

    // ---- sensors (§3.4) ----

    /// <summary>Number of rays. Limits 3…15.</summary>
    public int RayCount { get; init; } = 7;
    public const int MinRayCount = 3, MaxRayCount = 15;

    /// <summary>Angle covered by the rays, centred on the heading, degrees. Limits 90…360.</summary>
    public float RaySpreadDeg { get; init; } = 180f;
    public const float MinRaySpreadDeg = 90f, MaxRaySpreadDeg = 360f;

    /// <summary>Ray length, cells. Limits 10…80.</summary>
    public float RayRange { get; init; } = 30f;
    public const float MinRayRange = 10f, MaxRayRange = 80f;

    /// <summary>Previous throttle/steer, elapsed time and distance driven as inputs (on by default).</summary>
    public bool MemoryInputs { get; init; } = true;

    /// <summary>The angle to the distance field's downhill direction as an input (off by default).</summary>
    public bool DirectionHint { get; init; }

    // ---- timing (§3.6) ----

    /// <summary>Stop a car whose best distance has not improved by <see cref="RacingFitness.StallDistance"/> for this long, s. Limits 0.5…60.</summary>
    public float StallSeconds { get; init; } = 3f;
    public const float MinStallSeconds = 0.5f, MaxStallSeconds = 60f;

    /// <summary>Generation time limit in seconds; 0 = auto (reference time × <see cref="RacingFitness.AutoTimeLimitFactor"/>). Limits 1…3600 when set.</summary>
    public float TimeLimitSeconds { get; init; }
    public const float MinTimeLimitSeconds = 1f, MaxTimeLimitSeconds = 3600f;

    // ---- derived ----

    public string ModeId => "racing";

    /// <summary>Brain outputs: throttle, steer.</summary>
    public const int OutputCount = 2;
    /// <summary>Added to the throttle output's bias in fresh random genomes, so generation-0 cars start moving (PLAN §3.5).</summary>
    public const float InitialThrottleBias = 0.3f;

    /// <summary>Per-output bias offsets for fresh random genomes: [throttle, steer].</summary>
    public static float[] InitialOutputBias() => [InitialThrottleBias, 0f];

    /// <summary>Inputs per ray: distance to the first non-road cell and to the first wall/danger cell.</summary>
    public const int InputsPerRay = 2;
    public const int BodyInputCount = 4;
    public const int MemoryInputCount = 4;

    /// <summary>Total sensor inputs (default 7×2 + 4 + 4 = 22).</summary>
    public int InputCount => RayCount * InputsPerRay + BodyInputCount + (MemoryInputs ? MemoryInputCount : 0) + (DirectionHint ? 1 : 0);

    /// <summary>Distance-field cost of grass relative to road = roadTopSpeed / grassTopSpeed (PLAN §3.2).</summary>
    public double GrassCost => 1.0 / GrassSpeedFraction;

    /// <summary>A copy with every value inside its hard limits; NaN falls back to the start value.</summary>
    public RacingSettings Clamped()
    {
        var d = new RacingSettings();
        return this with
        {
            MaxSpeed = Clamp(MaxSpeed, MinMaxSpeed, MaxMaxSpeed, d.MaxSpeed),
            Acceleration = Clamp(Acceleration, MinAcceleration, MaxAcceleration, d.Acceleration),
            TurnRateDeg = Clamp(TurnRateDeg, MinTurnRateDeg, MaxTurnRateDeg, d.TurnRateDeg),
            GrassSpeedFraction = Clamp(GrassSpeedFraction, MinGrassFraction, MaxGrassFraction, d.GrassSpeedFraction),
            GrassGrip = Clamp(GrassGrip, MinGrassFraction, MaxGrassFraction, d.GrassGrip),
            Walls = Enum.IsDefined(Walls) ? Walls : d.Walls,
            RayCount = Math.Clamp(RayCount, MinRayCount, MaxRayCount),
            RaySpreadDeg = Clamp(RaySpreadDeg, MinRaySpreadDeg, MaxRaySpreadDeg, d.RaySpreadDeg),
            RayRange = Clamp(RayRange, MinRayRange, MaxRayRange, d.RayRange),
            StallSeconds = Clamp(StallSeconds, MinStallSeconds, MaxStallSeconds, d.StallSeconds),
            // 0 (or anything not positive) means auto.
            TimeLimitSeconds = TimeLimitSeconds > 0 ? Clamp(TimeLimitSeconds, MinTimeLimitSeconds, MaxTimeLimitSeconds, 0) : 0,
        };
    }

    private static float Clamp(float v, float min, float max, float fallback) =>
        float.IsNaN(v) ? fallback : v < min ? min : v > max ? max : v;

    /// <summary>Stall time in whole ticks (rounded up).</summary>
    public int StallTicks => (int)MathF.Ceiling(StallSeconds * TicksPerSecond);

    public void WriteHash(ConfigHashWriter w)
    {
        w.Float("car.length", CarSize.Length);
        w.Float("car.width", CarSize.Width);
        w.Int("ticksPerSecond", TicksPerSecond);
        w.Float("maxSpeed", MaxSpeed);
        w.Float("acceleration", Acceleration);
        w.Float("turnRateDeg", TurnRateDeg);
        w.Float("grassSpeedFraction", GrassSpeedFraction);
        w.Float("grassGrip", GrassGrip);
        w.Int("walls", (int)Walls);
        w.Float("brake", Brake);
        w.Float("reverseCap", ReverseCap);
        w.Float("roadGrip", RoadGrip);
        w.Float("roadDragFraction", RoadDragFraction);
        w.Float("grassDragFraction", GrassDragFraction);
        w.Float("overspeedDecel", OverspeedDecel);
        w.Float("fullSteerSpeedFraction", FullSteerSpeedFraction);
        w.Float("wallKeepTangential", WallKeepTangential);
        w.Float("bounceRestitution", BounceRestitution);
        w.Float("collision.maxSubStep", CarPhysics.MaxSubStep);
        w.Float("collision.maxOutlineSpacing", CarPhysics.MaxOutlineSpacing);
        w.Int("rayCount", RayCount);
        w.Float("raySpreadDeg", RaySpreadDeg);
        w.Float("rayRange", RayRange);
        w.Bool("memoryInputs", MemoryInputs);
        w.Bool("directionHint", DirectionHint);
        w.Float("stallSeconds", StallSeconds);
        w.Float("fitness.startGraceSeconds", RacingFitness.StartGraceSeconds);
        w.Float("fitness.stallDistance", RacingFitness.StallDistance);
        w.Float("fitness.tieBreakWeight", RacingFitness.TieBreakWeight);
        w.Float("fitness.finishBase", RacingFitness.FinishBase);
    }
}
