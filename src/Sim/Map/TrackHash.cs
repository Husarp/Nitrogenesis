using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Nitrogenesis.Sim.Map;

/// <summary>
/// Identity of a track's drivable content (PLAN §5.1): <c>SHA-256(width, height, cells, start)</c>.
/// The name and meta are not included, so renaming a track keeps its ghosts matching.
/// </summary>
/// <remarks>
/// Hashed bytes, in order: width (int32 LE), height (int32 LE), the raw cell bytes (row-major), then
/// start X, start Y, start angle in degrees (each the IEEE float32 bit pattern, LE). Returned as 64
/// lowercase hex digits. Never change this layout: it is stored in sessions and ghost picks.
/// </remarks>
public static class TrackHash
{
    public static string Compute(Track track) => Compute(track.Grid, track.Start);

    public static string Compute(Grid grid, TrackStart start)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> buf = stackalloc byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(buf, grid.Width);
        BinaryPrimitives.WriteInt32LittleEndian(buf[4..], grid.Height);
        sha.AppendData(buf[..8]);
        sha.AppendData(grid.Cells);
        BinaryPrimitives.WriteSingleLittleEndian(buf, start.X);
        BinaryPrimitives.WriteSingleLittleEndian(buf[4..], start.Y);
        BinaryPrimitives.WriteSingleLittleEndian(buf[8..], start.AngleDeg);
        sha.AppendData(buf);
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }
}
