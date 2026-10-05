using System.Text;
using Nitrogenesis.Sim.Brain;
using Nitrogenesis.Sim.Evolution;
using Nitrogenesis.Sim.History;

namespace Nitrogenesis.Sim.Io;

/// <summary>
/// Binary codec of a history chunk file <c>history/&lt;branch&gt;/gen-&lt;from&gt;-&lt;to&gt;.bin</c> (PLAN §5.1): consecutive
/// <see cref="GenerationRecord"/>s. All numbers little-endian.
/// </summary>
/// <remarks>
/// <para>Layout: magic "NGHC", format version (int32), SimVersion (int32), first generation (int32), record count
/// (int32), then per record:</para>
/// <list type="bullet">
/// <item>generation (int32), seed (uint64);</item>
/// <item>params: elitism (f32), tournament size (int32), crossover rate (f32), mutation chance (f32), σ (f32),
/// random fraction (f32), boost (byte 0/1);</item>
/// <item>stats: best score (f32), average score (f32), best index (int32), finished count (int32), best finish
/// ticks (f32, +∞ = none), best progress (f32);</item>
/// <item>brain shape: inputs, hidden 1, hidden 2, outputs (int32 each); best genome (WeightCount × f32);</item>
/// <item>best recording: byte length (int32) + <see cref="Recording.Encode"/> bytes;</item>
/// <item>population snapshot: genome count (int32, 0 = thinned) + count × WeightCount × f32.</item>
/// </list>
/// </remarks>
public static class HistoryChunk
{
    public const int FormatVersion = 1;
    private static ReadOnlySpan<byte> Magic => "NGHC"u8;

    public static byte[] Encode(IReadOnlyList<GenerationRecord> records, int simVersion = SimInfo.SimVersion)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0) throw new ArgumentException("A chunk holds at least one record.", nameof(records));
        using var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(Magic);
            w.Write(FormatVersion);
            w.Write(simVersion);
            w.Write(records[0].Generation);
            w.Write(records.Count);
            for (int i = 0; i < records.Count; i++)
            {
                var r = records[i];
                if (r.Generation != records[0].Generation + i) throw new ArgumentException("Records must be consecutive generations.", nameof(records));
                WriteRecord(w, r);
            }
        }
        return stream.ToArray();
    }

    /// <summary>Decodes a chunk. Throws <see cref="InvalidDataException"/> on any problem.</summary>
    public static List<GenerationRecord> Decode(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        try
        {
            using var r = new BinaryReader(new MemoryStream(data, writable: false));
            if (!r.ReadBytes(4).AsSpan().SequenceEqual(Magic)) throw new InvalidDataException("Not a history chunk.");
            int version = r.ReadInt32();
            if (version < 1 || version > FormatVersion)
                throw new InvalidDataException($"History chunk format version {version} is not supported (this app reads up to {FormatVersion}).");
            r.ReadInt32(); // SimVersion of the writer; the recordings carry their own, the session header decides compatibility
            int first = r.ReadInt32(), count = r.ReadInt32();
            if (count < 1 || count > 1_000_000) throw new InvalidDataException($"History chunk record count {count} is out of range.");
            var records = new List<GenerationRecord>(count);
            for (int i = 0; i < count; i++)
            {
                var record = ReadRecord(r);
                if (record.Generation != first + i) throw new InvalidDataException("History chunk generations are not consecutive.");
                records.Add(record);
            }
            if (r.BaseStream.Position != data.Length) throw new InvalidDataException("History chunk has data after the last record.");
            return records;
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

    private static void WriteRecord(BinaryWriter w, GenerationRecord r)
    {
        w.Write(r.Generation);
        w.Write(r.Seed);
        var p = r.Params;
        w.Write(p.ElitismFraction);
        w.Write(p.TournamentSize);
        w.Write(p.CrossoverRate);
        w.Write(p.MutationChance);
        w.Write(p.MutationSigma);
        w.Write(p.RandomFraction);
        w.Write(p.Boost);
        var s = r.Stats;
        w.Write(s.BestScore);
        w.Write(s.AverageScore);
        w.Write(s.BestIndex);
        w.Write(s.FinishedCount);
        w.Write(s.BestFinishTicks);
        w.Write(s.BestProgress);
        BrainShape shape = r.BestGenome.Shape;
        w.Write(shape.Inputs);
        w.Write(shape.Hidden1);
        w.Write(shape.Hidden2);
        w.Write(shape.Outputs);
        WriteFloats(w, r.BestGenome.Weights);
        byte[] recording = r.BestRecording.Encode();
        w.Write(recording.Length);
        w.Write(recording);
        float[]? snapshot = r.PopulationSnapshot;
        if (snapshot is null)
        {
            w.Write(0);
            return;
        }
        if (snapshot.Length % shape.WeightCount != 0) throw new ArgumentException("Population snapshot length does not match the brain shape.");
        w.Write(snapshot.Length / shape.WeightCount);
        WriteFloats(w, snapshot);
    }

    private static GenerationRecord ReadRecord(BinaryReader r)
    {
        int generation = r.ReadInt32();
        ulong seed = r.ReadUInt64();
        var p = new EvolutionParams(r.ReadSingle(), r.ReadInt32(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadBoolean());
        var s = new GenerationStats(r.ReadSingle(), r.ReadSingle(), r.ReadInt32(), r.ReadInt32(), r.ReadSingle(), r.ReadSingle());
        var shape = new BrainShape(r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
        var best = new Genome(shape, ReadFloats(r, shape.WeightCount));
        int recordingLength = r.ReadInt32();
        if (recordingLength < 16 || recordingLength > 64 << 20) throw new InvalidDataException("History chunk recording length is out of range.");
        var recording = Recording.Decode(r.ReadBytes(recordingLength));
        int genomes = r.ReadInt32();
        if (genomes < 0 || genomes > 1_000_000) throw new InvalidDataException("History chunk population size is out of range.");
        float[]? snapshot = genomes == 0 ? null : ReadFloats(r, genomes * shape.WeightCount);
        return new GenerationRecord
        {
            Generation = generation,
            Seed = seed,
            Params = p,
            Stats = s,
            BestGenome = best,
            BestRecording = recording,
            PopulationSnapshot = snapshot,
        };
    }

    private static void WriteFloats(BinaryWriter w, float[] values)
    {
        foreach (float v in values) w.Write(v);
    }

    private static float[] ReadFloats(BinaryReader r, int count)
    {
        var values = new float[count];
        for (int i = 0; i < count; i++) values[i] = r.ReadSingle();
        return values;
    }
}
