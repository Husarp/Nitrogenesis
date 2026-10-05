using System.Text.Json.Nodes;
using Nitrogenesis.Sim.Io;
using Nitrogenesis.Sim.Map;

public class TrackFileTests
{
    private static Track Sample()
    {
        var grid = new Grid(40, 20);
        new TrackBuilder(grid)
            .Polyline([(4, 10), (36, 10)], 3, CellType.Grass)
            .Polyline([(4, 10), (36, 10)], 2, CellType.Road)
            .Rect(32, 7, 38, 13, CellType.Finish, onlyOver: CellType.Road)
            .Circle(20, 4, 1.5, CellType.Danger);
        var meta = new TrackMeta("Sprint", new JsonObject { ["width"] = 6, ["curvy"] = true }, 18446744073709551615UL);
        return new Track("Sample track ✓", grid, new TrackStart(8.25f, 10.5f, -15f), meta);
    }

    [Fact]
    public void RoundTripPreservesEverything()
    {
        var a = Sample();
        var b = TrackFile.Deserialize(TrackFile.Serialize(a));
        Assert.Equal(a.Name, b.Name);
        Assert.Equal(a.Grid.Width, b.Grid.Width);
        Assert.Equal(a.Grid.Height, b.Grid.Height);
        Assert.Equal(a.Grid.Cells, b.Grid.Cells);
        Assert.Equal(a.Start, b.Start);
        Assert.Equal(a.Meta.Template, b.Meta.Template);
        Assert.Equal(a.Meta.Seed, b.Meta.Seed);
        Assert.True(JsonNode.DeepEquals(a.Meta.Params, b.Meta.Params));
        Assert.Equal(TrackHash.Compute(a), TrackHash.Compute(b));
    }

    [Fact]
    public void FileRoundTripAndEmptyMeta()
    {
        var a = new Track("plain", new Grid(3, 2), new TrackStart(1, 1, 0));
        string path = Path.Combine(Path.GetTempPath(), $"nitro-{Guid.NewGuid():N}.track");
        try
        {
            TrackFile.Save(a, path);
            var b = TrackFile.Load(path);
            Assert.Equal(a.Grid.Cells, b.Grid.Cells);
            Assert.Null(b.Meta.Template);
            Assert.Null(b.Meta.Params);
            Assert.Null(b.Meta.Seed);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void JsonHasTheDocumentedShape()
    {
        var root = JsonNode.Parse(TrackFile.Serialize(Sample()))!.AsObject();
        Assert.Equal(1, (int)root["formatVersion"]!);
        Assert.Equal("Sample track ✓", (string)root["name"]!);
        Assert.Equal(40, (int)root["width"]!);
        Assert.Equal(20, (int)root["height"]!);
        Assert.Equal(8.25f, (float)root["start"]!["x"]!);
        Assert.Equal(10.5f, (float)root["start"]!["y"]!);
        Assert.Equal(-15f, (float)root["start"]!["angleDeg"]!);
        Assert.Equal("Sprint", (string)root["meta"]!["template"]!);
        Assert.Equal(18446744073709551615UL, (ulong)root["meta"]!["seed"]!);

        // cells = base64(raw deflate(cells))
        byte[] compressed = Convert.FromBase64String((string)root["cells"]!);
        using var inflate = new System.IO.Compression.DeflateStream(new MemoryStream(compressed), System.IO.Compression.CompressionMode.Decompress);
        using var raw = new MemoryStream();
        inflate.CopyTo(raw);
        Assert.Equal(Sample().Grid.Cells, raw.ToArray());
    }

    [Fact]
    public void UnknownFieldsAreIgnored()
    {
        var root = JsonNode.Parse(TrackFile.Serialize(Sample()))!.AsObject();
        root["futureField"] = 42;
        Assert.Equal("Sample track ✓", TrackFile.Deserialize(root.ToJsonString()).Name);
    }

    [Theory]
    [InlineData("formatVersion", "2")]
    [InlineData("formatVersion", "0")]
    [InlineData("width", "41")]          // cells no longer match width × height
    [InlineData("height", "0")]
    [InlineData("cells", "\"!!notbase64\"")]
    [InlineData("cells", "\"AAAA\"")]    // valid base64, not valid deflate of 800 bytes
    [InlineData("name", "5")]
    [InlineData("start", "null")]
    public void InvalidFilesAreRejected(string field, string json)
    {
        var root = JsonNode.Parse(TrackFile.Serialize(Sample()))!.AsObject();
        root[field] = JsonNode.Parse(json);
        Assert.Throws<InvalidDataException>(() => TrackFile.Deserialize(root.ToJsonString()));
    }

    [Fact]
    public void InvalidCellValuesAndGarbageAreRejected()
    {
        // A valid 2×1 track whose cells are replaced by deflate data holding an undefined cell type (200).
        var root = JsonNode.Parse(TrackFile.Serialize(new Track("bad", new Grid(2, 1), new TrackStart(0, 0, 0))))!.AsObject();
        using var ms = new MemoryStream();
        using (var d = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionLevel.Optimal, true))
            d.Write([0, 200]);
        root["cells"] = Convert.ToBase64String(ms.ToArray());
        Assert.Throws<InvalidDataException>(() => TrackFile.Deserialize(root.ToJsonString()));
        Assert.Throws<InvalidDataException>(() => TrackFile.Deserialize("not json"));
        Assert.Throws<InvalidDataException>(() => TrackFile.Deserialize("[1,2]"));
    }
}
