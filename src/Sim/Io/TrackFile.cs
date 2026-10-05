using System.IO.Compression;
using System.Text.Json.Nodes;
using Nitrogenesis.Sim.Map;

namespace Nitrogenesis.Sim.Io;

/// <summary>
/// The <c>.track</c> file (PLAN §5.1), JSON:
/// <c>{formatVersion:1, name, width, height, start:{x,y,angleDeg}, cells: base64(deflate(byte[] cells)), meta:{template?, params?, seed?}}</c>.
/// </summary>
/// <remarks>
/// <c>cells</c> is raw deflate (RFC 1951) of the row-major cell bytes. Compressed bytes may differ between
/// zlib versions; that is harmless because <see cref="TrackHash"/> covers the raw cells, not the file.
/// Unknown fields are ignored on load; a newer <c>formatVersion</c> is rejected.
/// </remarks>
public static class TrackFile
{
    public const int FormatVersion = 1;
    public const string Extension = ".track";

    public static void Save(Track track, string path) => File.WriteAllText(path, Serialize(track));

    public static Track Load(string path) => Deserialize(File.ReadAllText(path));

    public static string Serialize(Track track)
    {
        var meta = new JsonObject();
        if (track.Meta.Template is { } template) meta["template"] = template;
        if (track.Meta.Params is { } parameters) meta["params"] = parameters.DeepClone();
        if (track.Meta.Seed is { } seed) meta["seed"] = seed;

        var root = new JsonObject
        {
            ["formatVersion"] = FormatVersion,
            ["name"] = track.Name,
            ["width"] = track.Grid.Width,
            ["height"] = track.Grid.Height,
            ["start"] = new JsonObject
            {
                ["x"] = track.Start.X,
                ["y"] = track.Start.Y,
                ["angleDeg"] = track.Start.AngleDeg,
            },
            ["cells"] = Convert.ToBase64String(Compress(track.Grid.Cells)),
            ["meta"] = meta,
        };
        return root.ToJsonString(JsonFields.Indented);
    }

    /// <summary>Parses and validates a track. Throws <see cref="InvalidDataException"/> on any problem.</summary>
    public static Track Deserialize(string json)
    {
        const string what = "Track file";
        JsonObject root = JsonFields.ParseObject(json, what);

        int version = JsonFields.Required<int>(root, "formatVersion", what);
        if (version < 1 || version > FormatVersion)
            throw new InvalidDataException($"{what}: format version {version} is not supported (this app reads up to {FormatVersion}).");

        string name = JsonFields.Required<string>(root, "name", what);
        int width = JsonFields.Required<int>(root, "width", what);
        int height = JsonFields.Required<int>(root, "height", what);
        if (width < 1 || height < 1 || width > Grid.MaxSide || height > Grid.MaxSide)
            throw new InvalidDataException($"{what}: size {width}×{height} is out of range.");

        if (root["start"] is not JsonObject start) throw new InvalidDataException($"{what}: field 'start' is missing.");
        var trackStart = new TrackStart(
            JsonFields.Required<float>(start, "x", what),
            JsonFields.Required<float>(start, "y", what),
            JsonFields.Required<float>(start, "angleDeg", what));
        if (!float.IsFinite(trackStart.X) || !float.IsFinite(trackStart.Y) || !float.IsFinite(trackStart.AngleDeg))
            throw new InvalidDataException($"{what}: start must be finite numbers.");

        byte[] compressed;
        try
        {
            compressed = Convert.FromBase64String(JsonFields.Required<string>(root, "cells", what));
        }
        catch (FormatException e)
        {
            throw new InvalidDataException($"{what}: 'cells' is not valid base64.", e);
        }
        byte[] cells = Decompress(compressed, width * height, what);
        foreach (byte b in cells)
            if (b > (byte)CellTypes.MaxValue) throw new InvalidDataException($"{what}: unknown cell type {b}.");

        TrackMeta meta = new();
        if (root["meta"] is JsonObject m)
        {
            JsonNode? parameters = m["params"];
            if (parameters is not null and not JsonObject) throw new InvalidDataException($"{what}: 'meta.params' must be an object.");
            meta = new TrackMeta(
                JsonFields.Optional<string>(m, "template", what),
                (JsonObject?)parameters?.DeepClone(),
                JsonFields.Optional<ulong?>(m, "seed", what));
        }

        return new Track(name, new Grid(width, height, cells), trackStart, meta);
    }

    private static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            deflate.Write(data);
        return output.ToArray();
    }

    /// <summary>Inflates exactly <paramref name="expectedLength"/> bytes; more or fewer is an error (also guards against deflate bombs).</summary>
    private static byte[] Decompress(byte[] data, int expectedLength, string what)
    {
        var result = new byte[expectedLength];
        try
        {
            using var deflate = new DeflateStream(new MemoryStream(data), CompressionMode.Decompress);
            int read = 0;
            while (read < expectedLength)
            {
                int n = deflate.Read(result, read, expectedLength - read);
                if (n == 0) break;
                read += n;
            }
            if (read != expectedLength || deflate.ReadByte() != -1)
                throw new InvalidDataException($"{what}: 'cells' does not hold exactly width × height = {expectedLength} cells.");
        }
        catch (InvalidDataException e) when (!e.Message.StartsWith(what, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{what}: 'cells' is not valid deflate data.", e);
        }
        return result;
    }
}
