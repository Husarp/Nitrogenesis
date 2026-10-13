using Nitrogenesis.Sim.Brain;
using Nitrogenesis.Sim.Core;
using Nitrogenesis.Sim.Evolution;
using Nitrogenesis.Sim.Io;
using Nitrogenesis.Sim.Racing;
using Nitrogenesis.Sim.Settings;

/// <summary>M7: the settings (PLAN §6.4): kinds, clamping, reset, presets, persistence and the resource and FPS rules.</summary>
public sealed class SettingsTests : IDisposable
{
    private static readonly MachineInfo Laptop = new(LogicalCores: 8, PhysicalCores: 4, TotalMemoryBytes: 16L << 30);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ng-settings-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string Path_() => Path.Combine(_root, "settings.json");

    // ---------------------------------------------------------------- the catalog

    [Fact]
    public void EverySettingHasAUniqueKeyAWorkingDefaultATooltipAndASection()
    {
        var defs = SettingsCatalog.Create(Laptop);
        Assert.Equal(defs.Count, defs.Select(d => d.Key).Distinct().Count());
        foreach (var d in defs)
        {
            Assert.False(string.IsNullOrWhiteSpace(d.Tooltip), d.Key);
            Assert.Contains(d.Section, SettingsCatalog.Sections);
            Assert.Equal(d.Default, d.Clamp(d.Default), 6);
            Assert.True(d.Min <= d.Max, d.Key);
            if (d.Type == SettingType.Choice) Assert.Equal(d.Max + 1, d.Choices!.Count);
        }
    }

    [Fact]
    public void TheKindsAreThoseOfThePlan()
    {
        var kinds = SettingsCatalog.Create(Laptop).ToDictionary(d => d.Key, d => d.Kind);
        foreach (string k in new[] { "mutationSigma", "mutationChancePercent", "elitismPercent", "randomPercent", "stagnationBoost", "speed", "fpsCap", "vsync",
                     "graphicsQuality", "carModel", "colorMode", "threads", "headlessThreads", "priority", "historyMb", "soundUi", "soundEngine" })
            Assert.Equal(SettingKind.Live, kinds[k]);
        foreach (string k in new[] { "maxSpeed", "acceleration", "turnRateDeg", "grassSpeedPercent", "grassGrip", "walls", "timeLimitSeconds", "stallSeconds", "populationSize" })
            Assert.Equal(SettingKind.NewSegment, kinds[k]);
        foreach (string k in new[] { "rayCount", "raySpreadDeg", "rayRange", "memoryInputs", "directionHint", "hidden1", "hidden2" })
            Assert.Equal(SettingKind.NewTraining, kinds[k]);
    }

    [Fact]
    public void ARunningTrainingOnlyLocksTheNewTrainingSettings()
    {
        Assert.True(AppSettings.CanEdit(SettingKind.Live, inTraining: true));
        Assert.True(AppSettings.CanEdit(SettingKind.NewSegment, inTraining: true));
        Assert.False(AppSettings.CanEdit(SettingKind.NewTraining, inTraining: true));
        Assert.True(AppSettings.CanEdit(SettingKind.NewTraining, inTraining: false));
    }

    [Fact]
    public void TheDefaultsGiveExactlyTheDefaultRacingAndEvolutionSettings()
    {
        // A drifting default would change every new training's SimConfig hash.
        var s = new AppSettings(Laptop);
        Assert.Equal(new RacingSettings(), s.Racing);
        Assert.Equal(new EvolutionSettings(), s.Evolution);
        Assert.Equal(EvolutionSettings.DefaultPopulationSize, s.PopulationSize);
        Assert.Equal(BrainShape.DefaultHidden, s.Hidden1);
        Assert.Equal(0, s.Hidden2);
        Assert.Equal(1.0, s.Speed);
        foreach (var d in s.Defs) Assert.True(s.IsDefault(d.Key), d.Key);
    }

    // ---------------------------------------------------------------- values

    [Fact]
    public void ValuesAreClampedToTheirRangesAndTypes()
    {
        var s = new AppSettings(Laptop);
        s.Set("maxSpeed", 500);
        Assert.Equal(RacingSettings.MaxMaxSpeed, s.Get("maxSpeed"));
        s.Set("maxSpeed", -3);
        Assert.Equal(RacingSettings.MinMaxSpeed, s.Get("maxSpeed"));
        s.Set("maxSpeed", double.NaN);
        Assert.Equal(18, s.Get("maxSpeed"));
        s.Set("rayCount", 7.6);
        Assert.Equal(8, s.Get("rayCount"));
        s.Set("rayCount", 99);
        Assert.Equal(RacingSettings.MaxRayCount, s.Get("rayCount"));
        s.Set("vsync", 0.2);
        Assert.False(s.Flag("vsync"));
        s.Set("walls", 9);
        Assert.Equal(2, s.Get("walls"));
        Assert.Equal(WallMode.Kill, s.Racing.Walls);
        s.Set("threads", 100);
        Assert.Equal(Laptop.LogicalCores, s.Get("threads"));
    }

    [Fact]
    public void ZeroSettingsMeanAutoOrOffAndNothingBetweenZeroAndTheMinimum()
    {
        var s = new AppSettings(Laptop);
        s.Set("timeLimitSeconds", 0.3);
        Assert.Equal(0, s.Get("timeLimitSeconds"));
        s.Set("timeLimitSeconds", 0.8);
        Assert.Equal(RacingSettings.MinTimeLimitSeconds, s.Get("timeLimitSeconds"));
        s.Set("timeLimitSeconds", 45);
        Assert.Equal(45f, s.Racing.TimeLimitSeconds);
        s.Set("hidden2", 1);
        Assert.Equal(0, s.Get("hidden2"));
        s.Set("hidden2", 10);
        Assert.Equal(10, s.Hidden2);
        Assert.Equal("auto (3)", s.Def("threads").Format(0));
        Assert.Equal("auto (7)", s.Def("headlessThreads").Format(0));
        Assert.Equal("3", s.Def("threads").Format(3).Split(' ')[0]);
        Assert.Equal("off", s.Def("hidden2").Format(0));
        Assert.Equal("Kill", s.Def("walls").Format(2));
        Assert.Equal("45 %", s.Def("grassSpeedPercent").Format(45));
    }

    [Fact]
    public void ResetGoesBackToTheDefaultOneAtATime()
    {
        var s = new AppSettings(Laptop);
        Assert.True(s.Set("mutationSigma", 0.7));
        Assert.False(s.Set("mutationSigma", 0.7));
        Assert.False(s.IsDefault("mutationSigma"));
        Assert.Equal(0.7f, s.Evolution.MutationSigma);
        Assert.True(s.Reset("mutationSigma"));
        Assert.True(s.IsDefault("mutationSigma"));
        Assert.False(s.Reset("mutationSigma"));
        Assert.Equal(new EvolutionSettings().MutationSigma, s.Evolution.MutationSigma);
    }

    [Fact]
    public void EveryChangeRaisesTheVersion()
    {
        var s = new AppSettings(Laptop);
        int v = s.Version;
        s.Set("vsync", 0);
        Assert.True(s.Version > v);
        v = s.Version;
        s.Set("vsync", 0);
        Assert.Equal(v, s.Version);
    }

    // ---------------------------------------------------------------- presets

    [Fact]
    public void PresetsSetTheirValuesAndDefaultRestoresTheTrainingSettingsOnly()
    {
        var s = new AppSettings(Laptop);
        s.Set("fpsCap", 5);
        s.Set("speed", 8);
        var changed = s.ApplyPreset(SettingsPreset.Easy, inTraining: false);
        Assert.Contains("directionHint", changed);
        Assert.True(s.Racing.DirectionHint);
        Assert.Equal(300, s.PopulationSize);
        Assert.Equal(9, s.Racing.RayCount);
        Assert.Equal(5, s.Index("fpsCap")); // not a training setting
        Assert.Equal(8, s.Index("speed"));

        s.ApplyPreset(SettingsPreset.Chaos, inTraining: false);
        Assert.False(s.Racing.DirectionHint);
        Assert.Equal(WallMode.Kill, s.Racing.Walls);
        Assert.Equal(0.5f, s.Evolution.MutationSigma);
        Assert.Equal(0.3f, s.Evolution.MutationChance, 4);
        Assert.Equal(100, s.PopulationSize);

        s.ApplyPreset(SettingsPreset.Default, inTraining: false);
        Assert.Equal(new RacingSettings(), s.Racing);
        Assert.Equal(new EvolutionSettings(), s.Evolution);
        Assert.Equal(EvolutionSettings.DefaultPopulationSize, s.PopulationSize);
        Assert.Equal(5, s.Index("fpsCap"));
    }

    [Fact]
    public void InARunningTrainingAPresetLeavesTheNewTrainingSettingsAlone()
    {
        var s = new AppSettings(Laptop);
        var changed = s.ApplyPreset(SettingsPreset.Easy, inTraining: true);
        Assert.DoesNotContain("directionHint", changed);
        Assert.DoesNotContain("rayCount", changed);
        Assert.False(s.Racing.DirectionHint);
        Assert.Equal(7, s.Racing.RayCount);
        Assert.Contains("populationSize", changed); // a new-segment setting: the screen asks before applying
        Assert.Equal(0.15f, s.Evolution.MutationSigma);
    }

    [Theory]
    [InlineData(SettingsPreset.Easy)]
    [InlineData(SettingsPreset.Chaos)]
    public void PresetsMakeValidTrainingsThatRun(SettingsPreset preset)
    {
        var s = new AppSettings(Laptop);
        s.ApplyPreset(preset, inTraining: false);
        Assert.Equal(s.Racing, s.Racing.Clamped());
        using var training = new RacingTraining(TestTracks.Load("sprint.track"), s.Racing, 5, s.Evolution, populationSize: 30, threads: 2, s.Hidden1, s.Hidden2);
        var record = training.Runner.RunGeneration();
        Assert.Equal(0, record.Generation);
        Assert.Equal(s.Racing.InputCount, training.Shape.Inputs);
    }

    // ---------------------------------------------------------------- persistence

    [Fact]
    public void SettingsRoundTripThroughTheFileAndOnlyChangedOnesAreWritten()
    {
        var s = AppSettings.Load(Path_(), Laptop, out var problem);
        Assert.Null(problem);
        s.Set("fpsCap", 4);
        s.Set("vsync", 0);
        s.Set("mutationSigma", 0.35);
        s.Set("walls", 1);
        s.Set("carModel", 2);
        s.Set("historyMb", 2048);
        s.Set("threads", 3);
        s.Save();

        string text = File.ReadAllText(Path_());
        Assert.Contains("\"fpsCap\": \"75\"", text);
        Assert.Contains("\"walls\": \"Bounce\"", text);
        Assert.DoesNotContain("populationSize", text); // untouched = default = not written
        Assert.DoesNotContain("soundUi", text);
        Assert.False(File.Exists(Path_() + ".tmp"));

        var t = AppSettings.Load(Path_(), Laptop, out problem);
        Assert.Null(problem);
        Assert.Equal(4, t.Index("fpsCap"));
        Assert.False(t.VSync);
        Assert.Equal(0.35f, t.Evolution.MutationSigma);
        Assert.Equal(WallMode.Bounce, t.Racing.Walls);
        Assert.Equal(2, t.CarModel);
        Assert.Equal(2048L << 20, t.HistoryLimitBytes);
        Assert.Equal(3, t.VisualThreads);
        foreach (var d in t.Defs) Assert.Equal(s.Get(d.Key), t.Get(d.Key), 6);

        t.Reset("walls");
        t.Save();
        Assert.DoesNotContain("walls", File.ReadAllText(Path_()));
    }

    [Fact]
    public void AMissingFileGivesDefaultsAndABrokenOneIsKeptAsBadAndReplaced()
    {
        Directory.CreateDirectory(_root);
        var none = AppSettings.Load(Path_(), Laptop, out var problem);
        Assert.Null(problem);
        Assert.True(none.IsDefault("fpsCap"));

        File.WriteAllText(Path_(), "{ this is not json");
        var s = AppSettings.Load(Path_(), Laptop, out problem);
        Assert.NotNull(problem);
        Assert.True(File.Exists(Path_() + ".bad"));
        Assert.True(s.IsDefault("mutationSigma"));
        s.Set("vsync", 0);
        s.Save();
        Assert.False(AppSettings.Load(Path_(), Laptop, out _).VSync);

        // Valid JSON that System.Text.Json still refuses (a key twice, e.g. after a hand edit) is a damaged file too, not a crash.
        File.WriteAllText(Path_(), """{ "formatVersion": 1, "values": { "populationSize": 80, "populationSize": 90 } }""");
        s = AppSettings.Load(Path_(), Laptop, out problem);
        Assert.NotNull(problem);
        Assert.True(s.IsDefault("populationSize"));
    }

    [Fact]
    public void WrongTypedOrOutOfRangeValuesInTheFileFallBackToTheDefaultOrTheLimit()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path_(), """{"formatVersion":1,"values":{"fpsCap":"warp","maxSpeed":"fast","rayCount":400,"walls":"Kill","vsync":"yes","futureSetting":5}}""");
        var s = AppSettings.Load(Path_(), Laptop, out var problem);
        Assert.Null(problem);
        Assert.Equal(FpsCap.DefaultIndex, s.Index("fpsCap"));
        Assert.Equal(18, s.Get("maxSpeed"));
        Assert.Equal(RacingSettings.MaxRayCount, s.Racing.RayCount);
        Assert.Equal(WallMode.Kill, s.Racing.Walls);
        Assert.True(s.VSync);
        s.Save();
        Assert.Contains("futureSetting", File.ReadAllText(Path_())); // settings of a newer version survive
    }

    [Fact]
    public void ACopyForATrainingHoldsItsValuesAndIsNeverSaved()
    {
        var evolution = new EvolutionSettings { MutationSigma = 0.4f, ElitismFraction = 0.1f, StagnationBoost = false };
        var racing = new RacingSettings { MaxSpeed = 25f, Walls = WallMode.Bounce, DirectionHint = true, TimeLimitSeconds = 40f };
        var shape = new BrainShape(racing.InputCount, 16, 8, 2);
        var s = AppSettings.ForTraining(Laptop, evolution, racing, 120, shape, speed: 10);
        Assert.Equal(evolution, s.Evolution);
        Assert.Equal(racing, s.Racing);
        Assert.Equal(120, s.PopulationSize);
        Assert.Equal((16, 8), (s.Hidden1, s.Hidden2));
        Assert.Equal(10.0, s.Speed);
        s.Save(); // no path: nothing happens
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void ACopyForATrainingGivesBackItsExactFloats()
    {
        // Percent settings go float → ×100 → double and back: any drift would change the physics (and the config hash) of a new
        // segment that only meant to change something else, e.g. the number of cars.
        for (int k = 10; k <= 100; k++)
        {
            float f = k / 100f;
            var racing = new RacingSettings { GrassSpeedFraction = f, GrassGrip = f }.Clamped();
            var evolution = new EvolutionSettings { MutationChance = f, CrossoverRate = f, ElitismFraction = f / 2, RandomFraction = f / 2 }.Clamped();
            var s = AppSettings.ForTraining(Laptop, evolution, racing, 200, BrainShape.Default(racing.InputCount, 2), speed: 1);
            Assert.Equal(racing, s.Racing);
            Assert.Equal(evolution, s.Evolution);
        }
    }

    // ---------------------------------------------------------------- resources

    [Fact]
    public void ThreadsDefaultToPhysicalMinusOneWithVisualsAndLogicalMinusOneWithout()
    {
        var s = new AppSettings(Laptop);
        Assert.Equal(3, s.VisualThreads);
        Assert.Equal(7, s.HeadlessThreads);
        s.Set("threads", 2);
        s.Set("headlessThreads", 5);
        Assert.Equal((2, 5), (s.VisualThreads, s.HeadlessThreads));
        var single = new AppSettings(new MachineInfo(1, 1, 4L << 30));
        Assert.Equal((1, 1), (single.VisualThreads, single.HeadlessThreads));
    }

    [Fact]
    public void TheHistoryLimitDefaultsToOneGigabyteAndNeverExceedsThreeQuartersOfTheRam()
    {
        var s = new AppSettings(Laptop);
        Assert.Equal(1024L << 20, s.HistoryLimitBytes);
        Assert.Equal(12 * 1024, Laptop.MaxHistoryMb); // 75 % of 16 GB
        s.Set("historyMb", 100_000);
        Assert.Equal(12 * 1024, s.Get("historyMb"));
        s.Set("historyMb", 1);
        Assert.Equal(MachineInfo.MinHistoryMb, s.Get("historyMb"));

        var small = new AppSettings(new MachineInfo(2, 2, 1L << 30)); // 1 GB of RAM: 768 MB at most
        Assert.Equal(768, small.Def("historyMb").Max);
        Assert.Equal(768L << 20, small.HistoryLimitBytes);
        Assert.Contains("More memory does not make training faster", s.Def("historyMb").Tooltip);
    }

    // ---------------------------------------------------------------- FPS cap

    [Theory]
    [InlineData(0, 60.0, 24)]
    [InlineData(3, 60.0, 60)]
    [InlineData(5, 144.0, 120)]
    [InlineData(6, 60.0, 0)]
    [InlineData(7, 74.97, 75)]
    [InlineData(7, 0.0, 60)]
    public void TheCapGivesTheEngineMaxFps(int index, double hz, int expected) => Assert.Equal(expected, FpsCap.MaxFps(index, hz));

    [Theory]
    [InlineData(3, 75.0, true, "75 Hz monitor: choose 75")]   // 60 does not divide 75
    [InlineData(4, 75.0, true, null)]                          // 75 on 75
    [InlineData(1, 60.0, true, null)]                          // 30 divides 60
    [InlineData(0, 60.0, true, "60 Hz monitor")]              // 24 does not divide 60
    [InlineData(5, 144.0, true, "144 Hz monitor")]            // 120 below 144 without dividing it
    [InlineData(5, 60.0, true, null)]                          // a cap above the refresh rate is paced by VSync
    [InlineData(3, 75.0, false, null)]                         // VSync off: nothing to hint
    [InlineData(FpsCap.UnlimitedIndex, 75.0, true, null)]
    [InlineData(FpsCap.MatchMonitorIndex, 75.0, true, null)]
    [InlineData(3, 0.0, true, null)]                           // refresh rate unknown
    public void TheHintAppearsOnlyWhenTheCapDoesNotDivideTheRefreshRate(int index, double hz, bool vsync, string? expectedStart)
    {
        string? hint = FpsCap.Hint(index, hz, vsync);
        if (expectedStart is null) Assert.Null(hint);
        else Assert.StartsWith(expectedStart, hint);
    }

    [Fact]
    public void TheHintNamesTheMonitorAndTheFix()
    {
        Assert.Equal("75 Hz monitor: choose 75 (or Match monitor) or turn VSync off for smooth motion", FpsCap.Hint(3, 75, true));
        Assert.Equal(15, FpsCap.UnfocusedFps);
        Assert.Equal(60, FpsCap.Labels.Select((_, i) => FpsCap.MaxFps(i, 60)).ElementAt(FpsCap.DefaultIndex));
    }

    // ---------------------------------------------------------------- graphics, sound, colours

    [Fact]
    public void LowAndHighQualityMatchThePlan()
    {
        var low = GraphicsQuality.Low;
        Assert.False(low.Trails);
        Assert.False(low.Rays);
        Assert.False(low.AntiAliasedLines);
        Assert.Equal(200, low.MaxCarsDrawn);
        var high = GraphicsQuality.High;
        Assert.True(high.Trails && high.Rays && high.AntiAliasedLines);
        Assert.Equal(1000, high.MaxCarsDrawn);
        var s = new AppSettings(Laptop);
        Assert.Equal(high, s.Quality);
        s.Set("graphicsQuality", 0);
        Assert.Equal(low, s.Quality);
    }

    [Fact]
    public void EnginePitchIsPointEightPlusOnePointTwoTimesTheSpeedShare()
    {
        Assert.Equal(0.8f, EngineSound.Pitch(0, 18), 5);
        Assert.Equal(1.4f, EngineSound.Pitch(9, 18), 5);
        Assert.Equal(2.0f, EngineSound.Pitch(18, 18), 5);
        Assert.Equal(2.0f, EngineSound.Pitch(40, 18), 5); // above the top speed: clamped
        Assert.Equal(1.4f, EngineSound.Pitch(-9, 18), 5); // reversing is as loud as going forward
        Assert.Equal(0.8f, EngineSound.Pitch(5, 0), 5);
    }

    [Fact]
    public void TheEngineIsSilentAboveFiveTimesSpeed()
    {
        Assert.True(EngineSound.Audible(0.01));
        Assert.True(EngineSound.Audible(1));
        Assert.True(EngineSound.Audible(5));
        Assert.False(EngineSound.Audible(10));
        Assert.False(EngineSound.Audible(double.PositiveInfinity));
    }

    [Fact]
    public void AChildKeepsItsParentsHueWithASmallShiftAndTheHueWraps()
    {
        for (int child = 0; child < 200; child++)
        {
            float hue = CarPalette.ChildHue(0.5f, 42, child);
            Assert.InRange(hue, 0.5f - CarPalette.MaxHueShift - 1e-6f, 0.5f + CarPalette.MaxHueShift + 1e-6f);
        }
        for (int child = 0; child < 200; child++)
            Assert.InRange(CarPalette.ChildHue(0.99f, 7, child), 0f, 0.99999994f);
        Assert.Equal(CarPalette.ChildHue(0.3f, 9, 4), CarPalette.ChildHue(0.3f, 9, 4)); // deterministic
        Assert.NotEqual(CarPalette.ChildHue(0.3f, 9, 4), CarPalette.ChildHue(0.3f, 9, 5));
    }

    [Fact]
    public void RandomCarColoursStayInThePlansRangeAndDifferPerCar()
    {
        var hues = new HashSet<float>();
        for (int car = 0; car < 300; car++)
        {
            CarPalette.Random(77, car, out float h, out float s, out float v);
            Assert.InRange(h, 0f, 0.99999994f);
            Assert.InRange(s, 0.6f, 0.9f);
            Assert.InRange(v, 0.8f, 1f);
            hues.Add(h);
        }
        Assert.True(hues.Count > 250);
        CarPalette.Random(77, 5, out float h1, out _, out _);
        CarPalette.Random(77, 5, out float h2, out _, out _);
        Assert.Equal(h1, h2);
    }

    [Fact]
    public void TheThreeCarModelsAndColourModesAreChoices()
    {
        var s = new AppSettings(Laptop);
        Assert.Equal(3, s.Def("carModel").Choices!.Count);
        Assert.Equal(3, s.Def("colorMode").Choices!.Count);
        Assert.Equal(CarPalette.SingleMode, (int)s.Def("colorMode").Max);
        Assert.Equal(8, CarPalette.Singles.Count);
    }

    // ---------------------------------------------------------------- the brain view's numbers

    [Fact]
    public void TheProbeSeesWhatTheCarsBrainComputes()
    {
        var shape = new BrainShape(5, 6, 4, 2);
        var population = new Population(shape, 3);
        population.Randomize(11, [0.3f, 0f]);
        var inputs = new float[] { 0.1f, -0.4f, 0.9f, 0f, 0.5f };
        var values = new float[BrainProbe.ValueCount(shape)];
        BrainProbe.Fill(shape, population.Genome(1), inputs, values);
        Assert.Equal(5 + 10 + 2, values.Length);
        Assert.Equal(inputs, values[..5]);

        var mlp = new Mlp(shape, population.Weights);
        var outputs = new float[2];
        mlp.Act(1, inputs, outputs);
        Assert.Equal(outputs, values[^2..]);
        Assert.All(values[5..], v => Assert.InRange(v, -1f, 1f));
    }
}
