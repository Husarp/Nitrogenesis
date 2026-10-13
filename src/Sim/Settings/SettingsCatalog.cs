using Nitrogenesis.Sim.Brain;
using Nitrogenesis.Sim.Core;
using Nitrogenesis.Sim.Evolution;
using Nitrogenesis.Sim.Racing;

namespace Nitrogenesis.Sim.Settings;

/// <summary>Every setting of PLAN §6.4 with its kind, range, default and tooltip.</summary>
public static class SettingsCatalog
{
    public const string Training = "Training", Evolution = "Evolution", Population = "Population", Physics = "Car physics",
        Sensors = "Sensors and brain", Graphics = "Graphics", Performance = "Performance", Sound = "Sound";

    public static readonly IReadOnlyList<string> Sections = [Training, Evolution, Population, Physics, Sensors, Graphics, Performance, Sound];

    public static readonly IReadOnlyList<string> CarModels = ["Hatchback", "Racer", "Truck"];
    public static readonly IReadOnlyList<string> Priorities = ["Auto", "Normal", "Below normal"];
    public const int PriorityAuto = 0, PriorityNormal = 1, PriorityBelowNormal = 2;

    /// <summary>The definitions for a machine (the thread and memory limits depend on it), in screen order.</summary>
    public static IReadOnlyList<SettingDef> Create(MachineInfo machine)
    {
        var d = new RacingSettings();
        var e = new EvolutionSettings();
        var speeds = Enumerable.Range(0, SpeedController.MaxIndex + 1).Select(i => SpeedController.Label(SpeedController.StepValue(i))).ToArray();
        string[] walls = Enum.GetNames<WallMode>();
        const SettingKind live = SettingKind.Live, segment = SettingKind.NewSegment, fresh = SettingKind.NewTraining;
        const SettingScope app = SettingScope.App, train = SettingScope.Training;
        const SettingType b = SettingType.Bool, i32 = SettingType.Int, f = SettingType.Float, c = SettingType.Choice;
        int hMax = Math.Max(machine.MaxHistoryMb, MachineInfo.MinHistoryMb);

        return
        [
            new("speed", "Start speed", Training, live, train, c, SpeedController.DefaultIndex, 0, speeds.Length - 1, 1,
                "The speed a training runs at. 100× and MAX simulate faster than real time; the bar at the top shows the actual speed.", speeds),

            new("mutationSigma", "Mutation strength (σ)", Evolution, live, train, f, e.MutationSigma, EvolutionSettings.MinMutationSigma, EvolutionSettings.MaxMutationSigma, 0.01,
                "How far a mutated weight moves. Bigger = bolder changes. Takes effect at the next generation."),
            new("mutationChancePercent", "Mutation chance", Evolution, live, train, f, e.MutationChance * 100, 0, 100, 0.5,
                "The chance that each weight of a child is mutated. Takes effect at the next generation.", Unit: "%"),
            new("elitismPercent", "Elitism", Evolution, live, train, f, e.ElitismFraction * 100, 0, EvolutionSettings.MaxElitismFraction * 100, 1,
                "The best share of each generation is copied unchanged into the next one (at least one car).", Unit: "%"),
            new("randomPercent", "New random cars", Evolution, live, train, f, e.RandomFraction * 100, 0, EvolutionSettings.MaxRandomFraction * 100, 1,
                "Share of each generation replaced by brand-new random cars. Keeps fresh ideas coming.", Unit: "%"),
            new("tournamentSize", "Tournament size", Evolution, live, train, i32, e.TournamentSize, EvolutionSettings.MinTournamentSize, EvolutionSettings.MaxTournamentSize, 1,
                "Parents are picked as the best of this many random cars. 1 = pure chance, bigger = only the best breed."),
            new("crossoverPercent", "Crossover", Evolution, live, train, f, e.CrossoverRate * 100, 0, 100, 5,
                "The chance that a child mixes the weights of two parents instead of copying one.", Unit: "%"),
            new("stagnationBoost", "Boost when stuck", Evolution, live, train, b, 1, 0, 1, 1,
                "When progress stalls, mutation is doubled for 5 generations (a banner tells you)."),
            new("stagnationWindow", "Stuck after", Evolution, live, train, i32, e.StagnationWindow, EvolutionSettings.MinStagnationWindow, EvolutionSettings.MaxStagnationWindow, 1,
                "Generations without enough improvement before the boost starts.", Unit: "generations"),

            new("populationSize", "Cars per generation", Population, segment, train, i32, EvolutionSettings.DefaultPopulationSize, 10, 1000, 10,
                "More cars explore more, but each generation takes longer. In a running training the extra cars are dropped, or mutated copies of the population are added, and a new segment starts."),

            new("maxSpeed", "Top speed", Physics, segment, train, f, d.MaxSpeed, RacingSettings.MinMaxSpeed, RacingSettings.MaxMaxSpeed, 1,
                "Fastest a car goes on the road.", Unit: "cells/s"),
            new("acceleration", "Acceleration", Physics, segment, train, f, d.Acceleration, RacingSettings.MinAcceleration, RacingSettings.MaxAcceleration, 1,
                "How quickly a car speeds up at full throttle.", Unit: "cells/s²"),
            new("turnRateDeg", "Turn rate", Physics, segment, train, f, d.TurnRateDeg, RacingSettings.MinTurnRateDeg, RacingSettings.MaxTurnRateDeg, 10,
                "How fast a car can turn at speed, at full steering.", Unit: "°/s"),
            new("grassSpeedPercent", "Grass top speed", Physics, segment, train, f, d.GrassSpeedFraction * 100, RacingSettings.MinGrassFraction * 100, RacingSettings.MaxGrassFraction * 100, 5,
                "Top speed on grass as a share of the road top speed. 100 % = grass is as fast as road.", Unit: "%"),
            new("grassGrip", "Grass grip", Physics, segment, train, f, d.GrassGrip, RacingSettings.MinGrassFraction, RacingSettings.MaxGrassFraction, 0.05,
                "How well the tyres hold on grass. Low = the car slides sideways."),
            new("walls", "Walls", Physics, segment, train, c, (double)d.Walls, 0, walls.Length - 1, 1,
                "Block: the car slides along a wall. Bounce: it bounces off. Kill: touching a wall ends the run.", walls),
            new("timeLimitSeconds", "Time limit", Physics, segment, train, f, 0, RacingSettings.MinTimeLimitSeconds, RacingSettings.MaxTimeLimitSeconds, 1,
                "Seconds each generation may run. Auto = 2.5 × the time of a scripted reference driver.", Unit: "s", ZeroMeans: "auto"),
            new("stallSeconds", "Stall time", Physics, segment, train, f, d.StallSeconds, RacingSettings.MinStallSeconds, RacingSettings.MaxStallSeconds, 0.5,
                "A car that makes no progress for this long is stopped.", Unit: "s"),

            new("rayCount", "Rays", Sensors, fresh, train, i32, d.RayCount, RacingSettings.MinRayCount, RacingSettings.MaxRayCount, 1,
                "How many distance sensors a car has, spread in front of it."),
            new("raySpreadDeg", "Ray spread", Sensors, fresh, train, f, d.RaySpreadDeg, RacingSettings.MinRaySpreadDeg, RacingSettings.MaxRaySpreadDeg, 5,
                "The angle the rays cover, centred on the car's heading.", Unit: "°"),
            new("rayRange", "Ray length", Sensors, fresh, train, f, d.RayRange, RacingSettings.MinRayRange, RacingSettings.MaxRayRange, 1,
                "How far a ray sees.", Unit: "cells"),
            new("memoryInputs", "Memory inputs", Sensors, fresh, train, b, 1, 0, 1, 1,
                "Gives the brain its last throttle and steering, the elapsed time and the distance driven. Helps on mazes."),
            new("directionHint", "Direction hint", Sensors, fresh, train, b, 0, 0, 1, 1,
                "Tells the brain which way the finish is. Makes learning much easier, but is less of a challenge."),
            new("hidden1", "Hidden neurons", Sensors, fresh, train, i32, BrainShape.DefaultHidden, BrainShape.MinHidden, BrainShape.MaxHidden, 1,
                "Size of the brain's first hidden layer."),
            new("hidden2", "Second hidden layer", Sensors, fresh, train, i32, 0, BrainShape.MinHidden, BrainShape.MaxHidden, 1,
                "Neurons in an optional second hidden layer. Off = a single layer.", ZeroMeans: "off"),

            new("fpsCap", "FPS cap", Graphics, live, app, c, FpsCap.DefaultIndex, 0, FpsCap.Labels.Count - 1, 1,
                "The most frames drawn per second. A lower cap saves power. An unfocused window always draws 15 per second.", FpsCap.Labels),
            new("vsync", "VSync", Graphics, live, app, b, 1, 0, 1, 1,
                "Syncs drawing with the monitor so frames never tear. Turn it off if motion looks uneven at your cap."),
            new("graphicsQuality", "Graphics quality", Graphics, live, app, c, GraphicsQuality.DefaultLevel, 0, 1, 1,
                "Low: no route trail, no rays, plain lines, at most 200 cars drawn. High: trail, rays, smooth lines, up to 1000 cars.", GraphicsQuality.Labels),
            new("carModel", "Car model", Graphics, live, app, c, 0, 0, CarModels.Count - 1, 1,
                "How the cars look. Only the picture changes, never the physics.", CarModels),
            new("colorMode", "Car colours", Graphics, live, app, c, CarPalette.RandomMode, 0, CarPalette.ModeLabels.Count - 1, 1,
                "Random: every car its own colour. Per family: a child keeps its parent's colour with a small shift. Single colour: all cars alike.", CarPalette.ModeLabels),
            new("carColor", "Single colour", Graphics, live, app, c, 0, 0, CarPalette.Singles.Count - 1, 1,
                "The colour used when Car colours is Single colour.", CarPalette.Singles.Select(s => s.Name).ToArray()),

            new("threads", "Simulation threads", Performance, live, app, i32, 0, 1, machine.LogicalCores, 1,
                $"Threads that simulate the cars while you watch. Auto = physical cores − 1 ({machine.VisualThreads} here). Fewer threads leave more of the computer to other programs.", ZeroMeans: $"auto ({machine.VisualThreads})"),
            new("headlessThreads", "Threads without visuals", Performance, live, app, i32, 0, 1, machine.LogicalCores, 1,
                $"Threads used by Train to… (the map is not drawn). Auto = logical threads − 1 ({machine.HeadlessThreads} here).", ZeroMeans: $"auto ({machine.HeadlessThreads})"),
            new("priority", "Process priority", Performance, live, app, c, PriorityAuto, 0, Priorities.Count - 1, 1,
                "Below normal lets other programs go first. Auto: Below normal while training without visuals, Normal otherwise. Windows only.", Priorities),
            new("historyMb", "History memory limit", Performance, live, app, i32, Math.Min(MachineInfo.DefaultHistoryMb, hMax), MachineInfo.MinHistoryMb, hMax, 64,
                "Memory the training history may use (at most 75 % of your RAM). When it is full the oldest full-population snapshots are thinned; best cars and stats are always kept. More memory does not make training faster.", Unit: "MB"),

            new("soundUi", "Interface sounds", Sound, live, app, b, 1, 0, 1, 1, "A small click when you press a button."),
            new("soundEngine", "Engine sound", Sound, live, app, b, 1, 0, 1, 1,
                "The engine of the followed car, or of your car when you drive. Silent above 5× speed."),
            new("volumeUi", "Interface volume", Sound, live, app, i32, 70, 0, 100, 5, "How loud the interface clicks are.", Unit: "%"),
            new("volumeEngine", "Engine volume", Sound, live, app, i32, 60, 0, 100, 5, "How loud the engine is.", Unit: "%"),
        ];
    }

    /// <summary>Which keys the presets touch: the training-scope ones except the speed.</summary>
    public static bool IsPresetKey(SettingDef def) => def.Scope == SettingScope.Training && def.Key != "speed";
}
