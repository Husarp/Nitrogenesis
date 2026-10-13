namespace Nitrogenesis.Sim.Settings;

/// <summary>
/// What the machine has, for the limits and defaults of the resource settings (PLAN §2.1, §4): simulation threads
/// (visuals: physical cores − 1; without visuals: logical threads − 1) and the history memory (at most 75 % of the RAM).
/// </summary>
public sealed record MachineInfo(int LogicalCores, int PhysicalCores, long TotalMemoryBytes)
{
    public const int MinHistoryMb = 64;
    public const int DefaultHistoryMb = 1024;
    /// <summary>The share of the RAM the history memory limit may take.</summary>
    public const double MaxMemoryShare = 0.75;

    public int VisualThreads => Math.Max(1, PhysicalCores - 1);
    public int HeadlessThreads => Math.Max(1, LogicalCores - 1);

    /// <summary>Largest history memory limit, MB: 75 % of the RAM (never below <see cref="MinHistoryMb"/>).</summary>
    public int MaxHistoryMb => (int)Math.Clamp((long)(TotalMemoryBytes * MaxMemoryShare / (1024 * 1024)), MinHistoryMb, int.MaxValue);

    /// <summary>This machine; the caller supplies the physical core count (it is platform code).</summary>
    public static MachineInfo Detect(int physicalCores) =>
        new(Math.Max(1, Environment.ProcessorCount), Math.Max(1, physicalCores), GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
}
