namespace Nitrogenesis.Sim.Settings;

/// <summary>Low / High graphics (PLAN §6.4): what the training view draws.</summary>
/// <param name="Trails">The followed car's route trail.</param>
/// <param name="Rays">The followed car's sensor rays.</param>
/// <param name="AntiAliasedLines">Anti-aliased route and ray lines.</param>
/// <param name="MaxCarsDrawn">Most cars drawn (the best and the selected car always are).</param>
public readonly record struct GraphicsQuality(bool Trails, bool Rays, bool AntiAliasedLines, int MaxCarsDrawn)
{
    public static readonly IReadOnlyList<string> Labels = ["Low", "High"];
    public const int DefaultLevel = 1;

    public static GraphicsQuality Low => new(false, false, false, 200);
    public static GraphicsQuality High => new(true, true, true, 1000);

    public static GraphicsQuality For(int level) => level <= 0 ? Low : High;
}
