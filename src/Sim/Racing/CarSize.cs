namespace Nitrogenesis.Sim.Racing;

/// <summary>The car's hitbox in cells (PLAN §3.3: fixed, the same for every car model).</summary>
public static class CarSize
{
    public const float Length = 3.0f;
    public const float Width = 1.8f;
    public const float HalfLength = Length / 2;
    /// <summary>A cell is passable for the car centre when its clearance is at least this (PLAN §3.2).</summary>
    public const float HalfWidth = Width / 2;
}
