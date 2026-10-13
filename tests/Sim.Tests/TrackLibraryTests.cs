using Nitrogenesis.Sim.Io;
using Nitrogenesis.Sim.Map;

public sealed class TrackLibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"nitro-lib-{Guid.NewGuid():N}", "tracks");
    private readonly TrackLibrary _lib;

    public TrackLibraryTests() => _lib = new TrackLibrary(_root);

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_root)!, true); } catch (IOException) { }
    }

    private static Track Make(byte fill = 1, string name = "x")
    {
        var grid = new Grid(20, 10);
        Array.Fill(grid.Cells, fill);
        return new Track(name, grid, new TrackStart(3.5f, 4.5f, 15f));
    }

    [Theory]
    [InlineData("My track")]
    [InlineData("S_curve copy")]
    [InlineData("a(1) [2], 3+4 & 5! #6's")]
    [InlineData("Zażółć 12")]
    public void GoodNames(string name) => Assert.Null(TrackLibrary.CheckName(name));

    [Theory]
    [InlineData("", "Type a name")]
    [InlineData("   ", "Type a name")]
    [InlineData("a/b", "cannot be used")]
    [InlineData("a:b", "cannot be used")]
    [InlineData("a*b?", "cannot be used")]
    [InlineData("dots.", "end with")]
    [InlineData("CON", "reserved")]
    [InlineData("nul", "reserved")]
    [InlineData("com1", "reserved")]
    [InlineData("LPT9.txt", "reserved")]
    public void BadNames(string name, string reason)
    {
        string? p = TrackLibrary.CheckName(name);
        Assert.NotNull(p);
        Assert.Contains(reason, p);
    }

    [Fact]
    public void TooLongName() => Assert.Contains("too long", TrackLibrary.CheckName(new string('a', TrackLibrary.MaxNameLength + 1)));

    [Fact]
    public void NullNameIsEmpty() => Assert.Contains("Type a name", TrackLibrary.CheckName(null));

    [Fact]
    public void SaveListLoadRoundTrip()
    {
        var t = Make(2);
        _lib.Save(t, "My test");
        var list = _lib.List();
        var one = Assert.Single(list);
        Assert.Equal("My test", one.Name);
        Assert.Null(one.Problem);
        Assert.Equal(20, one.Width);
        var back = TrackLibrary.Load(one.Path);
        Assert.Equal("My test", back.Name);
        Assert.Equal(TrackHash.Compute(t), TrackHash.Compute(back));
        Assert.True(_lib.Exists("my TEST"));
        Assert.False(_lib.Exists("other"));
    }

    [Fact]
    public void ListOnMissingFolderIsEmpty() => Assert.Empty(_lib.List());

    [Fact]
    public void OverwriteKeepsExactlyOneFile()
    {
        _lib.Save(Make(1), "Foo");
        _lib.Save(Make(2), "foo"); // same track on a case-insensitive file system
        var files = Directory.GetFiles(_root);
        Assert.Single(files);
        Assert.Equal(TrackHash.Compute(Make(2)), TrackHash.Compute(TrackLibrary.Load(files[0])));
    }

    [Fact]
    public void FailedSaveLeavesOldFileAndNoTemp()
    {
        string path = _lib.Save(Make(1), "Keep");
        string before = File.ReadAllText(path);
        _lib.FaultBeforeCommit = _ => throw new IOException("disk full");
        Assert.Throws<IOException>(() => _lib.Save(Make(3), "Keep"));
        Assert.Equal(before, File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFiles(_root));
    }

    [Fact]
    public void FailedFirstSaveLeavesNothing()
    {
        _lib.FaultBeforeCommit = _ => throw new IOException("nope");
        Assert.Throws<IOException>(() => _lib.Save(Make(), "New one"));
        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public void DirectoryInTheWayFailsCleanly()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Blocked.track"));
        Assert.ThrowsAny<Exception>(() => _lib.Save(Make(), "Blocked"));
        Assert.Empty(Directory.GetFiles(_root));
        Assert.True(Directory.Exists(Path.Combine(_root, "Blocked.track")));
    }

    [Fact]
    public void BadNameIsRejectedWithoutWriting()
    {
        Assert.Throws<ArgumentException>(() => _lib.Save(Make(), "a/b"));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void DamagedFileIsListedWithAReasonAndDoesNotBreakTheList()
    {
        _lib.Save(Make(), "Good");
        File.WriteAllText(Path.Combine(_root, "Broken.track"), "{ not json");
        File.WriteAllText(Path.Combine(_root, "Empty.track"), "");
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "ignored");
        var list = _lib.List();
        Assert.Equal(3, list.Count);
        Assert.Null(list.Single(i => i.Name == "Good").Problem);
        Assert.NotNull(list.Single(i => i.Name == "Broken").Problem);
        Assert.False(list.Single(i => i.Name == "Empty").CanOpen);
    }

    [Fact]
    public void LeftoverTempFileIsNotListed()
    {
        _lib.Save(Make(), "A");
        File.WriteAllText(Path.Combine(_root, "A.track" + TrackLibrary.TempSuffix), "half");
        Assert.Single(_lib.List());
    }
}
