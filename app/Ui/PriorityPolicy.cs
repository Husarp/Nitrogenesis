using Nitrogenesis.Sim.Settings;
using Nitrogenesis.View;

namespace Nitrogenesis.Ui;

/// <summary>
/// The "Process priority" setting (PLAN §4): Auto = Below normal while training without visuals and Normal otherwise; Normal
/// and Below normal hold their class always. Windows only (see <see cref="CpuPriority"/>).
/// </summary>
public static class PriorityPolicy
{
    private static bool s_wantLower, s_lowered;

    /// <summary>Puts the process in the class the setting wants now. Returns true when it is Below normal afterwards.</summary>
    public static bool Apply(bool headless)
    {
        int choice = AppRuntime.Settings.Priority;
        bool lower = choice == SettingsCatalog.PriorityBelowNormal || choice == SettingsCatalog.PriorityAuto && headless;
        if (lower == s_wantLower) return s_lowered; // nothing changed (every settings change comes through here)
        s_wantLower = lower;
        if (!lower)
        {
            CpuPriority.Restore();
            return s_lowered = false;
        }
        return s_lowered = CpuPriority.Lower();
    }
}
