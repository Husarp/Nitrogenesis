using Nitrogenesis.Sim.History;

public class HistoryStoreTests
{
    private static HistoryEntry Entry(int generation, bool snapshot = true) => SessionFolderTests.FakeEntry(generation, snapshot);

    private static long SnapshotSize => Entry(0).SnapshotBytes;

    private static int[] WithSnapshot(HistoryStore store, string branch) =>
        store.Find(branch)!.Entries.Where(e => e.Snapshot is not null).Select(e => e.Generation).ToArray();

    [Fact]
    public void BytesCountEveryEntry()
    {
        var store = new HistoryStore();
        long expected = 0;
        for (int g = 0; g < 10; g++)
        {
            var e = Entry(g, snapshot: g % 2 == 0);
            expected += e.Bytes;
            store.Add("main", e);
        }
        Assert.Equal(expected, store.Bytes);
        Assert.Equal(5 * SnapshotSize, store.SnapshotBytes);
        Assert.Equal(5, store.SnapshotCount);
        Assert.Throws<ArgumentException>(() => store.Add("main", Entry(9)));
    }

    [Fact]
    public void UnderTheLimitNothingIsThinned()
    {
        var store = new HistoryStore();
        for (int g = 0; g < 50; g++) store.Add("main", Entry(g));
        Assert.Equal(0, store.Thin());
        Assert.Equal(50, store.SnapshotCount);
    }

    [Fact]
    public void ThinningKeepsEveryFifthOldestFirstThenEveryTenth()
    {
        var store = new HistoryStore();
        for (int g = 0; g < 40; g++) store.Add("main", Entry(g));
        long withoutSnapshots = store.Bytes - store.SnapshotBytes;

        // Room for 36 snapshots: the 4 oldest that are not multiples of 5 go (1, 2, 3, 4).
        store.MemoryLimit = withoutSnapshots + 36 * SnapshotSize;
        var thinned = new List<int>();
        Assert.Equal(4, store.Thin((_, g) => thinned.Add(g)));
        Assert.Equal([1, 2, 3, 4], thinned);
        Assert.True(store.Bytes <= store.MemoryLimit);

        // Room for 8: only multiples of 5 are left (0, 5, …, 35), every one of them still fits.
        store.MemoryLimit = withoutSnapshots + 8 * SnapshotSize;
        store.Thin();
        Assert.Equal([0, 5, 10, 15, 20, 25, 30, 35], WithSnapshot(store, "main"));

        // Room for 6: level 2 (every 10th) drops the oldest odd multiples of 5 first.
        store.MemoryLimit = withoutSnapshots + 6 * SnapshotSize;
        store.Thin();
        Assert.Equal([0, 10, 20, 25, 30, 35], WithSnapshot(store, "main"));

        // Room for 1: only generation 0 is left; best genomes, recordings and stats are all still there.
        store.MemoryLimit = withoutSnapshots + SnapshotSize;
        store.Thin();
        Assert.Equal([0], WithSnapshot(store, "main"));
        Assert.Equal(40, store.Find("main")!.Count);
        Assert.All(store.Find("main")!.Entries, e => Assert.NotEmpty(e.RecordingBytes));

        // Below even that: generation 0 (a multiple of every spacing) stays, and the store stops trying.
        store.MemoryLimit = 0;
        Assert.Equal(0, store.Thin());
        Assert.Equal([0], WithSnapshot(store, "main"));
    }

    [Fact]
    public void SteadyStateKeepsRecentGenerationsEveryFifthAndOldOnesSparser()
    {
        var store = new HistoryStore();
        long perEntry = Entry(1, snapshot: false).Bytes;
        store.MemoryLimit = 200 * perEntry + 30 * SnapshotSize; // about 30 snapshots for 200 generations
        for (int g = 0; g < 200; g++)
        {
            store.Add("main", Entry(g));
            store.Thin();
            Assert.True(store.Bytes <= store.MemoryLimit || store.SnapshotCount <= 1);
        }
        int[] kept = WithSnapshot(store, "main");
        Assert.All(kept, g => Assert.Equal(0, g % 5));
        Assert.Contains(0, kept);
        // Old generations are sparser than recent ones.
        int oldGaps = kept.Where(g => g < 100).Count(), newGaps = kept.Where(g => g >= 100).Count();
        Assert.True(newGaps > oldGaps, $"recent {newGaps} vs old {oldGaps}: {string.Join(",", kept)}");
    }

    [Fact]
    public void OnATieTheOlderBranchIsThinnedFirst()
    {
        var store = new HistoryStore();
        store.Add("main", Entry(3));
        store.Add("fork-1", Entry(3));
        store.Add("fork-1", Entry(4));
        store.MemoryLimit = store.Bytes - 1;
        var thinned = new List<(string, int)>();
        store.Thin((b, g) => thinned.Add((b, g)));
        Assert.Equal([("main", 3)], thinned);
    }

    [Fact]
    public void ThinnedEntriesAreNewObjectsSoEarlierReadersKeepTheirData()
    {
        var store = new HistoryStore();
        for (int g = 0; g < 3; g++) store.Add("main", Entry(g));
        var held = store.Get("main", 1)!;
        store.MemoryLimit = store.Bytes - 1;
        store.Thin();
        Assert.NotNull(held.Snapshot);
        Assert.Null(store.Get("main", 1)!.Snapshot);
        Assert.True(held.SameResultAs(store.Get("main", 1)!));
    }

    [Fact]
    public void LookupByGenerationWorksWithGaps()
    {
        var store = new HistoryStore();
        foreach (int g in new[] { 0, 1, 2, 5, 6, 9 }) store.Add("main", Entry(g));
        var b = store.Find("main")!;
        Assert.Equal(3, b.IndexOf(5));
        Assert.Equal(-1, b.IndexOf(3));
        Assert.Equal(9, b.Get(9)!.Generation);
        Assert.Null(b.Get(10));
    }

    [Fact]
    public void AForkAddingOlderGenerationsIsThinnedInTurn()
    {
        var store = new HistoryStore();
        for (int g = 0; g < 40; g++) store.Add("main", Entry(g));
        long withoutSnapshots = store.Bytes - store.SnapshotBytes;
        store.MemoryLimit = withoutSnapshots + 8 * SnapshotSize;
        store.Thin(); // main keeps 0, 5, …, 35
        for (int g = 12; g < 16; g++) store.Add("fork-1", Entry(g)); // older generations than main's newest
        store.MemoryLimit = store.Bytes - 3 * SnapshotSize; // three have to go
        var thinned = new List<(string, int)>();
        store.Thin((b, g) => thinned.Add((b, g)));
        Assert.Equal([("fork-1", 12), ("fork-1", 13), ("fork-1", 14)], thinned);
        Assert.Equal([15], WithSnapshot(store, "fork-1"));
    }

    [Fact]
    public void ThinningEachGenerationStaysCheapWithManySnapshots()
    {
        // 20 000 small snapshots under the limit, then one thinning per new generation: each must be quick.
        var store = new HistoryStore();
        var small = SessionFolderTests.FakeEntry(0);
        for (int g = 0; g < 20_000; g++)
            store.Add("main", new HistoryEntry { Generation = g, Seed = 0, Params = small.Params, Stats = small.Stats, Ticks = 1, BestGenome = small.BestGenome, RecordingBytes = small.RecordingBytes, Snapshot = small.Snapshot });
        store.MemoryLimit = store.Bytes;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (int g = 20_000; g < 22_000; g++)
        {
            store.Add("main", new HistoryEntry { Generation = g, Seed = 0, Params = small.Params, Stats = small.Stats, Ticks = 1, BestGenome = small.BestGenome, RecordingBytes = small.RecordingBytes, Snapshot = small.Snapshot });
            store.MemoryLimit += small.Bytes - small.SnapshotBytes;
            Assert.Equal(1, store.Thin());
        }
        Assert.True(watch.Elapsed.TotalSeconds < 2, $"2000 thinnings took {watch.Elapsed.TotalMilliseconds:F0} ms");
    }
}
