using System.Globalization;
using Nitrogenesis.Sim.Brain;
using Nitrogenesis.Sim.Core;
using Nitrogenesis.Sim.Evolution;
using Nitrogenesis.Sim.Io;
using Nitrogenesis.Sim.Racing;

namespace Nitrogenesis.Sim.Settings;

/// <summary>
/// The values of every setting in <see cref="SettingsCatalog"/> (PLAN §6.4): clamped on every change, reset to the
/// default one by one or by preset, and saved in <c>settings.json</c> (<see cref="SettingsFile"/>: temp file and rename).
/// Only values that differ from the default are written, so a changed default reaches everyone who never touched the
/// setting. A <see cref="ForTraining"/> copy is not saved: it holds a running training's own values for the Settings
/// screen opened over it.
/// </summary>
public sealed class AppSettings
{
    private readonly Dictionary<string, SettingDef> _defs = new();
    private readonly Dictionary<string, double> _values = new();
    private readonly SettingsFile _file;
    private readonly string? _path;

    public AppSettings(MachineInfo machine) : this(machine, new SettingsFile(), null) { }

    private AppSettings(MachineInfo machine, SettingsFile file, string? path)
    {
        Machine = machine;
        _file = file;
        _path = path;
        Defs = SettingsCatalog.Create(machine);
        foreach (var def in Defs)
        {
            _defs[def.Key] = def;
            _values[def.Key] = Read(def);
        }
    }

    public MachineInfo Machine { get; }
    public IReadOnlyList<SettingDef> Defs { get; }

    /// <summary>Goes up with every change, so a view can notice cheaply.</summary>
    public int Version { get; private set; }

    /// <summary>
    /// Loads <paramref name="path"/>; a missing file gives the defaults, and so does a damaged one (kept as
    /// <c>settings.json.bad</c>, <paramref name="problem"/> says why).
    /// </summary>
    public static AppSettings Load(string path, MachineInfo machine, out string? problem)
    {
        problem = null;
        try
        {
            return new AppSettings(machine, SettingsFile.Load(path), path);
        }
        // ArgumentException: System.Text.Json refuses a key that appears twice only when the object is first used.
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            problem = $"The settings file could not be read ({e.Message}); defaults are used.";
            try
            {
                File.Move(path, path + ".bad", overwrite: true);
            }
            catch (Exception move) when (move is IOException or UnauthorizedAccessException)
            {
                // the broken file stays; the next save replaces it
            }
            return new AppSettings(machine, new SettingsFile(), path);
        }
    }

    /// <summary>A copy of a running training's values, not saved (the Settings screen over a training).</summary>
    public static AppSettings ForTraining(MachineInfo machine, EvolutionSettings evolution, RacingSettings racing, int populationSize, BrainShape brain, double speed)
    {
        var s = new AppSettings(machine);
        s.Set("speed", SpeedController.IndexFor(speed));
        s.Set("mutationSigma", evolution.MutationSigma);
        s.Set("mutationChancePercent", evolution.MutationChance * 100);
        s.Set("elitismPercent", evolution.ElitismFraction * 100);
        s.Set("randomPercent", evolution.RandomFraction * 100);
        s.Set("tournamentSize", evolution.TournamentSize);
        s.Set("crossoverPercent", evolution.CrossoverRate * 100);
        s.Set("stagnationBoost", evolution.StagnationBoost ? 1 : 0);
        s.Set("stagnationWindow", evolution.StagnationWindow);
        s.Set("populationSize", populationSize);
        s.Set("maxSpeed", racing.MaxSpeed);
        s.Set("acceleration", racing.Acceleration);
        s.Set("turnRateDeg", racing.TurnRateDeg);
        s.Set("grassSpeedPercent", racing.GrassSpeedFraction * 100);
        s.Set("grassGrip", racing.GrassGrip);
        s.Set("walls", (int)racing.Walls);
        s.Set("timeLimitSeconds", racing.TimeLimitSeconds);
        s.Set("stallSeconds", racing.StallSeconds);
        s.Set("rayCount", racing.RayCount);
        s.Set("raySpreadDeg", racing.RaySpreadDeg);
        s.Set("rayRange", racing.RayRange);
        s.Set("memoryInputs", racing.MemoryInputs ? 1 : 0);
        s.Set("directionHint", racing.DirectionHint ? 1 : 0);
        s.Set("hidden1", brain.Hidden1);
        s.Set("hidden2", brain.Hidden2);
        return s;
    }

    public SettingDef Def(string key) => _defs.TryGetValue(key, out var d) ? d : throw new ArgumentException($"No setting '{key}'.", nameof(key));

    public double Get(string key) => _values[Def(key).Key];

    public bool Flag(string key) => Get(key) >= 0.5;

    public int Index(string key) => (int)Get(key);

    /// <summary>Sets a value (clamped to its range). Returns false when it did not change.</summary>
    public bool Set(string key, double value)
    {
        var def = Def(key);
        double v = Round(def.Clamp(value));
        if (Same(_values[key], v)) return false;
        _values[key] = v;
        Store(def, v);
        Version++;
        return true;
    }

    public bool IsDefault(string key) => Same(_values[key], Round(Def(key).Default));

    /// <summary>Back to the default; returns false when it already was.</summary>
    public bool Reset(string key) => Set(key, Def(key).Default);

    /// <summary>Whether a setting of this kind may be changed now: a new training changes everything, a running one not <see cref="SettingKind.NewTraining"/> (PLAN §6.4).</summary>
    public static bool CanEdit(SettingKind kind, bool inTraining) => kind != SettingKind.NewTraining || !inTraining;

    /// <summary>
    /// Applies a preset to the training-scope settings the situation allows (see <see cref="CanEdit"/>): its own values, or
    /// the defaults for the ones it does not name. Returns the keys that changed.
    /// </summary>
    public IReadOnlyList<string> ApplyPreset(SettingsPreset preset, bool inTraining)
    {
        var overrides = SettingsPresets.Overrides(preset);
        var changed = new List<string>();
        foreach (var def in Defs)
        {
            if (!SettingsCatalog.IsPresetKey(def) || !CanEdit(def.Kind, inTraining)) continue;
            if (Set(def.Key, overrides.TryGetValue(def.Key, out double v) ? v : def.Default)) changed.Add(def.Key);
        }
        return changed;
    }

    /// <summary>Writes settings.json (a temp file, then a rename). Nothing for a copy that has no file.</summary>
    public void Save()
    {
        if (_path is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        _file.Save(_path);
    }

    // ---- typed views ----

    public RacingSettings Racing => new RacingSettings
    {
        MaxSpeed = (float)Get("maxSpeed"),
        Acceleration = (float)Get("acceleration"),
        TurnRateDeg = (float)Get("turnRateDeg"),
        GrassSpeedFraction = (float)(Get("grassSpeedPercent") / 100),
        GrassGrip = (float)Get("grassGrip"),
        Walls = (WallMode)Index("walls"),
        RayCount = (int)Get("rayCount"),
        RaySpreadDeg = (float)Get("raySpreadDeg"),
        RayRange = (float)Get("rayRange"),
        MemoryInputs = Flag("memoryInputs"),
        DirectionHint = Flag("directionHint"),
        StallSeconds = (float)Get("stallSeconds"),
        TimeLimitSeconds = (float)Get("timeLimitSeconds"),
    }.Clamped();

    public EvolutionSettings Evolution => new EvolutionSettings
    {
        MutationSigma = (float)Get("mutationSigma"),
        MutationChance = (float)(Get("mutationChancePercent") / 100),
        ElitismFraction = (float)(Get("elitismPercent") / 100),
        RandomFraction = (float)(Get("randomPercent") / 100),
        TournamentSize = (int)Get("tournamentSize"),
        CrossoverRate = (float)(Get("crossoverPercent") / 100),
        StagnationBoost = Flag("stagnationBoost"),
        StagnationWindow = (int)Get("stagnationWindow"),
    }.Clamped();

    public int PopulationSize => (int)Get("populationSize");
    public int Hidden1 => (int)Get("hidden1");
    public int Hidden2 => (int)Get("hidden2");
    public double Speed => SpeedController.StepValue(Index("speed"));

    /// <summary>Simulation threads with visuals (auto = physical cores − 1).</summary>
    public int VisualThreads => Get("threads") is > 0 and var t ? (int)t : Machine.VisualThreads;
    /// <summary>Simulation threads without visuals (auto = logical threads − 1).</summary>
    public int HeadlessThreads => Get("headlessThreads") is > 0 and var t ? (int)t : Machine.HeadlessThreads;
    public long HistoryLimitBytes => (long)Get("historyMb") * 1024 * 1024;
    /// <summary>One of <see cref="SettingsCatalog.PriorityAuto"/>, <see cref="SettingsCatalog.PriorityNormal"/>, <see cref="SettingsCatalog.PriorityBelowNormal"/>.</summary>
    public int Priority => Index("priority");

    public int FpsCapIndex => Index("fpsCap");
    public bool VSync => Flag("vsync");
    public GraphicsQuality Quality => GraphicsQuality.For(Index("graphicsQuality"));
    public int CarModel => Index("carModel");
    public int ColorMode => Index("colorMode");
    public int CarColor => Index("carColor");
    public bool UiSound => Flag("soundUi");
    public bool EngineSoundOn => Flag("soundEngine");
    public float UiVolume => (float)(Get("volumeUi") / 100);
    public float EngineVolume => (float)(Get("volumeEngine") / 100);

    // ---- storage ----

    private double Read(SettingDef def)
    {
        double value = def.Type switch
        {
            SettingType.Bool => _file.GetBool(def.Key, def.Default >= 0.5) ? 1 : 0,
            SettingType.Choice => ChoiceIndex(def, _file.GetString(def.Key, def.Choices![(int)def.Default])),
            _ => _file.GetDouble(def.Key, def.Default),
        };
        return Round(def.Clamp(value));
    }

    private static double ChoiceIndex(SettingDef def, string text)
    {
        for (int i = 0; i < def.Choices!.Count; i++)
            if (string.Equals(def.Choices[i], text, StringComparison.OrdinalIgnoreCase)) return i;
        return def.Default;
    }

    private void Store(SettingDef def, double value)
    {
        if (Same(value, Round(def.Default)))
        {
            _file.Remove(def.Key);
            return;
        }
        switch (def.Type)
        {
            case SettingType.Bool: _file.Set(def.Key, value >= 0.5); break;
            case SettingType.Choice: _file.Set(def.Key, def.Choices![(int)value]); break;
            case SettingType.Int: _file.Set(def.Key, (int)value); break;
            default: _file.Set(def.Key, value); break;
        }
    }

    private static double Round(double v) => Math.Round(v, 6);

    private static bool Same(double a, double b) => Math.Abs(a - b) < 1e-6;
}
