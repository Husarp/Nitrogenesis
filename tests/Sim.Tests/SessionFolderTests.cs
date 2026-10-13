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

    /// <summary>A small synthetic entry (no simulation needed); every field depends on the generation.</summary>
    internal static HistoryEntry FakeEntry(int generation, bool snapshot = true, int variant = 0)
    {
        var weights = new float[Shape.WeightCount];
        for (int i = 0; i < weights.Length; i++) weights[i] = generation + i / 100f + variant;
        var recording = new Recording();
        for (int t = 0; t < 30 + generation; t++) recording.Add(10 + t * 0.3f, 20 - t * 0.1f, t * 300 + generation);
        return new HistoryEntry
        {
            Generation = generation,
            Seed = 0xF000_0000_0000_0000UL + (ulong)generation,
            Params = EvolutionParams.From(new EvolutionSettings { MutationSigma = 0.1f + generation / 1000f }, generation % 3 == 0),
            Stats = new GenerationStats(2.5f - generation / 100f + variant, 0.4f, generation % 7, generation, generation % 2 == 0 ? float.PositiveInfinity : 700.25f, 120.5f),
            Ticks = 500 + generation,
            BestGenome = new Genome(Shape, weights),
            RecordingBytes = recording.Encode(),
            Snapshot = snapshot ? Enumerable.Range(0, 3 * Shape.WeightCount).Select(i => (float)(i * generation)).ToArray() : null,
        };
    }

    internal static string Describe(HistoryEntry e)
    {
        var rec = e.DecodeRecording()!;
        var samples = Enumerable.Range(0, rec.Count).Select(i => $"{rec.X(i)}/{rec.Y(i)}/{rec.Heading(i)}");
        return $"{e.Generation} {e.Seed} {e.Params} {e.Stats} {e.Ticks} {string.Join(",", e.BestGenome.Weights.Select(BitConverter.SingleToUInt32Bits))} " +
               $"{rec.SimVersion} {string.Join(",", samples)} {(e.Snapshot is null ? "-" : string.Join(",", e.Snapshot))}";
    }

    private static List<HistoryEntry> Range(int from, int count, bool snapshot = true) =>
        Enumerable.Range(from, count).Select(g => FakeEntry(g, snapshot)).ToList();

    /// <summary>Writes the windows of a branch's entries the way a save does (no header).</summary>
    private static void WriteAll(SessionFolder folder, string branch, IReadOnlyList<HistoryEntry> entries, int first, int end)
    {
        foreach (var group in entries.Where(e => e.Generation < end).GroupBy(e => e.Generation / SessionFolder.ChunkSize))
            folder.WriteWindow(new WindowWrite(branch, group.Key, first, end, group.ToList()));
    }

    private static string[] Files(string dir) => Directory.GetFiles(dir).Select(f => Path.GetFileName(f)!).Order().ToArray();

    // ---- history chunks ----

    [Fact]
    public void ChunkRoundTrip()
    {
        var entries = Enumerable.Range(40, 5).Select(g => FakeEntry(g, snapshot: g != 42)).ToList();
        var decoded = HistoryChunk.Decode(HistoryChunk.Encode(entries));
        Assert.Equal(entries.Select(Describe), decoded.Select(Describe));
        Assert.Null(decoded[2].Snapshot);
    }

    [Fact]
    public void DamagedChunksAreRejected()
    {
        byte[] data = HistoryChunk.Encode([FakeEntry(0), FakeEntry(1)]);
        Assert.Throws<InvalidDataException>(() => HistoryChunk.Decode(data[..^5]));
        Assert.Throws<InvalidDataException>(() => HistoryChunk.Decode([.. data, 0]));
        var badMagic = (byte[])data.Clone();
        badMagic[0] = (byte)'X';
        Assert.Throws<InvalidDataException>(() => HistoryChunk.Decode(badMagic));
        var newer = (byte[])data.Clone();
        newer[4] = 99;
        Assert.Throws<InvalidDataException>(() => HistoryChunk.Decode(newer));
        // Any flipped bit is caught by the checksum.
        for (int i = 8; i < data.Length; i += 37)
        {
            var flipped = (byte[])data.Clone();
            flipped[i] ^= 0x10;
            Assert.Throws<InvalidDataException>(() => HistoryChunk.Decode(flipped));
        }
        Assert.Throws<ArgumentException>(() => HistoryChunk.Encode([FakeEntry(0), FakeEntry(2)]));
    }

    [Fact]
    public void FormatOneChunksStillLoad()
    {
        // M1's layout: no ticks, no checksum.
        var e = FakeEntry(3);
        using var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream))
        {
            w.Write("NGHC"u8);
            w.Write(1);
            w.Write(SimInfo.SimVersion);
            w.Write(3);
            w.Write(1);
            w.Write(e.Generation);
            w.Write(e.Seed);
            var p = e.Params;
            w.Write(p.ElitismFraction); w.Write(p.TournamentSize); w.Write(p.CrossoverRate); w.Write(p.MutationChance); w.Write(p.MutationSigma); w.Write(p.RandomFraction); w.Write(p.Boost);
            var s = e.Stats;
            w.Write(s.BestScore); w.Write(s.AverageScore); w.Write(s.BestIndex); w.Write(s.FinishedCount); w.Write(s.BestFinishTicks); w.Write(s.BestProgress);
            w.Write(Shape.Inputs); w.Write(Shape.Hidden1); w.Write(Shape.Hidden2); w.Write(Shape.Outputs);
            foreach (float f in e.BestGenome.Weights) w.Write(f);
            w.Write(e.RecordingBytes.Length);
            w.Write(e.RecordingBytes);
            w.Write(3);
            foreach (float f in e.Snapshot!) w.Write(f);
        }
        var decoded = HistoryChunk.Decode(stream.ToArray()).Single();
        Assert.Equal(-1, decoded.Ticks); // unknown: a rebuild runs it to the end
        Assert.Equal(Describe(e).Replace($" {e.Ticks} ", " -1 "), Describe(decoded));
    }

    [Fact]
    public void HistoryIsWrittenInChunksOf25AndReadBack()
    {
        var folder = new SessionFolder(_dir);
        var entries = Range(0, 30);
        WriteAll(folder, "main", entries, 0, 30);
        Assert.Equal(["gen-0-24.bin", "gen-25-29.bin"], Files(folder.BranchFolder("main")));
        var warnings = new List<string>();
        Assert.Equal(entries.Select(Describe), folder.LoadBranch("main", 0, 30, warnings).Select(Describe));
        Assert.Empty(warnings);
    }

    [Fact]
    public void ALongerPartialChunkReplacesTheShorterOne()
    {
        var folder = new SessionFolder(_dir);
        var entries = Range(0, 30);
        WriteAll(folder, "main", entries, 0, 30);
        entries.AddRange(Range(30, 22)); // up to 51
        folder.WriteWindow(new WindowWrite("main", 1, 0, 52, entries.Where(e => e.Generation is >= 25 and < 50).ToList()));
        folder.WriteWindow(new WindowWrite("main", 2, 0, 52, entries.Where(e => e.Generation >= 50).ToList()));
        Assert.Equal(["gen-0-24.bin", "gen-25-49.bin", "gen-50-51.bin"], Files(folder.BranchFolder("main")));
        Assert.Equal(entries.Select(Describe), folder.LoadBranch("main", 0, 52, []).Select(Describe));
    }

    [Fact]
    public void ALeftoverShorterChunkFromACrashIsIgnored()
    {
        var folder = new SessionFolder(_dir);
        WriteAll(folder, "main", Range(0, 27), 0, 27);
        string partial = Path.Combine(folder.BranchFolder("main"), "gen-25-26.bin");
        byte[] old = File.ReadAllBytes(partial);
        var all = Range(0, 29);
        WriteAll(folder, "main", all, 0, 29);
        File.WriteAllBytes(partial, old); // as if the app died between writing gen-25-28 and deleting gen-25-26
        File.WriteAllText(Path.Combine(folder.BranchFolder("main"), "gen-25-28.bin.tmp"), "half");
        Assert.Equal(all.Select(Describe), folder.LoadBranch("main", 0, 29, []).Select(Describe));
    }

    [Fact]
    public void GenerationsAtOrAfterTheCommittedEndAreIgnoredAndLaterReplaced()
    {
        var folder = new SessionFolder(_dir);
        // A save wrote 25…40 (another timeline) but its header never committed: the header says the history ends at 33.
        WriteAll(folder, "main", Range(0, 41), 0, 41);
        Assert.Equal(Range(0, 33).Select(Describe), folder.LoadBranch("main", 0, 33, []).Select(Describe));
        // Training resumed at 33 and made other generations; the next save writes 25…36 and drops the stale file.
        var redone = Range(0, 33).Concat(Enumerable.Range(33, 4).Select(g => FakeEntry(g, variant: 7))).ToList();
        folder.WriteWindow(new WindowWrite("main", 1, 0, 37, redone.Where(e => e.Generation >= 25).ToList()));
        Assert.Equal(["gen-0-24.bin", "gen-25-36.bin"], Files(folder.BranchFolder("main")));
        Assert.Equal(redone.Select(Describe), folder.LoadBranch("main", 0, 37, []).Select(Describe));
    }

    [Fact]
    public void ABranchStartingInsideAChunk()
    {
        var folder = new SessionFolder(_dir);
        var entries = Range(37, 16); // 37…52
        WriteAll(folder, "fork-1", entries, 37, 53);
        Assert.Equal(["gen-37-49.bin", "gen-50-52.bin"], Files(folder.BranchFolder("fork-1")));
        Assert.Equal(entries.Select(Describe), folder.LoadBranch("fork-1", 37, 53, []).Select(Describe));
        Assert.Empty(folder.LoadBranch("main", 0, 0, []));
    }

    [Fact]
    public void ADamagedChunkLeavesAReportedGapAndTheRestLoads()
    {
        var folder = new SessionFolder(_dir);
        WriteAll(folder, "main", Range(0, 60), 0, 60);
        string middle = Path.Combine(folder.BranchFolder("main"), "gen-25-49.bin");
        byte[] bytes = File.ReadAllBytes(middle);
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(middle, bytes);
        var warnings = new List<string>();
        var loaded = folder.LoadBranch("main", 0, 60, warnings);
        Assert.Equal(Range(0, 25).Concat(Range(50, 10)).Select(Describe), loaded.Select(Describe));
        Assert.Contains("generations 25–49", Assert.Single(warnings));

        // A save of that window with what is known keeps the damaged file (it still holds generations we lack).
        folder.WriteWindow(new WindowWrite("main", 1, 0, 60, []));
        Assert.True(File.Exists(middle));
        File.Delete(Path.Combine(folder.BranchFolder("main"), "gen-0-24.bin"));
        warnings.Clear();
        folder.LoadBranch("main", 0, 60, warnings);
        Assert.Contains("generations 0–49", Assert.Single(warnings));
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
                new SegmentEntry(0, "main", 0, config, track, 7),
                new SegmentEntry(1, "fork-a", 12, new SimConfig(config.TrackHash, settings with { MaxSpeed = 30f }, brain, 2400), track, 7),
            },
            Branches =
            {
                new BranchEntry("main", null, null, 0xFEDCBA9876543210UL),
                new BranchEntry("fork-a", "main", 11, 42, ForkKind.Full),
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
        Assert.Equal([SessionFolder.HeaderFileName, SessionFolder.HeaderFileName + SessionFolder.BackupSuffix], Files(_dir));
        Assert.Equal("fork-a", SessionHeader.Deserialize(File.ReadAllText(folder.BackupHeaderPath)).CurrentBranch); // the previous save
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
    public void ADamagedPopulationIsCaughtByItsChecksum()
    {
        string json = SampleHeader(out _, out _).Serialize();
        var root = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        string weights = (string)root["population"]!["weights"]!;
        char[] chars = weights.ToCharArray();
        chars[10] = chars[10] == 'A' ? 'B' : 'A';
        root["population"]!["weights"] = new string(chars);
        var e = Assert.Throws<InvalidDataException>(() => SessionHeader.Deserialize(root.ToJsonString()));
        Assert.Contains("checksum", e.Message);
    }

    [Fact]
    public void M3HeaderFieldsRoundTrip()
    {
        var header = SampleHeader(out _, out _);
        header.SavedAtUtc = new DateTime(2026, 10, 9, 12, 34, 56, 789, DateTimeKind.Utc);
        header.TrainingSeconds = 1234.5;
        header.TotalTicks = 9_876_543_210;
        header.HistoryEnds["main"] = 12;
        header.HistoryEnds["fork-a"] = 21;
        header.Branches[1] = header.Branches[1] with { Kind = ForkKind.BestOnly, ForkParams = EvolutionParams.From(new EvolutionSettings { MutationSigma = 0.7f }, false) };
        var loaded = SessionHeader.Deserialize(header.Serialize());
        Assert.Equal(header.SavedAtUtc, loaded.SavedAtUtc);
        Assert.Equal(1234.5, loaded.TrainingSeconds);
        Assert.Equal(9_876_543_210, loaded.TotalTicks);
        Assert.Equal(header.HistoryEnds, loaded.HistoryEnds);
        Assert.Equal(header.Branches, loaded.Branches);
        Assert.Equal(ForkKind.Root, loaded.Branches[0].Kind);
        Assert.Equal(header.Segments.Select(s => s.PopulationSize), loaded.Segments.Select(s => s.PopulationSize));
    }

    [Fact]
    public void ModeAndSpeedRoundTripAndAreOptional()
    {
        var header = SampleHeader(out _, out _);
        Assert.Null(SessionHeader.Deserialize(header.Serialize()).StepMode); // a header without them (before M3 saved them)
        Assert.Null(SessionHeader.Deserialize(header.Serialize()).Speed);
        foreach (double speed in new[] { 0.25, 1, 10, 100, double.PositiveInfinity })
        {
            header.StepMode = true;
            header.Speed = speed;
            var loaded = SessionHeader.Deserialize(header.Serialize());
            Assert.True(loaded.StepMode);
            Assert.Equal(speed, loaded.Speed);
        }
        header.StepMode = false;
        Assert.False(SessionHeader.Deserialize(header.Serialize()).StepMode);
    }

    [Theory]
    [InlineData("\"speed\": \"fast\"")]
    [InlineData("\"speed\": -3")]
    [InlineData("\"stepMode\": \"yes\"")]
    public void AnInvalidModeOrSpeedIsRefused(string field)
    {
        var header = SampleHeader(out _, out _);
        string json = header.Serialize();
        json = "{" + field + "," + json[(json.IndexOf('{') + 1)..];
        Assert.Throws<InvalidDataException>(() => SessionHeader.Deserialize(json));
    }
}
