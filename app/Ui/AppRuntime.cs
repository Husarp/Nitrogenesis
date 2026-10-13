using System;
using Godot;
using Nitrogenesis.Sim.Io;
using Nitrogenesis.Sim.Settings;
using Nitrogenesis.View;

namespace Nitrogenesis.Ui;

/// <summary>
/// The app-wide settings (PLAN §6.4): loaded once at start from <c>settings.json</c> (<see cref="AppPaths.Settings"/>), saved
/// after every change. <see cref="Apply"/> is the one place an app-level change reaches the engine (FPS cap, VSync, process
/// priority, sound volume); views listen to <see cref="Changed"/> for the rest (threads, history memory, graphics).
/// </summary>
public static class AppRuntime
{
    private static AppSettings? s_settings;

    /// <summary>The settings (loaded on first use).</summary>
    public static AppSettings Settings => s_settings ??= Load();

    /// <summary>Raised after an app-level setting changed and was applied.</summary>
    public static event Action? Changed;

    /// <summary>The machine's cores and memory, which set the limits of the resource settings.</summary>
    public static MachineInfo Machine { get; } = MachineInfo.Detect(CpuInfo.PhysicalCores());

    private static AppSettings Load()
    {
        var settings = AppSettings.Load(AppPaths.Settings, Machine, out string? problem);
        if (problem is not null) GD.PushWarning(problem);
        return settings;
    }

    /// <summary>Applies what the engine itself holds: FPS cap and VSync, the process priority, the UI sound level.</summary>
    public static void Apply()
    {
        FrameRate.Apply();
        PriorityPolicy.Apply(headless: false);
        Sfx.Refresh();
    }

    /// <summary>An app-level setting changed: save, apply, tell the views.</summary>
    public static void NotifyChanged()
    {
        try
        {
            Settings.Save();
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
        {
            GD.PushWarning($"Settings could not be saved: {e.Message}");
        }
        Apply();
        Changed?.Invoke();
    }

    /// <summary>Saves the training defaults after an edit from the menu.</summary>
    public static void Save()
    {
        try
        {
            Settings.Save();
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
        {
            GD.PushWarning($"Settings could not be saved: {e.Message}");
        }
    }
}
