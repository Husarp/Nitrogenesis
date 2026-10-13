using System.Globalization;

namespace Nitrogenesis.Sim.Settings;

/// <summary>The FPS cap choices (PLAN §4) and the hint for a cap that does not suit the monitor.</summary>
public static class FpsCap
{
    public static readonly IReadOnlyList<string> Labels = ["24", "30", "45", "60", "75", "120", "Unlimited", "Match monitor"];
    private static readonly int[] Fixed = [24, 30, 45, 60, 75, 120];

    public const int DefaultIndex = 3;
    public const int UnlimitedIndex = 6;
    public const int MatchMonitorIndex = 7;
    /// <summary>An unfocused window draws this often (PLAN §4).</summary>
    public const int UnfocusedFps = 15;
    /// <summary>The progress screen of "train without visuals" (PLAN §4).</summary>
    public const int ProgressFps = 10;

    /// <summary>Godot's <c>Engine.MaxFps</c> for a choice: 0 = unlimited; "match monitor" is the refresh rate rounded (60 when unknown).</summary>
    public static int MaxFps(int index, double monitorHz) => index switch
    {
        >= 0 and < 6 => Fixed[index],
        UnlimitedIndex => 0,
        _ => monitorHz >= 1 ? (int)Math.Round(monitorHz) : 60,
    };

    /// <summary>
    /// "75 Hz monitor: choose 75 or turn VSync off for smooth motion", or null. It appears when VSync is on and the cap
    /// is below the refresh rate without dividing it (frames then show for uneven times). A cap at or above the refresh
    /// rate is paced by VSync alone; "match monitor" and "unlimited" never need the hint.
    /// </summary>
    public static string? Hint(int index, double monitorHz, bool vsync)
    {
        if (!vsync || index < 0 || index >= Fixed.Length || monitorHz < 1) return null;
        int hz = (int)Math.Round(monitorHz), cap = Fixed[index];
        if (cap >= hz || hz % cap == 0) return null;
        return string.Create(CultureInfo.InvariantCulture, $"{hz} Hz monitor: choose {hz} (or Match monitor) or turn VSync off for smooth motion");
    }
}
