using Nitrogenesis.Sim.Evolution;

namespace Nitrogenesis.Sim.History;

/// <summary>
/// One generation as the history keeps it (PLAN §5): what a <see cref="GenerationRecord"/> holds, with the best
/// car's recording kept encoded (a few KB instead of 10 bytes per tick) and the full-population snapshot until it
/// is thinned. Immutable: thinning replaces an entry with <see cref="WithoutSnapshot"/>, so a save that captured
/// the entry earlier still sees consistent data.
/// </summary>
public sealed class HistoryEntry
{
    /// <summary>Bookkeeping bytes counted per entry on top of its arrays (object headers, fields).</summary>
    public const int OverheadBytes = 160;

    public required int Generation { get; init; }
    /// <summary>The generation seed its population was bred (or initialised) with.</summary>
    public required ulong Seed { get; init; }
    /// <summary>Evolution parameters that bred its population, boost included.</summary>
    public required EvolutionParams Params { get; init; }
    public required GenerationStats Stats { get; init; }
    /// <summary>Ticks the generation ran before it was scored (see <see cref="GenerationRecord.Ticks"/>); −1 = unknown (an M1 file: it ran to the end).</summary>
    public required int Ticks { get; init; }
    public required Genome BestGenome { get; init; }
    /// <summary>The best car's recording, encoded (<see cref="Recording.Encode"/>); empty when it was not recorded.</summary>
    public required byte[] RecordingBytes { get; init; }
    /// <summary>All genomes of the generation (agent-major), or null once thinned. Never changed in place.</summary>
    public float[]? Snapshot { get; init; }

    /// <summary>Memory the entry holds (arrays plus <see cref="OverheadBytes"/>).</summary>
    public long Bytes => OverheadBytes + RecordingBytes.Length + 4L * BestGenome.Weights.Length + SnapshotBytes;

    /// <summary>Memory of the population snapshot alone (0 when thinned).</summary>
    public long SnapshotBytes => Snapshot is { } s ? 4L * s.Length : 0;

    /// <summary>The best car's recording (decoded on each call; null when the generation was not recorded).</summary>
    public Recording? DecodeRecording() => RecordingBytes.Length == 0 ? null : Recording.Decode(RecordingBytes);

    /// <summary>The same entry without its population snapshot.</summary>
    public HistoryEntry WithoutSnapshot() => Snapshot is null ? this : new HistoryEntry
    {
        Generation = Generation,
        Seed = Seed,
        Params = Params,
        Stats = Stats,
        Ticks = Ticks,
        BestGenome = BestGenome,
        RecordingBytes = RecordingBytes,
        Snapshot = null,
    };

    /// <summary>The entry for a record from the runner (encodes the recording unless the runner already did).</summary>
    public static HistoryEntry From(GenerationRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new HistoryEntry
        {
            Generation = record.Generation,
            Seed = record.Seed,
            Params = record.Params,
            Stats = record.Stats,
            Ticks = record.Ticks,
            BestGenome = record.BestGenome,
            RecordingBytes = record.EncodedRecording ?? (record.BestRecording.Count == 0 ? [] : record.BestRecording.Encode()),
            Snapshot = record.PopulationSnapshot,
        };
    }

    /// <summary>True when both describe the same evaluation: same stats (bit for bit), ticks and best genome.</summary>
    public bool SameResultAs(HistoryEntry other) =>
        Generation == other.Generation && Ticks == other.Ticks && SameStats(Stats, other.Stats) && BestGenome.BitEquals(other.BestGenome);

    /// <summary>Stats compared bit for bit (NaN equals NaN).</summary>
    public static bool SameStats(GenerationStats a, GenerationStats b) =>
        Bits(a.BestScore) == Bits(b.BestScore) && Bits(a.AverageScore) == Bits(b.AverageScore) && a.BestIndex == b.BestIndex
        && a.FinishedCount == b.FinishedCount && Bits(a.BestFinishTicks) == Bits(b.BestFinishTicks) && Bits(a.BestProgress) == Bits(b.BestProgress);

    private static uint Bits(float f) => BitConverter.SingleToUInt32Bits(f);
}
