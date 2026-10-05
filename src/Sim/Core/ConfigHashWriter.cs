using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Nitrogenesis.Sim.Core;

/// <summary>
/// Feeds values into a SHA-256 in a fixed, platform-independent byte layout (all little-endian), for
/// <see cref="SimConfig.Hash"/>. Every value is preceded by its field name, so adding, removing or reordering
/// a field can never produce the same byte stream as an older layout by accident.
/// </summary>
public sealed class ConfigHashWriter : IDisposable
{
    private readonly IncrementalHash _sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    /// <summary>Name, then an int32.</summary>
    public void Int(string name, int value)
    {
        Name(name);
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, value);
        _sha.AppendData(b);
    }

    /// <summary>Name, then the IEEE float32 bit pattern (so −0 and +0, or two NaNs, hash differently).</summary>
    public void Float(string name, float value)
    {
        Name(name);
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(b, value);
        _sha.AppendData(b);
    }

    public void Bool(string name, bool value) => Int(name, value ? 1 : 0);

    /// <summary>Name, then the UTF-8 byte length (int32) and bytes.</summary>
    public void String(string name, string value)
    {
        Name(name);
        Raw(value);
    }

    /// <summary>Lowercase hex of the SHA-256 of everything written so far.</summary>
    public string Finish() => Convert.ToHexStringLower(_sha.GetHashAndReset());

    public void Dispose() => _sha.Dispose();

    private void Name(string name) => Raw(name);

    private void Raw(string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(len, bytes.Length);
        _sha.AppendData(len);
        _sha.AppendData(bytes);
    }
}
