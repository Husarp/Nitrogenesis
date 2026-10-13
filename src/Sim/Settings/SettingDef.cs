namespace Nitrogenesis.Sim.Settings;

/// <summary>When a setting may change (PLAN §6.4); the UI enforces it.</summary>
public enum SettingKind
{
    /// <summary>Applies now (evolution ones at the next generation boundary).</summary>
    Live,
    /// <summary>Applies as a new segment: the UI asks first.</summary>
    NewSegment,
    /// <summary>Only a new training can change it: greyed out while a training is open.</summary>
    NewTraining,
}

public enum SettingType { Bool, Int, Float, Choice }

/// <summary>Where a setting's value lives: app-wide (saved in settings.json) or in the training (a session's own, or the defaults of a new one).</summary>
public enum SettingScope { App, Training }

/// <summary>
/// One setting: key, text, kind, range and default. Every value is a <see cref="double"/> (bool 0/1, choice = index,
/// ints whole), so the UI and the presets treat them alike.
/// </summary>
/// <param name="Key">The key in settings.json.</param>
/// <param name="Label">Short name.</param>
/// <param name="Section">Group on the screen.</param>
/// <param name="Kind">When it may change.</param>
/// <param name="Scope">App-wide or per training.</param>
/// <param name="Type">Value type.</param>
/// <param name="Default">The default value.</param>
/// <param name="Min">Smallest value (choice: 0).</param>
/// <param name="Max">Largest value (choice: count − 1).</param>
/// <param name="Step">Step of the UI control.</param>
/// <param name="Tooltip">What it does, in plain words.</param>
/// <param name="Choices">The texts of a choice.</param>
/// <param name="Unit">Shown after the value.</param>
/// <param name="ZeroMeans">When set, 0 is allowed below <see cref="Min"/> and shown as this text ("auto", "off").</param>
public sealed record SettingDef(
    string Key, string Label, string Section, SettingKind Kind, SettingScope Scope, SettingType Type,
    double Default, double Min, double Max, double Step, string Tooltip,
    IReadOnlyList<string>? Choices = null, string Unit = "", string? ZeroMeans = null)
{
    /// <summary>Forces <paramref name="value"/> into the setting's range and type; NaN gives the default.</summary>
    public double Clamp(double value)
    {
        if (double.IsNaN(value)) return Default;
        switch (Type)
        {
            case SettingType.Bool:
                return value >= 0.5 ? 1 : 0;
            case SettingType.Choice:
                return Math.Clamp(Math.Round(value), 0, Choices!.Count - 1);
            default:
                if (Type == SettingType.Int) value = Math.Round(value);
                if (ZeroMeans is not null && value < Min) return value < Min / 2 ? 0 : Min;
                return Math.Clamp(value, Min, Max);
        }
    }

    /// <summary>The value as text for the screen.</summary>
    public string Format(double value)
    {
        value = Clamp(value);
        return Type switch
        {
            SettingType.Bool => value >= 0.5 ? "on" : "off",
            SettingType.Choice => Choices![(int)value],
            _ when ZeroMeans is not null && value == 0 => ZeroMeans,
            SettingType.Int => ((long)value).ToString(System.Globalization.CultureInfo.InvariantCulture) + (Unit.Length > 0 ? " " + Unit : ""),
            _ => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + (Unit.Length > 0 ? " " + Unit : ""),
        };
    }
}
