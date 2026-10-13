using Nitrogenesis.Sim.History;
using Nitrogenesis.Sim.Io;

public sealed class SessionStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ng-store-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static void SaveSession(SessionFolder folder, int generations, DateTime savedAt)
    {
        using var d = SessionDriver.New(population: 12);
        d.Steps(generations);
        var plan = d.Session.PrepareSave(savedAt);
        folder.Save(plan);
    }

    [Theory]
    [InlineData("sprint 2026-10-09 18.30", "sprint 2026-10-09 18.30")]
    [InlineData("  Trening łuk #2  ", "Trening łuk #2")]
    [InlineData("a/b\\c:d*e?f\"g<h>i|j", "a_b_c_d_e_f_g_h_i_j")]
    [InlineData("...", "Session")]
    [InlineData("", "Session")]
    [InlineData("name.", "name")]
    [InlineData("CON", "_CON")]
    [InlineData("com1.txt", "_com1.txt")]
    [InlineData("console", "console")]
    public void NamesBecomeSafeFolderNames(string input, string expected) => Assert.Equal(expected, SessionStore.Sanitize(input));

    [Fact]
    public void LongNamesAreCut() => Assert.Equal(SessionStore.MaxNameLength, SessionStore.Sanitize(new string('x', 200)).Length);

    [Fact]
    public void CreateListLatestRenameDelete()
    {
        var store = new SessionStore(_root);
        Assert.Empty(store.List());
        Assert.Null(store.Latest());

        var a = store.Create("sprint");
        SaveSession(a, 3, new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc));
        var b = store.Create("sprint"); // same name: made unique
        Assert.Equal("sprint (2)", b.Name);
        SaveSession(b, 5, new DateTime(2026, 10, 2, 10, 0, 0, DateTimeKind.Utc));
        store.Create("never saved"); // a folder without a header is not a session (yet)
        Directory.CreateDirectory(Path.Combine(_root, "broken"));
        File.WriteAllText(Path.Combine(_root, "broken", SessionFolder.HeaderFileName), "{ nope");

        var list = store.List();
        Assert.Equal(["sprint (2)", "sprint", "broken"], list.Select(s => s.Name));
        Assert.Equal((5, "main", "Sprint", 1), (list[0].Generation, list[0].Branch, list[0].TrackName, list[0].Branches));
        Assert.True(list[0].CanOpen && list[0].Compatibility.CanResume);
        Assert.False(list[2].CanOpen);
        Assert.Equal("sprint (2)", store.Latest()!.Name);

        // A newer save of another SimVersion opens read-only: Continue skips it.
        var old = store.Create("old physics");
        SaveSession(old, 2, new DateTime(2026, 10, 3, 10, 0, 0, DateTimeKind.Utc));
        string header = Path.Combine(old.Path, SessionFolder.HeaderFileName);
        File.WriteAllText(header, File.ReadAllText(header).Replace($"\"simVersion\": {Nitrogenesis.Sim.SimInfo.SimVersion},",
            $"\"simVersion\": {Nitrogenesis.Sim.SimInfo.SimVersion - 1},"));
        Assert.Equal("old physics", store.List()[0].Name);
        Assert.True(store.List()[0].CanOpen);
        Assert.Equal("sprint (2)", store.Latest()!.Name);
        store.Delete("old physics");

        Assert.Equal("Sprint run", store.Rename("sprint (2)", "Sprint run"));
        Assert.Equal("Sprint run (2)", store.Rename("sprint", "Sprint run"));
        Assert.Equal("SPRINT RUN", store.Rename("Sprint run", "SPRINT RUN")); // only the case changes
        Assert.Equal(["SPRINT RUN", "Sprint run (2)", "broken"], store.List().Select(s => s.Name));
        Assert.Equal(5, Session.Load(store.Folder("SPRINT RUN")).Current.Generation);

        store.Delete("SPRINT RUN");
        Assert.Equal(["Sprint run (2)", "broken"], store.List().Select(s => s.Name));
        Assert.Throws<ArgumentException>(() => store.Delete("../x"));
        Assert.Throws<DirectoryNotFoundException>(() => store.Rename("nothing", "x"));
    }

    [Fact]
    public void AnOpenSessionCannotBeRenamedOrDeleted()
    {
        var store = new SessionStore(_root);
        var a = store.Create("open");
        SaveSession(a, 2, new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc));
        using (a.AcquireLock()) // what a training host holds while it trains the session
        {
            Assert.Throws<SessionLockedException>(() => store.Rename("open", "other"));
            Assert.Throws<SessionLockedException>(() => store.Delete("open"));
            Assert.Equal(2, Session.Load(store.Folder("open")).Current.Generation);
        }
        Assert.Equal("other", store.Rename("open", "other"));
        store.Delete("other");
        Assert.Empty(store.List());
    }

    [Fact]
    public void DefaultNamesAndLocation()
    {
        Assert.Equal("labyrinth 2026-10-09 18.05", SessionStore.DefaultName("labyrinth", new DateTime(2026, 10, 9, 18, 5, 0)));
        string? old = Environment.GetEnvironmentVariable(AppPaths.DataDirVariable);
        try
        {
            Environment.SetEnvironmentVariable(AppPaths.DataDirVariable, _root);
            Assert.Equal(Path.Combine(_root, "sessions"), AppPaths.Sessions);
            Assert.Equal(Path.Combine(_root, "sessions"), SessionStore.Default().Root);
            Environment.SetEnvironmentVariable(AppPaths.DataDirVariable, null);
            Assert.EndsWith(Path.Combine("Nitrogenesis", "sessions"), AppPaths.Sessions);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.DataDirVariable, old);
        }
    }
}
