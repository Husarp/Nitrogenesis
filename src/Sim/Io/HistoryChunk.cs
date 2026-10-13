using System.Buffers.Binary;
using System.Text;
using Nitrogenesis.Sim.Brain;
using Nitrogenesis.Sim.Evolution;
using Nitrogenesis.Sim.History;

namespace Nitrogenesis.Sim.Io;

/// <summary>
/// Binary codec of a history chunk file <c>history/&lt;branch&gt;/gen-&lt;from&gt;-&lt;to&gt;.bin</c> (PLAN §5.1): consecutive
/// <see cref="HistoryEntry"/>s. All numbers little-endian.
/// </summary>
/// <remarks>
/// <para>Layout (format 2): magic "NGHC", format version (int32), SimVersion (int32), first generation (int32), record
/// count (int32), then per record:</para>
/// <list type="bullet">
/// <item>generation (int32), seed (uint64);</item>
/// <item>params: elitism (f32), tournament size (int32), crossover rate (f32), mutation chance (f32), σ (f32),
/// random fraction (f32), boost (byte 0/1);</item>
/// <item>stats: best score (f32), average score (f32), best index (int32), finished count (int32), best finish
/// ticks (f32, +∞ = none), best progress (f32);</item>
/// <item>ticks the generation ran (int32; format 2);</item>
/// <item>brain shape: inputs, hidden 1, hidden 2, outputs (int32 each); best genome (WeightCount × f32);</item>
/// <item>best recording: byte length (int32, 0 = none) + <see cref="Recording.Encode"/> bytes;</item>
/// <item>population snapshot: genome count (int32, 0 = thinned) + count × WeightCount × f32;</item>
/// </list>
/// <para>and finally a CRC-32 of everything before it (uint32; format 2), so a damaged file is detected instead of
/// read as wrong numbers. Format 1 (M1: no ticks, no CRC) is still read.</para>
/// </remarks>
public static class HistoryChunk
{
    public const int FormatVersion = 2;
    private static ReadOnlySpan<byte> Magic => "NGHC"u8;

    public static byte[] Encode(IReadOnlyList<HistoryEntry> entries, int simVersion = SimInfo.SimVersion)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0) throw new ArgumentException("A chunk holds at least one record.", nameof(entries));
        using var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(Magic);
            w.Write(FormatVersion);
            w.Write(simVersion);
            w.Write(entries[0].Generation);
            w.Write(entries.Count);
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (e.Generation != entries[0].Generation + i) throw new ArgumentException("Records must be consecutive generations.", nameof(entries));
                WriteRecord(w, e);
            }
            w.Flush();
            w.Write(Crc32.Compute(stream.GetBuffer().AsSpan(0, (int)stream.Length)));
        }
        return stream.ToArray();
    }

    /// <summary>Decodes a chunk. Throws <see cref="InvalidDataException"/> on any problem (also a CRC mismatch).</summary>
    public static List<HistoryEntry> Decode(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < 8 || !data.AsSpan(0, 4).SequenceEqual(Magic)) throw new InvalidDataException("Not a history chunk.");
        int version = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4));
        if (version < 1 || version > FormatVersion)
            throw new InvalidDataException($"History chunk format version {version} is not supported (this app reads up to {FormatVersion}).");
        int length = data.Length;
        if (version >= 2)
        {
            if (data.Length < 24) throw new InvalidDataException("History chunk ends early.");
            length -= 4;
            uint stored = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(length));
            if (Crc32.Compute(data.AsSpan(0, length)) != stored) throw new InvalidDataException("History chunk is damaged (checksum mismatch).");
        }
        try
        {
            using var r = new BinaryReader(new MemoryStream(data, 0, length, writable: false));
            r.ReadBytes(8); // magic, version
            r.ReadInt32(); // SimVersion of the writer; the recordings carry their own, the session header decides compatibility
            int first = r.ReadInt32(), count = r.ReadInt32();
            if (count < 1 || count > 1_000_000) throw new InvalidDataException($"History chunk record count {count} is out of range.");
            var entries = new List<HistoryEntry>(count);
            for (int i = 0; i < count; i++)
            {
                var entry = ReadRecord(r, version);
                if (entry.Generation != first + i) throw new InvalidDataException("History chunk generations are not consecutive.");
                entries.Add(entry);
            }
            if (r.BaseStream.Position != length) throw new InvalidDataException("History chunk has data after the last record.");
            return entries;
        }
        catch (EndOfStreamException e)
        {
            throw new InvalidDataException("History chunk ends early.", e);
        }
        catch (ArgumentException e)
        {
            throw new InvalidDataException("History chunk holds an invalid value: " + e.Message, e);
        }
    }

    private static void WriteRecord(BinaryWriter w, HistoryEntry e)
    {
        w.Write(e.Generation);
        w.Write(e.Seed);
        var p = e.Params;
        w.Write(p.ElitismFraction);
        w.Write(p.TournamentSize);
        w.Write(p.CrossoverRate);
        w.Write(p.MutationChance);
        w.Write(p.MutationSigma);
        w.Write(p.RandomFraction);
        w.Write(p.Boost);
        var s = e.Stats;
        w.Write(s.BestScore);
        w.Write(s.AverageScore);
        w.Write(s.BestIndex);
        w.Write(s.FinishedCount);
        w.Write(s.BestFinishTicks);
        w.Write(s.BestProgress);
        w.Write(e.Ticks);
        BrainShape shape = e.BestGenome.Shape;
        w.Write(shape.Inputs);
        w.Write(shape.Hidden1);
        w.Write(shape.Hidden2);
        w.Write(shape.Outputs);
        WriteFloats(w, e.BestGenome.Weights);
        w.Write(e.RecordingBytes.Length);
        w.Write(e.RecordingBytes);
        float[]? snapshot = e.Snapshot;
        if (snapshot is null)
        {
            w.Write(0);
            return;
        }
        if (snapshot.Length % shape.WeightCount != 0) throw new ArgumentException("Population snapshot length does not match the brain shape.");
        w.Write(snapshot.Length / shape.WeightCount);
        WriteFloats(w, snapshot);
    }

    private static HistoryEntry ReadRecord(BinaryReader r, int version)
    {
        int generation = r.ReadInt32();
        ulong seed = r.ReadUInt64();
        var p = new EvolutionParams(r.ReadSingle(), r.ReadInt32(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadBoolean());
        var s = new GenerationStats(r.ReadSingle(), r.ReadSingle(), r.ReadInt32(), r.ReadInt32(), r.ReadSingle(), r.ReadSingle());
        int ticks = version >= 2 ? r.ReadInt32() : -1; // format 1 did not store it: unknown, ran to the end
        if (version >= 2 && ticks < 0) throw new InvalidDataException("History chunk tick count is negative.");
        var shape = new BrainShape(r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
        var best = new Genome(shape, ReadFloats(r, shape.WeightCount));
        int recordingLength = r.ReadInt32();
        if (recordingLength != 0 && (recordingLength < 16 || recordingLength > 64 << 20)) throw new InvalidDataException("History chunk recording length is out of range.");
        byte[] recording = r.ReadBytes(recordingLength);
        if (recording.Length != recordingLength) throw new EndOfStreamException();
        if (recordingLength > 0 && !recording.AsSpan(0, 4).SequenceEqual("NGRC"u8)) throw new InvalidDataException("History chunk holds a damaged recording.");
        int genomes = r.ReadInt32();
        if (genomes < 0 || genomes > 1_000_000) throw new InvalidDataException("History chunk population size is out of range.");
        float[]? snapshot = genomes == 0 ? null : ReadFloats(r, genomes * shape.WeightCount);
        return new HistoryEntry
        {
            Generation = generation,
            Seed = seed,
            Params = p,
            Stats = s,
            Ticks = ticks,
            BestGenome = best,
            RecordingBytes = recording,
            Snapshot = snapshot,
        };
    }

    private static void WriteFloats(BinaryWriter w, float[] values)
    {
        foreach (float v in values) w.Write(v);
    }

    private static float[] ReadFloats(BinaryReader r, int count)
    {
        if (count < 0) throw new InvalidDataException("History chunk holds an impossible array length.");
        if ((long)count * 4 > r.BaseStream.Length - r.BaseStream.Position) throw new EndOfStreamException();
        var values = new float[count];
        for (int i = 0; i < count; i++) values[i] = r.ReadSingle();
        return values;
    }
}
