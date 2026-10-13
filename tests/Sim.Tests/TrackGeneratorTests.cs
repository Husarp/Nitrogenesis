using System.Text.Json.Nodes;
using Nitrogenesis.Sim.Gen;
using Nitrogenesis.Sim.Io;
using Nitrogenesis.Sim.Map;
using Nitrogenesis.Sim.Racing;

/// <summary>The track generator (PLAN §7): determinism, the validation rules, the retry loop, meta and every template.</summary>
public class TrackGeneratorTests
{
    public static TheoryData<TrackTemplate> AllTemplates => new(Templates.All);

    public static TheoryData<TrackTemplate, ulong> TemplatesAndSeeds
    {
        get
        {
            var data = new TheoryData<TrackTemplate, ulong>();
            foreach (var t in Templates.All)
                foreach (ulong seed in new ulong[] { 1, 2, 3 })
                    data.Add(t, seed);
            return data;
        }
    }

    private static Track Make(TrackTemplate t, ulong seed) => TrackGenerator.Generate(t, GeneratorParams.Defaults(t), seed)!;

    [Theory]
    [MemberData(nameof(AllTemplates))]
    public void SameSeedGivesTheSameTrack(TrackTemplate template)
    {
        var a = Make(template, 7);
        var b = Make(template, 7);
        Assert.Equal(a.Grid.Cells, b.Grid.Cells);
        Assert.Equal(a.Start, b.Start);
        Assert.Equal(TrackHash.Compute(a), TrackHash.Compute(b));
        Assert.NotEqual(TrackHash.Compute(a), TrackHash.Compute(Make(template, 8)));
    }

    [Theory]
    [MemberData(nameof(TemplatesAndSeeds))]
    public void EveryTemplateMakesAValidTrack(TrackTemplate template, ulong seed)
    {
        var p = GeneratorParams.Defaults(template);
        var track = TrackGenerator.Generate(template, p, seed);
        Assert.NotNull(track);
        Assert.Equal(EditDocument.Dimensions(MapSize.M), (track.Grid.Width, track.Grid.Height));
        var check = TrackGenerator.Validate(track, p);
        Assert.True(check.Valid, $"{template} seed {seed}: {check.Text}");
        Assert.InRange(check.ReferenceSeconds, 20f, 40f);
        Assert.Equal(SolvabilityResult.Solvable, Solvability.Check(track, new RacingSettings().GrassCost));
    }

    [Theory]
    [MemberData(nameof(AllTemplates))]
    public void MetaHoldsTemplateParamsAndSeedAndMakesTheSameTrackAgain(TrackTemplate template)
    {
        var p = GeneratorParams.Defaults(template) with { RoadWidth = 9, Verge = 3 };
        var track = TrackGenerator.Generate(template, p, 42)!;
        Assert.Equal(template.ToString(), track.Meta.Template);
        Assert.Equal(42UL, track.Meta.Seed);

        var loaded = TrackFile.Deserialize(TrackFile.Serialize(track)); // through the file format
        var again = Templates.FromMeta(loaded.Meta);
        Assert.Equal(template, again);
        var params2 = GeneratorParams.FromJson(template, loaded.Meta.Params);
        Assert.Equal(p.Clamped(), params2);
        var remade = TrackGenerator.Generate(again!.Value, params2, loaded.Meta.Seed!.Value)!;
        Assert.Equal(TrackHash.Compute(track), TrackHash.Compute(remade));
    }

    [Fact]
    public void GenerateSimilarKeepsTemplateAndParamsWithANewSeed()
    {
        var p = GeneratorParams.Defaults(TrackTemplate.WrongTurns) with { Branches = 5 };
        var first = TrackGenerator.GenerateValid(TrackTemplate.WrongTurns, p, 3).Track!;
        // What the editor's "Generate similar" does: template and parameters from the meta, another seed.
        var template = Templates.FromMeta(first.Meta)!.Value;
        var similar = TrackGenerator.GenerateValid(template, GeneratorParams.FromJson(template, first.Meta.Params), 1000).Track!;
        Assert.Equal(first.Meta.Template, similar.Meta.Template);
        Assert.True(JsonNode.DeepEquals(first.Meta.Params, similar.Meta.Params));
        Assert.NotEqual(first.Meta.Seed, similar.Meta.Seed);
        Assert.NotEqual(TrackHash.Compute(first), TrackHash.Compute(similar));
    }

    [Fact]
    public void ParamsStoreOnlyWhatTheTemplateUses()
    {
        var json = GeneratorParams.Defaults(TrackTemplate.Labyrinth).ToJson(TrackTemplate.Labyrinth);
        Assert.Equal("M", (string)json["size"]!);
        Assert.Equal(7, (int)json["roadWidth"]!);
        Assert.NotNull(json["deadEndLength"]);
        Assert.Null(json["branches"]);
        Assert.Null(json["obstacles"]);
        Assert.NotNull(GeneratorParams.Defaults(TrackTemplate.WrongTurns).ToJson(TrackTemplate.WrongTurns)["branches"]);
        Assert.NotNull(GeneratorParams.Defaults(TrackTemplate.ObstacleField).ToJson(TrackTemplate.ObstacleField)["obstacles"]);
    }

    [Fact]
    public void ParamsFromJsonClampAndIgnoreWhatTheyCannotRead()
    {
        var json = new JsonObject { ["size"] = "l", ["roadWidth"] = "wide", ["verge"] = 99, ["branches"] = 4, ["unknown"] = true, ["minSeconds"] = 30, ["maxSeconds"] = 20 };
        var p = GeneratorParams.FromJson(TrackTemplate.WrongTurns, json);
        Assert.Equal(MapSize.L, p.Size);
        Assert.Equal(GeneratorParams.Defaults(TrackTemplate.WrongTurns).RoadWidth, p.RoadWidth); // not a number: the default
        Assert.Equal(3, p.Verge);                                                                   // clamped
        Assert.Equal(4, p.Branches);
        Assert.Equal((30, 35), (p.MinSeconds, p.MaxSeconds));                                       // the longest stays 5 s above the shortest
        Assert.Equal(GeneratorParams.Defaults(TrackTemplate.Sprint), GeneratorParams.FromJson(TrackTemplate.Sprint, null));
        Assert.Null(Templates.FromMeta(new TrackMeta("NoSuchTemplate")));
        Assert.Null(Templates.FromMeta(new TrackMeta()));
    }

    // ---------------------------------------------------------------- validation rules

    [Fact]
    public void ValidationChecksTheTimeWindow()
    {
        var p = GeneratorParams.Defaults(TrackTemplate.Sprint);
        var track = Make(TrackTemplate.Sprint, 1);
        float seconds = TrackGenerator.Validate(track, p).ReferenceSeconds;
        Assert.InRange(seconds, 20f, 40f);
        var tooShort = TrackGenerator.Validate(track, p with { MinSeconds = (int)seconds + 2, MaxSeconds = (int)seconds + 20 });
        Assert.Equal(GenProblem.TooShort, tooShort.Problem);
        var tooLong = TrackGenerator.Validate(track, p with { MinSeconds = 5, MaxSeconds = (int)seconds - 2 });
        Assert.Equal(GenProblem.TooLong, tooLong.Problem);
        Assert.Equal(seconds, tooLong.ReferenceSeconds);
    }

    [Fact]
    public void ValidationRejectsAGrassShortcut()
    {
        // The grass_shortcut track is no shortcut at the real grass speed; with grass as fast as road it is.
        var track = TestTracks.Load("grass_shortcut.track");
        var p = new GeneratorParams { MinSeconds = 5, MaxSeconds = 100 };
        Assert.True(TrackGenerator.Validate(track, p).Valid);
        Assert.Equal(GenProblem.GrassShortcut, TrackGenerator.Validate(track, p, new RacingSettings { GrassSpeedFraction = 1f }).Problem);
    }

    [Fact]
    public void ValidationRejectsAnUnfinishableTrack()
    {
        var track = Make(TrackTemplate.Sprint, 1);
        new TrackBuilder(track.Grid).Fill(CellType.Wall, onlyOver: CellType.Finish);
        Assert.Equal(GenProblem.NotFinishable, TrackGenerator.Validate(track, GeneratorParams.Defaults(TrackTemplate.Sprint)).Problem);
    }

    [Fact]
    public void ValidationWantsRoadBehindTheStart()
    {
        var track = Make(TrackTemplate.Twisty, 1);
        float a = track.Start.AngleDeg * Nitrogenesis.Sim.Brain.FastMath.DegToRad;
        float bx = track.Start.X - 2.5f * Nitrogenesis.Sim.Brain.FastMath.Cos(a), by = track.Start.Y - 2.5f * Nitrogenesis.Sim.Brain.FastMath.Sin(a);
        new TrackBuilder(track.Grid).Circle(bx, by, 0.6, CellType.Grass); // a grass spot just behind the car
        Assert.Equal(GenProblem.NoRoadBehindStart, TrackGenerator.Validate(track, GeneratorParams.Defaults(TrackTemplate.Twisty)).Problem);
    }

    [Fact]
    public void GeneratedStartsHaveACarLengthOfRoadBehindThem()
    {
        foreach (var t in Templates.All)
            foreach (int width in new[] { 4, 8, 20 })
            {
                var track = TrackGenerator.Generate(t, GeneratorParams.Defaults(t) with { RoadWidth = width, Size = MapSize.L }, 5);
                if (track is null) continue; // a wide road may not fit
                float a = track.Start.AngleDeg * Nitrogenesis.Sim.Brain.FastMath.DegToRad;
                for (float d = 0; d <= CarSize.HalfLength + CarSize.Length; d += 0.25f)
                {
                    float x = track.Start.X - d * Nitrogenesis.Sim.Brain.FastMath.Cos(a), y = track.Start.Y - d * Nitrogenesis.Sim.Brain.FastMath.Sin(a);
                    Assert.Equal(CellType.Road, track.Grid[(int)MathF.Floor(x), (int)MathF.Floor(y)]);
                }
            }
    }

    // ---------------------------------------------------------------- retries

    [Fact]
    public void RetriesTheNextSeedUntilATrackIsValid()
    {
        var seen = new List<ulong>();
        var r = TrackGenerator.GenerateValid(TrackTemplate.Twisty, GeneratorParams.Defaults(TrackTemplate.Twisty), 10, 50,
            (attempt, seed) => seen.Add(seed), CancellationToken.None,
            t => t.Meta.Seed < 13 ? new GenCheck(GenProblem.TooLong, 45f, "too long") : new GenCheck(GenProblem.None, 30f, "ok"));
        Assert.NotNull(r.Track);
        Assert.Equal(13UL, r.Seed);
        Assert.Equal(13UL, r.Track.Meta.Seed);
        Assert.Equal(4, r.Attempts);
        Assert.Equal(new ulong[] { 10, 11, 12, 13 }, seen);
        Assert.Equal(new ulong[] { 10, 11, 12 }, r.Failed.Select(f => f.Seed));
        Assert.All(r.Failed, f => Assert.Equal(GenProblem.TooLong, f.Check.Problem));
        Assert.Equal(TrackHash.Compute(Make(TrackTemplate.Twisty, 13)), TrackHash.Compute(r.Track));
    }

    [Fact]
    public void GivesUpAfterFiftySeedsAndSaysWhy()
    {
        var r = TrackGenerator.GenerateValid(TrackTemplate.Sprint, GeneratorParams.Defaults(TrackTemplate.Sprint), 7, TrackGenerator.MaxAttempts,
            null, CancellationToken.None, _ => new GenCheck(GenProblem.TooLong, 45f, "too long"));
        Assert.Null(r.Track);
        Assert.Equal(50, r.Attempts);
        Assert.Equal(50, r.Failed.Count);
        Assert.Equal(56UL, r.Failed[^1].Seed);
        Assert.StartsWith("No valid track in 50 tries (seeds 7–56)", r.Summary);
        Assert.Contains("reference time over the longest time 50×", r.Summary);
        Assert.Equal("Try a wider time range.", r.Hint);
    }

    [Fact]
    public void ARouteThatDoesNotFitIsReportedToo()
    {
        // A wide sprint does not fit a 30 s route on the small map.
        var r = TrackGenerator.GenerateValid(TrackTemplate.Sprint, GeneratorParams.Defaults(TrackTemplate.Sprint) with { Size = MapSize.S, RoadWidth = 30 }, 1, maxAttempts: 3);
        Assert.Null(r.Track);
        Assert.All(r.Failed, f => Assert.Equal(GenProblem.NoRoom, f.Check.Problem));
        Assert.Contains("the route did not fit on the map 3×", r.Summary);
        Assert.StartsWith("The route does not fit", r.Hint);
    }

    [Fact]
    public void CancellationStopsBetweenAttempts()
    {
        using var cts = new CancellationTokenSource();
        int attempts = 0;
        Assert.Throws<OperationCanceledException>(() => TrackGenerator.GenerateValid(TrackTemplate.Sprint, GeneratorParams.Defaults(TrackTemplate.Sprint), 1,
            TrackGenerator.MaxAttempts, (_, _) => { if (++attempts == 2) cts.Cancel(); }, cts.Token, _ => new GenCheck(GenProblem.TooLong, 0, "")));
        Assert.Equal(2, attempts);
    }

    // ---------------------------------------------------------------- the committed fixed-seed tracks

    public static TheoryData<string, TrackTemplate> GeneratedFiles => new()
    {
        { "gen_sprint.track", TrackTemplate.Sprint },
        { "gen_twisty.track", TrackTemplate.Twisty },
        { "gen_labyrinth.track", TrackTemplate.Labyrinth },
        { "gen_wrong_turns.track", TrackTemplate.WrongTurns },
        { "gen_obstacle_field.track", TrackTemplate.ObstacleField },
        { "gen_mixed.track", TrackTemplate.Mixed },
    };

    /// <summary>The fixed-seed tracks in tracks/ are exactly what the generator makes from their meta (a cross-platform pin, as their hashes are pinned in BundledTracksTests).</summary>
    [Theory]
    [MemberData(nameof(GeneratedFiles))]
    public void CommittedGeneratedTracksAreReproducible(string file, TrackTemplate template)
    {
        var track = TestTracks.Load(file);
        Assert.Equal(template, Templates.FromMeta(track.Meta));
        var remade = TrackGenerator.Generate(template, GeneratorParams.FromJson(template, track.Meta.Params), track.Meta.Seed!.Value)!;
        Assert.Equal(track.Grid.Cells, remade.Grid.Cells);
        Assert.Equal(track.Start, remade.Start);
    }
}
