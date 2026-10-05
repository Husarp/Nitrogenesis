using System.Text.Json.Nodes;
using Nitrogenesis.Sim.Io;

public class SettingsFileTests
{
    [Fact]
    public void MissingKeysAndWrongTypesFallBackToDefaults()
    {
        var s = SettingsFile.Deserialize("""{"formatVersion":1,"values":{"threads":"many","fpsCap":75}}""");
        Assert.Equal(3, s.GetInt("threads", 3));       // wrong type
        Assert.Equal(75, s.GetInt("fpsCap", 60));
        Assert.True(s.GetBool("vsync", true));          // missing
        Assert.Equal("x", s.GetString("fpsCap", "x"));  // a number is not a string
    }

    [Fact]
    public void RoundTripKeepsValuesIncludingUnknownOnes()
    {
        var s = new SettingsFile();
        s.Set("fpsCap", 120);
        s.Set("mutationSigma", 0.2);
        s.Set("vsync", false);
        s.Set("carModel", "wedge");
        var t = SettingsFile.Deserialize(s.Serialize());
        Assert.Equal(120, t.GetInt("fpsCap", 0));
        Assert.Equal(0.2, t.GetDouble("mutationSigma", 0));
        Assert.False(t.GetBool("vsync", true));
        Assert.Equal("wedge", t.GetString("carModel", ""));

        var root = JsonNode.Parse(s.Serialize())!.AsObject();
        Assert.Equal(1, (int)root["formatVersion"]!);
        root["values"]!["fromNewerVersion"] = new JsonArray(1, 2);
        var u = SettingsFile.Deserialize(root.ToJsonString());
        Assert.Contains("fromNewerVersion", SettingsFile.Deserialize(u.Serialize()).Keys);
    }

    [Fact]
    public void RemoveResetsToDefault()
    {
        var s = new SettingsFile();
        s.Set("fpsCap", 30);
        Assert.True(s.Remove("fpsCap"));
        Assert.Equal(60, s.GetInt("fpsCap", 60));
        Assert.False(s.Contains("fpsCap"));
    }

    [Fact]
    public void LoadOfMissingFileGivesDefaultsAndSaveIsReadable()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"nitro-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "settings.json");
            var s = SettingsFile.Load(path);
            Assert.Empty(s.Keys);
            s.Set("fpsCap", 45);
            s.Save(path);
            s.Set("fpsCap", 24);
            s.Save(path); // overwrite
            Assert.Equal(24, SettingsFile.Load(path).GetInt("fpsCap", 60));
            Assert.Single(Directory.GetFiles(dir)); // no temp file left behind
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData("""{"formatVersion":2,"values":{}}""")]
    [InlineData("""{"values":{}}""")]
    [InlineData("""{"formatVersion":1,"values":[1]}""")]
    [InlineData("""garbage""")]
    public void InvalidFilesAreRejected(string json) =>
        Assert.Throws<InvalidDataException>(() => SettingsFile.Deserialize(json));

    [Fact]
    public void MissingValuesObjectMeansAllDefaults() =>
        Assert.Empty(SettingsFile.Deserialize("""{"formatVersion":1}""").Keys);
}
