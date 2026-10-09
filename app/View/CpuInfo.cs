using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Nitrogenesis.View;

/// <summary>Physical core count, for the default simulation thread count with visuals (PLAN §2.1: physical cores − 1).</summary>
public static class CpuInfo
{
    /// <summary>Physical cores of this machine; the logical processor count when it cannot be found.</summary>
    public static int PhysicalCores()
    {
        try
        {
            int cores = OperatingSystem.IsWindows() ? WindowsCores() : OperatingSystem.IsLinux() ? LinuxCores() : 0;
            if (cores > 0) return Math.Min(cores, Environment.ProcessorCount);
        }
        catch (Exception)
        {
            // fall through
        }
        return Environment.ProcessorCount;
    }

    /// <summary>Simulation threads with visuals on: physical cores − 1, at least 1.</summary>
    public static int VisualThreads() => Math.Max(1, PhysicalCores() - 1);

    private static int LinuxCores()
    {
        var seen = new HashSet<(string, string)>();
        foreach (string cpu in Directory.GetDirectories("/sys/devices/system/cpu", "cpu*"))
        {
            string topology = Path.Combine(cpu, "topology");
            string core = Path.Combine(topology, "core_id"), package = Path.Combine(topology, "physical_package_id");
            if (File.Exists(core) && File.Exists(package))
                seen.Add((File.ReadAllText(package).Trim(), File.ReadAllText(core).Trim()));
        }
        return seen.Count;
    }

    private const int RelationProcessorCore = 0;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint length);

    private static int WindowsCores()
    {
        uint length = 0;
        GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref length);
        if (length == 0) return 0;
        IntPtr buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buffer, ref length)) return 0;
            int count = 0;
            for (uint offset = 0; offset < length;)
            {
                // SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX: Relationship (int32), Size (uint32), ...
                int relationship = Marshal.ReadInt32(buffer, (int)offset);
                uint size = (uint)Marshal.ReadInt32(buffer, (int)offset + 4);
                if (size == 0) break;
                if (relationship == RelationProcessorCore) count++;
                offset += size;
            }
            return count;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
