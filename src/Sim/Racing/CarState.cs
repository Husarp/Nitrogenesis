using Nitrogenesis.Sim.Brain;

namespace Nitrogenesis.Sim.Racing;

/// <summary>
/// One car's physical state (PLAN §3.3: position, heading, velocity, angular speed). <see cref="RacingMode"/>
/// stores these fields in per-agent arrays and loads them into this struct as a working copy for one tick.
/// </summary>
public struct CarState
{
    /// <summary>Centre position, world units (cells).</summary>
    public float X, Y;
    /// <summary>Velocity, cells/s.</summary>
    public float Vx, Vy;
    /// <summary>Heading in fixed-point angle units (<see cref="FastMath.AngleUnitsPerTurn"/> per turn), in [0, 65536).</summary>
    public int Heading;
    /// <summary>Turn rate applied in the last tick, radians/s (positive turns towards +y).</summary>
    public float YawRate;

    public CarState(float x, float y, int heading)
    {
        X = x;
        Y = y;
        Heading = heading & FastMath.AngleUnitsMask;
    }

    /// <summary>Unit vector of the heading.</summary>
    public readonly float DirX => FastMath.CosUnits(Heading);
    public readonly float DirY => FastMath.SinUnits(Heading);

    /// <summary>Velocity along the heading (negative when reversing).</summary>
    public readonly float ForwardSpeed => Vx * DirX + Vy * DirY;

    /// <summary>Velocity across the heading, positive towards the car's right (+y when heading along +x).</summary>
    public readonly float LateralSpeed => Vy * DirX - Vx * DirY;
}
