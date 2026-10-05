namespace Nitrogenesis.Sim.Map;

/// <summary>
/// What a grid cell is made of (PLAN §3.1). The byte values are stored in .track files, so existing
/// values must never change; new types (Boost, Ice) get new numbers.
/// </summary>
public enum CellType : byte
{
    /// <summary>Full grip, normal drag.</summary>
    Road = 0,
    /// <summary>Low grip, high drag. Slows the car but does not stop it.</summary>
    Grass = 1,
    /// <summary>Solid. Also what everything outside the map counts as.</summary>
    Wall = 2,
    /// <summary>Destroys the car.</summary>
    Danger = 3,
    /// <summary>Road that ends the run when the car centre enters it.</summary>
    Finish = 4,
}

public static class CellTypes
{
    /// <summary>Highest defined value; anything above is invalid in a file.</summary>
    public const CellType MaxValue = CellType.Finish;

    /// <summary>True for cells the car's body may not overlap: Wall and Danger (PLAN §3.2).</summary>
    public static bool IsBlocking(CellType type) => type == CellType.Wall || type == CellType.Danger;
}
