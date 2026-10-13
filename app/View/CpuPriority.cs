using System;
using System.Diagnostics;
using Godot;

namespace Nitrogenesis.View;

/// <summary>
/// "Process priority" (PLAN §4): Below normal while training without visuals (the default there), so the rest of the
/// laptop stays responsive; back to what it was afterwards. Windows only: on Linux a process may lower its priority
/// but not raise it again, so it is left alone there.
/// </summary>
public static class CpuPriority
{
    private static ProcessPriorityClass? s_saved;

    /// <summary>Why <see cref="Lower"/> did nothing (for the progress screen).</summary>
    public static string Why => OperatingSystem.IsWindows() ? "not allowed" : "not on Windows";

    /// <summary>Sets Below normal and remembers the old class. Returns false when it could not.</summary>
    public static bool Lower()
    {
        if (!OperatingSystem.IsWindows())
        {
            GD.Print("process priority: left alone (not Windows; a process cannot raise its priority back there)");
            return false;
        }
        try
        {
            using var process = Process.GetCurrentProcess();
            var before = process.PriorityClass;
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
            s_saved ??= before; // remembered only once the change worked; a second Lower keeps the first original
            GD.Print($"process priority: {before} -> BelowNormal");
            return true;
        }
        catch (Exception e)
        {
            GD.Print("process priority: could not lower (" + e.Message + ")");
            return false;
        }
    }

    /// <summary>Puts back the class <see cref="Lower"/> replaced (nothing when it changed nothing).</summary>
    public static void Restore()
    {
        if (s_saved is not { } saved) return;
        try
        {
            using var process = Process.GetCurrentProcess();
            process.PriorityClass = saved;
            s_saved = null; // kept on failure, so a later Restore tries again
            GD.Print($"process priority: restored to {saved}");
        }
        catch (Exception e)
        {
            // The class stays below normal until the app closes (or the next Restore works).
            GD.Print("process priority: could not restore (" + e.Message + ")");
        }
    }

    /// <summary>The current class, for the debug log ("?" when it cannot be read).</summary>
    public static string Current()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return process.PriorityClass.ToString();
        }
        catch (Exception)
        {
            return "?";
        }
    }
}
