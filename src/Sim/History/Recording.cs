using System.Buffers.Binary;
using System.IO.Compression;
using Nitrogenesis.Sim.Brain;

namespace Nitrogenesis.Sim.History;

/// <summary>
/// A car's trajectory, one sample per tick (PLAN §2.1, "Recordings"): position quantized to 1/256 cell and the
/// heading in angle units (65536 per turn). Ghosts and "replay best" play these, so they keep working even when
/// the physics changes. Encoded as deltas + raw deflate: a few KB for a typical 25 s run.
/// </summary>
/// <remarks>
/// <para>Binary layout: magic "NGRC", format version (int32 LE), SimVersion (int32 LE), sample count (int32 LE),
/// then raw deflate (RFC 1951) of, per sample, three zigzag LEB128 varints: Δx, Δy (in 1/256 cell) and Δheading
/// (wrapped to −32768…32767). The first sample's deltas are from (0, 0, 0).</para>
/// <para><see cref="Add"/> does not allocate while the count stays within the capacity given at construction.</para>
/// </remarks>
public sealed class Recording
{
    public const int FormatVersion = 1;
    /// <summary>Position steps per cell.</summary>
    public const int PositionScale = 256;
    /// <summary>Largest sample count accepted when decoding (an hour at 60 Hz, with room to spare).</summary>
    public const int MaxSamples = 1 << 22;

    private static ReadOnlySpan<byte> Magic => "NGRC"u8;

    private int[] _x, _y;
    private ushort[] _heading;

    public Recording(int capacity = 0, int simVersion = SimInfo.SimVersion)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _x = new int[capacity];
        _y = new int[capacity];
        _heading = new ushort[capacity];
        SimVersion = simVersion;
    }

    /// <summary>SimVersion of the physics that produced the trajectory.</summary>
    public int SimVersion { get; }

    public int Count { get; private set; }

    /// <summary>Appends a sample; positions are rounded to the nearest 1/256 cell.</summary>
    public void Add(float x, float y, int heading)
    {
        if (Count == _x.Length) Grow();
        _x[Count] = Quantize(x);
        _y[Count] = Quantize(y);
        _heading[Count] = (ushort)heading;
        Count++;
    }

    public void Clear() => Count = 0;

    /// <summary>Sample position in cells (as quantized).</summary>
    public float X(int i) => _x[CheckIndex(i)] * (1f / PositionScale);
    public float Y(int i) => _y[CheckIndex(i)] * (1f / PositionScale);
    /// <summary>Heading in angle units, [0, 65536).</summary>
    public int Heading(int i) => _heading[CheckIndex(i)];
    /// <summary>Heading in radians, [0, 2π).</summary>
    public float HeadingRadians(int i) => Heading(i) * FastMath.RadiansPerUnit;

    public byte[] Encode()
    {
        using var output = new MemoryStream();
        Span<byte> header = stackalloc byte[16];
        Magic.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], FormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], SimVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], Count);
        output.Write(header);
        using (var deflate = new DeflateStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            int px = 0, py = 0, ph = 0;
            Span<byte> buf = stackalloc byte[15];
            for (int i = 0; i < Count; i++)
            {
                int n = WriteVarint(buf, _x[i] - px);
                n += WriteVarint(buf[n..], _y[i] - py);
                n += WriteVarint(buf[n..], (short)(_heading[i] - ph));
                deflate.Write(buf[..n]);
                px = _x[i];
                py = _y[i];
                ph = _heading[i];
            }
        }
        return output.ToArray();
    }

    /// <summary>Decodes <see cref="Encode"/> output. Throws <see cref="InvalidDataException"/> on any problem.</summary>
    public static Recording Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 16 || !data[..4].SequenceEqual(Magic)) throw new InvalidDataException("Not a recording.");
        int version = BinaryPrimitives.ReadInt32LittleEndian(data[4..]);
        if (version < 1 || version > FormatVersion)
            throw new InvalidDataException($"Recording format version {version} is not supported (this app reads up to {FormatVersion}).");
        int simVersion = BinaryPrimitives.ReadInt32LittleEndian(data[8..]);
        int count = BinaryPrimitives.ReadInt32LittleEndian(data[12..]);
        if (count < 0 || count > MaxSamples) throw new InvalidDataException($"Recording sample count {count} is out of range.");

        var rec = new Recording(count, simVersion);
        try
        {
            using var deflate = new DeflateStream(new MemoryStream(data[16..].ToArray()), CompressionMode.Decompress);
            int x = 0, y = 0, h = 0;
            for (int i = 0; i < count; i++)
            {
                x += ReadVarint(deflate);
                y += ReadVarint(deflate);
                h = (h + ReadVarint(deflate)) & FastMath.AngleUnitsMask;
                rec._x[i] = x;
                rec._y[i] = y;
                rec._heading[i] = (ushort)h;
            }
            if (deflate.ReadByte() != -1) throw new InvalidDataException("Recording has data after the last sample.");
        }
        catch (InvalidDataException e) when (!e.Message.StartsWith("Recording", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Recording payload is not valid deflate data.", e);
        }
        rec.Count = count;
        return rec;
    }

    private static int Quantize(float v) => (int)MathF.Round(v * PositionScale);

    private void Grow()
    {
        int size = Math.Max(16, _x.Length * 2);
        Array.Resize(ref _x, size);
        Array.Resize(ref _y, size);
        Array.Resize(ref _heading, size);
    }

    private int CheckIndex(int i) => (uint)i < (uint)Count ? i : throw new ArgumentOutOfRangeException(nameof(i));

    /// <summary>Zigzag + LEB128 (7 bits per byte, low first); at most 5 bytes.</summary>
    private static int WriteVarint(Span<byte> dest, int value)
    {
        uint v = (uint)((value << 1) ^ (value >> 31));
        int n = 0;
        while (v >= 0x80)
        {
            dest[n++] = (byte)(v | 0x80);
            v >>= 7;
        }
        dest[n++] = (byte)v;
        return n;
    }

    private static int ReadVarint(Stream s)
    {
        uint v = 0;
        for (int shift = 0; shift < 35; shift += 7)
        {
            int b = s.ReadByte();
            if (b < 0) throw new InvalidDataException("Recording ends early.");
            v |= (uint)(b & 0x7F) << shift;
            if (b < 0x80) return (int)(v >> 1) ^ -(int)(v & 1);
        }
        throw new InvalidDataException("Recording has a malformed number.");
    }
}
