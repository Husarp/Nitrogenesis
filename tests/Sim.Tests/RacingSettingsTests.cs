using Nitrogenesis.Sim.Racing;

public class RacingSettingsTests
{
    [Fact]
    public void StartValuesMatchThePlan()
    {
        var s = new RacingSettings();
        Assert.Equal((18f, 14f, 200f, 0.45f, 0.45f), (s.MaxSpeed, s.Acceleration, s.TurnRateDeg, s.GrassSpeedFraction, s.GrassGrip));
        Assert.Equal((30f, 4f), (RacingSettings.Brake, RacingSettings.ReverseCap));
        Assert.Equal((7, 180f, 30f, true, false), (s.RayCount, s.RaySpreadDeg, s.RayRange, s.MemoryInputs, s.DirectionHint));
        Assert.Equal(WallMode.Block, s.Walls);
        Assert.Equal(3f, s.StallSeconds);
        Assert.Equal(0f, s.TimeLimitSeconds); // auto
        Assert.Equal(s, s.Clamped());
        Assert.Equal(1 / 0.45, s.GrassCost, 6);
    }

    [Fact]
    public void ClampedForcesHardLimits()
    {
        var low = new RacingSettings
        {
            MaxSpeed = 1, Acceleration = 0, TurnRateDeg = 10, GrassSpeedFraction = 0, GrassGrip = -1,
            RayCount = 1, RaySpreadDeg = 10, RayRange = 1, StallSeconds = 0, TimeLimitSeconds = -5, Walls = (WallMode)9,
        }.Clamped();
        Assert.Equal((5f, 2f, 60f, 0.1f, 0.1f), (low.MaxSpeed, low.Acceleration, low.TurnRateDeg, low.GrassSpeedFraction, low.GrassGrip));
        Assert.Equal((3, 90f, 10f, 0.5f, 0f), (low.RayCount, low.RaySpreadDeg, low.RayRange, low.StallSeconds, low.TimeLimitSeconds));
        Assert.Equal(WallMode.Block, low.Walls);

        var high = new RacingSettings
        {
            MaxSpeed = 99, Acceleration = 99, TurnRateDeg = 999, GrassSpeedFraction = 2, GrassGrip = 2,
            RayCount = 99, RaySpreadDeg = 999, RayRange = 999, StallSeconds = 999, TimeLimitSeconds = 1e6f,
        }.Clamped();
        Assert.Equal((60f, 60f, 400f, 1f, 1f), (high.MaxSpeed, high.Acceleration, high.TurnRateDeg, high.GrassSpeedFraction, high.GrassGrip));
        Assert.Equal((15, 360f, 80f, 60f, 3600f), (high.RayCount, high.RaySpreadDeg, high.RayRange, high.StallSeconds, high.TimeLimitSeconds));

        var nan = new RacingSettings { MaxSpeed = float.NaN, RayRange = float.NaN }.Clamped();
        Assert.Equal((18f, 30f), (nan.MaxSpeed, nan.RayRange));
    }
}
