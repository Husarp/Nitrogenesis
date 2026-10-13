using Godot;
using Nitrogenesis.Sim.Settings;

namespace Nitrogenesis.Ui;

/// <summary>
/// The FPS cap and VSync (PLAN §4): the chosen cap, 15 FPS while the window is not focused, 10 FPS on the progress screen of
/// "train without visuals". <see cref="Apply"/> sets <c>Engine.MaxFps</c> and the VSync mode from the settings.
/// </summary>
public static class FrameRate
{
    private static bool s_focused = true;
    private static int s_override;

    /// <summary>A temporary cap (the progress screen's 10); 0 = none.</summary>
    public static int Override
    {
        get => s_override;
        set
        {
            s_override = value;
            Apply();
        }
    }

    /// <summary>Debug (<c>--monitor-hz</c>): pretends the monitor has this refresh rate (a virtual screen reports none).</summary>
    public static double HzOverride { get; set; }

    /// <summary>The refresh rate of the monitor the window is on, Hz; 0 when it is not known.</summary>
    public static double MonitorHz
    {
        get
        {
            if (HzOverride > 0) return HzOverride;
            float hz = DisplayServer.ScreenGetRefreshRate();
            return hz > 0 ? hz : 0;
        }
    }

    /// <summary>The window gained or lost the focus.</summary>
    public static void SetFocused(bool focused)
    {
        s_focused = focused;
        Apply();
    }

    /// <summary>The cap in force now: the lowest of the chosen cap (0 = unlimited), the progress screen's and the unfocused one.</summary>
    public static int Current()
    {
        var settings = AppRuntime.Settings;
        int fps = FpsCap.MaxFps(settings.FpsCapIndex, MonitorHz);
        if (s_override > 0) fps = fps == 0 ? s_override : System.Math.Min(fps, s_override);
        if (!s_focused) fps = fps == 0 ? FpsCap.UnfocusedFps : System.Math.Min(fps, FpsCap.UnfocusedFps);
        return fps;
    }

    public static void Apply()
    {
        Engine.MaxFps = Current();
        // Only on a change: this also runs at every focus change, and a driver may rebuild its swap chain on each call.
        var vsync = AppRuntime.Settings.VSync ? DisplayServer.VSyncMode.Enabled : DisplayServer.VSyncMode.Disabled;
        if (DisplayServer.WindowGetVsyncMode() != vsync) DisplayServer.WindowSetVsyncMode(vsync);
    }
}
