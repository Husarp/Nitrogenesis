namespace Nitrogenesis.Sim.Settings;

/// <summary>The presets of PLAN §6.4. Each one sets every training-scope setting (except the speed) to its own value or the default.</summary>
public enum SettingsPreset { Easy, Default, Chaos }

public static class SettingsPresets
{
    /// <summary>
    /// The values a preset changes from the defaults. Easy: easier learning (the direction hint, more and better-informed cars,
    /// gentler mutation, a more forgiving grass). Chaos: wild evolution and harder rules (strong and frequent mutation, many
    /// fresh cars, few elites, walls that kill, slippery grass).
    /// </summary>
    public static IReadOnlyDictionary<string, double> Overrides(SettingsPreset preset) => preset switch
    {
        SettingsPreset.Easy => new Dictionary<string, double>
        {
            ["directionHint"] = 1, ["populationSize"] = 300, ["rayCount"] = 9, ["elitismPercent"] = 10, ["mutationSigma"] = 0.15,
            ["mutationChancePercent"] = 8, ["randomPercent"] = 1, ["grassSpeedPercent"] = 60, ["stallSeconds"] = 4,
        },
        SettingsPreset.Chaos => new Dictionary<string, double>
        {
            ["mutationSigma"] = 0.5, ["mutationChancePercent"] = 30, ["randomPercent"] = 10, ["elitismPercent"] = 2,
            ["tournamentSize"] = 2, ["crossoverPercent"] = 80, ["populationSize"] = 100, ["walls"] = 2,
            ["grassSpeedPercent"] = 30, ["grassGrip"] = 0.3, ["stallSeconds"] = 2,
        },
        _ => new Dictionary<string, double>(),
    };
}
