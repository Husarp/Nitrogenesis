using Nitrogenesis.Sim;
using Nitrogenesis.Sim.Brain;
using Nitrogenesis.Sim.Core;
using Nitrogenesis.Sim.Evolution;
using Nitrogenesis.Sim.History;
using Nitrogenesis.Sim.Io;
using Nitrogenesis.Sim.Map;
using Nitrogenesis.Sim.Racing;

public sealed class SessionFolderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ng-session-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static readonly BrainShape Shape = new(5, 4, 0, 2);

    /// <summary>A small synthetic record (no simulation needed); every field depends on the generation.</summary>
    private static GenerationRecord FakeRecord(int generation, bool snapshot = true)
    {
        var weights = new float[Shape.WeightCount];
        for (int i = 0; i < weights.Length; i++) weights[i] = generation + i / 100f;
        var recording = new Recording();
        for (int t = 0; t < 30 + generation; t++) recording.Add(10 + t * 0.3f, 20 - t * 0.1f, t * 300 + generation);
        return new GenerationRecord
        {
            Generation = generation,
            Seed = 0xF000_0000_0000_0000UL + (ulong)generation,
            Params = EvolutionParams.From(new EvolutionSettings { MutationSigma = 0.1f + generation / 1000f }, generation % 3 == 0),
            Stats = new GenerationStats(2.5f - generation / 100f, 0.4f, generation % 7, generation, generation % 2 == 0 ? float.PositiveInfinity : 700.25f, 120.5f),
            BestGenome = new Genome(Shape, weights),
            BestRecording = recording,
            PopulationSnapshot = snapshot ? Enumerable.Range(0, 3 * Shape.WeightCount).Select(i => (float)(i * generation)).ToArray() : null,
        };
    }

    private static string Describe(GenerationRecord r)
    {
        var rec = r.BestRecording;
        var samples = Enumerable.Range(0, rec.Count).Select(i => $"{rec.X(i)}/{rec.Y(i)}/{rec.Heading(i)}");
        return $"{r.Generation} {r.Seed} {r.Params} {r.Stats} {string.Join(",", r.BestGenome.Weights.Select(BitConverter.SingleToUInt32Bits))} " +
               $"{rec.SimVersion} {string.Join(",", samples)} {(r.PopulationSnapshot is null ? "-" : string.Join(",", r.PopulationSnapshot))}";
    }

    // ---- history chunks ----

    [Fact]
    public void ChunkRoundTrip()
    {
        var records = Enumerable.Range(40, 5).Select(g => FakeRecord(g, snapshot: g != 42)).ToList();
        var decoded = HistoryChunk.Decode(HistoryChunk.Encode(records));
        Assert.Equal(records.Select(Describe), decoded.Select(Describe));
        Assert.Null(decoded[2].PopulationSnapshot);
    }

    [Fact]
    public void DamagedChunksAreRejected()
    {
        byte[] data = HistoryChunk.Encode([FakeRecord(0), FakeRecord(1)]);
        Assert.Throws<InvalidDataException>(() => HistoryChunk.Decode(data[..^5]));
        Assert.Throws<InvalidDataException>(() => HistoryChunk.Decode([.. data, 0]));
        var badMagic = (byte[])data.Clone();
        badMagic[0] = (byte)'X';
        Assert.Throws<InvalidDataException>(() => HistoryChunk.Decode(badMagic));
        var newer = (byte[])data.Clone();
        newer[4] = 99;
        Assert.Throws<InvalidDataException>(() => HistoryChunk.Decode(newer));
        Assert.Throws<ArgumentException>(() => HistoryChunk.Encode([FakeRecord(0), FakeRecord(2)]));
    }

    [Fact]
    public void HistoryIsWrittenInChunksOf25AndReadBack()
    {
        var folder = new SessionFolder(_dir);
        var records = Enumerable.Range(0, 30).Select(g => FakeRecord(g)).ToList();
        folder.SaveHistory("main", records);
        string branch = folder.BranchFolder("main");
        Assert.Equal(["gen-0-24.bin", "gen-25-29.bin"], Directory.GetFiles(branch).Select(Path.GetFileName).Order());
        Assert.Equal(records.Select(Describe), folder.LoadHistory("main").Select(Describe));
    }

    [Fact]
    public void CompleteChunksAreNeverRewrittenAndPartialOnesAreReplaced()
    {
        var folder = new SessionFolder(_dir);
        var records = Enumerable.Range(0, 30).Select(g => FakeRecord(g)).ToList();
        folder.SaveHistory("main", records);
        string full = Path.Combine(folder.BranchFolder("main"), "gen-0-24.bin");
        var stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(full, stamp);

        records.AddRange(Enumerable.Range(30, 22).Select(g => FakeRecord(g))); // up to 51
        folder.SaveHistory("main", records);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(full));
        Assert.Equal(["gen-0-24.bin", "gen-25-49.bin", "gen-50-51.bin"],
            Directory.GetFiles(folder.BranchFolder("main")).Select(Path.GetFileName).Order());
        Assert.Equal(records.Select(Describe), folder.LoadHistory("main").Select(Describe));
    }

    [Fact]
    public void ALeftoverShorterChunkFromACrashIsIgnored()
    {
        var folder = new SessionFolder(_dir);
        folder.SaveHistory("main", Enumerable.Range(0, 27).Select(g => FakeRecord(g)).ToList());
        string partial = Path.Combine(folder.BranchFolder("main"), "gen-25-26.bin");
        byte[] old = File.ReadAllBytes(partial);
        var all = Enumerable.Range(0, 29).Select(g => FakeRecord(g)).ToList();
        folder.SaveHistory("main", all);
        File.WriteAllBytes(partial, old); // as if the app died between writing gen-25-28 and deleting gen-25-26
        File.WriteAllText(Path.Combine(folder.BranchFolder("main"), "gen-25-28.bin.tmp"), "half");
        Assert.Equal(all.Select(Describe), folder.LoadHistory("main").Select(Describe));
    }

    [Fact]
    public void ABranchStartingInsideAChunk()
    {
        var folder = new SessionFolder(_dir);
        var records = Enumerable.Range(37, 16).Select(g => FakeRecord(g)).ToList(); // 37…52
        folder.SaveHistory("fork-1", records);
        Assert.Equal(["gen-37-49.bin", "gen-50-52.bin"], Directory.GetFiles(folder.BranchFolder("fork-1")).Select(Path.GetFileName).Order());
        Assert.Equal(records.Select(Describe), folder.LoadHistory("fork-1").Select(Describe));
        Assert.Empty(folder.LoadHistory("main"));
    }

    [Fact]
    public void MissingGenerationsAreReported()
    {
        var folder = new SessionFolder(_dir);
        folder.SaveHistory("main", Enumerable.Range(0, 30).Select(g => FakeRecord(g)).ToList());
        File.Delete(Path.Combine(folder.BranchFolder("main"), "gen-0-24.bin"));
        folder.SaveHistory("other", Enumerable.Range(0, 25).Select(g => FakeRecord(g)).ToList());
        File.Move(Path.Combine(folder.BranchFolder("other"), "gen-0-24.bin"), Path.Combine(folder.BranchFolder("main"), "gen-0-23.bin"));
        Assert.Throws<InvalidDataException>(() => folder.LoadHistory("main"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../x")]
    [InlineData("a b")]
    [InlineData("ä")]
    public void UnsafeBranchNamesAreRejected(string name) =>
        Assert.Throws<ArgumentException>(() => SessionFolder.CheckBranchName(name));

    // ---- header ----

    private static SessionHeader SampleHeader(out SimConfig config, out Track track)
    {
        track = TestTracks.Load("s_curve.track");
        var settings = new RacingSettings { MaxSpeed = 22f, Walls = WallMode.Bounce, RayCount = 9, TimeLimitSeconds = 40f };
        var brain = new BrainShape(settings.InputCount, 16, 8, RacingSettings.OutputCount);
        config = new SimConfig(TrackHash.Compute(track), settings, brain, 2400);
        var population = new Population(brain, 7);
        population.Randomize(3, RacingSettings.InitialOutputBias());
        return new SessionHeader
        {
            CurrentBranch = "fork-a",
            Evolution = new EvolutionSettings { MutationSigma = 0.35f, StagnationBoost = false },
            Segments =
            {
                new SegmentEntry(0, "main", 0, config, track),
                new SegmentEntry(1, "fork-a", 12, new SimConfig(config.TrackHash, settings with { MaxSpeed = 30f }, brain, 2400), track),
            },
            Branches =
            {
                new BranchEntry("main", null, null, 0xFEDCBA9876543210UL),
                new BranchEntry("fork-a", "main", 11, 42),
            },
            Population = new PopulationEntry("fork-a", 20, 1, EvolutionParams.From(new EvolutionSettings(), true), population),
        };
    }

    [Fact]
    public void HeaderRoundTrip()
    {
        var header = SampleHeader(out var config, out var track);
        var folder = new SessionFolder(_dir);
        folder.SaveHeader(header);
        var loaded = folder.LoadHeader();

        Assert.Equal(SimInfo.SimVersion, loaded.SimVersion);
        Assert.Equal("fork-a", loaded.CurrentBranch);
        Assert.Equal(header.Evolution, loaded.Evolution);
        Assert.Equal(header.Branches, loaded.Branches);
        Assert.Equal(2, loaded.Segments.Count);
        var s0 = loaded.Segments[0];
        Assert.Equal(config.Hash, s0.Config.Hash);
        Assert.Equal((RacingSettings)config.Mode, (RacingSettings)s0.Config.Mode);
        Assert.Equal(config.Brain, s0.Config.Brain);
        Assert.Equal(TrackHash.Compute(track), TrackHash.Compute(s0.Track));
        Assert.Equal(track.Name, s0.Track.Name);
        Assert.Equal(header.Segments[1].Config.Hash, loaded.Segments[1].Config.Hash);
        Assert.Equal((12, "fork-a"), (loaded.Segments[1].FirstGeneration, loaded.Segments[1].Branch));

        var p = loaded.Population!;
        Assert.Equal(("fork-a", 20, 1), (p.Branch, p.Generation, p.Segment));
        Assert.Equal(header.Population!.Params, p.Params);
        Assert.Equal(header.Population.Population.Shape, p.Population.Shape);
        Assert.Equal(header.Population.Population.ContentHash(), p.Population.ContentHash());
        Assert.True(loaded.Compatibility.CanResume);
        Assert.Null(loaded.Compatibility.Message);
    }

    [Fact]
    public void HeaderIsReplacedAtomically()
    {
        var folder = new SessionFolder(_dir);
        var header = SampleHeader(out _, out _);
        folder.SaveHeader(header);
        header.CurrentBranch = "main";
        folder.SaveHeader(header);
        Assert.Equal("main", folder.LoadHeader().CurrentBranch);
        Assert.Equal([SessionFolder.HeaderFileName], Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public void ATamperedConfigIsRejected()
    {
        string json = SampleHeader(out var config, out _).Serialize();
        string tampered = json.Replace("\"maxSpeed\": 22", "\"maxSpeed\": 23");
        Assert.NotEqual(json, tampered);
        Assert.Throws<InvalidDataException>(() => SessionHeader.Deserialize(tampered));
        Assert.Throws<InvalidDataException>(() => SessionHeader.Deserialize(json.Replace("\"formatVersion\": 1,", "\"formatVersion\": 2,")));
        Assert.Throws<InvalidDataException>(() => SessionHeader.Deserialize("[]"));
        Assert.Throws<InvalidDataException>(() => new SessionFolder(_dir).LoadHeader());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void OtherSimVersionLoadsWithResimulationDisabled(int delta)
    {
        int other = SimInfo.SimVersion + delta;
        string json = SampleHeader(out var config, out _).Serialize()
            .Replace($"\"simVersion\": {SimInfo.SimVersion}", $"\"simVersion\": {other}")
            .Replace("\"maxSpeed\": 22", "\"maxSpeed\": 23"); // other physics: the stored hash is kept, not checked
        var loaded = SessionHeader.Deserialize(json);
        var c = loaded.Compatibility;
        Assert.Equal(other, loaded.SimVersion);
        Assert.False(c.SimVersionMatches);
        Assert.True(c.CanPlayRecordings);
        Assert.False(c.CanResimulate || c.CanFork || c.CanResume);
        Assert.Contains(delta < 0 ? "older physics version" : "newer physics version", c.Message);
        Assert.Equal(other, loaded.Segments[0].Config.SimVersion);
    }

    [Fact]
    public void ARealTrainingRunSavesAndLoads()
    {
        using var t = new RacingTraining(TestTracks.Load("sprint.track"), new RacingSettings(), 5, populationSize: 20);
        var records = new List<GenerationRecord>();
        for (int g = 0; g < 3; g++) records.Add(t.Runner.RunGeneration());
        var folder = new SessionFolder(_dir);
        folder.SaveHistory("main", records);
        folder.SaveHeader(new SessionHeader
        {
            Segments = { new SegmentEntry(0, "main", 0, t.Config, t.Track.Track) },
            Branches = { new BranchEntry("main", null, null, 5) },
            Population = new PopulationEntry("main", t.Runner.Generation, 0, records[^1].Params, t.Runner.Population),
        });

        var header = folder.LoadHeader();
        var history = folder.LoadHistory("main");
        Assert.Equal(records.Select(Describe), history.Select(Describe));
        Assert.Equal(t.Config.Hash, header.Segments[0].Config.Hash);
        Assert.Equal(t.Runner.Population.ContentHash(), header.Population!.Population.ContentHash());
        // The stored best recording replays as written.
        Assert.Equal(records[2].BestRecording.Encode(), history[2].BestRecording.Encode());
    }
}
