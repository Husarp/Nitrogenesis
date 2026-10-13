using System.Text.Json.Nodes;
using Nitrogenesis.Sim.Map;

namespace Nitrogenesis.Sim.Gen;

/// <summary>The generator's templates (PLAN §7). The names are stored in a track's <c>meta.template</c>: never rename them.</summary>
public enum TrackTemplate { Sprint, Twisty, Labyrinth, WrongTurns, ObstacleField, Mixed }

/// <summary>One whole-number parameter of a template, as the UI shows it and the track's <c>meta.params</c> stores it.</summary>
public readonly record struct GeneratorParam(string Key, string Label, int Min, int Max, string Tip);

/// <summary>
/// A template's parameters (PLAN §7). Every template uses the map size, road width, grass verge and the target time; Wrong
/// turns adds <see cref="Branches"/>, Labyrinth <see cref="DeadEndLength"/>, Obstacle field <see cref="Obstacles"/>.
/// <see cref="Clamped"/> forces every value into its limits.
/// </summary>
public sealed record GeneratorParams
{
    public MapSize Size { get; init; } = MapSize.M;
    /// <summary>Road width, cells (the brush diameter; at least 2 × the car width).</summary>
    public int RoadWidth { get; init; } = 8;
    /// <summary>Grass verge on both sides of the road, cells (1–3).</summary>
    public int Verge { get; init; } = 2;
    /// <summary>The reference driver's time must lie within MinSeconds…MaxSeconds (PLAN §7: default 20–40 s); the route is sized for the middle.</summary>
    public int MinSeconds { get; init; } = 20;
    public int MaxSeconds { get; init; } = 40;
    /// <summary>Wrong turns: dead-end branches that point towards the finish.</summary>
    public int Branches { get; init; } = 3;
    /// <summary>Labyrinth: longest dead end, in maze cells.</summary>
    public int DeadEndLength { get; init; } = 2;
    /// <summary>Obstacle field: wall blocks and danger patches (as many as fit).</summary>
    public int Obstacles { get; init; } = 14;

    public static readonly GeneratorParam RoadWidthParam = new("roadWidth", "Road width", 4, 30, "Road width in cells (the car is 1.8 wide)");
    public static readonly GeneratorParam VergeParam = new("verge", "Grass verge", 1, 3, "Grass on both sides of the road, cells");
    public static readonly GeneratorParam MinSecondsParam = new("minSeconds", "Shortest time", 5, 300, "The reference driver must need at least this many seconds");
    public static readonly GeneratorParam MaxSecondsParam = new("maxSeconds", "Longest time", 10, 600, "… and at most this many; the route is sized for the middle");
    public static readonly GeneratorParam BranchesParam = new("branches", "Wrong turns", 0, 8, "Dead-end branches that point towards the finish");
    public static readonly GeneratorParam DeadEndParam = new("deadEndLength", "Dead-end length", 1, 6, "Longest dead end of the maze, in maze cells");
    public static readonly GeneratorParam ObstaclesParam = new("obstacles", "Obstacles", 1, 30, "Wall blocks and danger patches (as many as fit)");

    /// <summary>The template's starting values.</summary>
    public static GeneratorParams Defaults(TrackTemplate template) => template switch
    {
        TrackTemplate.Sprint => new GeneratorParams { RoadWidth = 12 },
        TrackTemplate.Labyrinth => new GeneratorParams { RoadWidth = 7 },
        TrackTemplate.ObstacleField => new GeneratorParams { RoadWidth = 20 },
        _ => new GeneratorParams(),
    };

    /// <summary>The whole-number parameters the template uses (the map size is separate), in UI order.</summary>
    public static IReadOnlyList<GeneratorParam> For(TrackTemplate template)
    {
        var list = new List<GeneratorParam> { RoadWidthParam, VergeParam };
        if (template == TrackTemplate.WrongTurns) list.Add(BranchesParam);
        if (template == TrackTemplate.Labyrinth) list.Add(DeadEndParam);
        if (template == TrackTemplate.ObstacleField) list.Add(ObstaclesParam);
        list.Add(MinSecondsParam);
        list.Add(MaxSecondsParam);
        return list;
    }

    public int Get(string key) => key switch
    {
        "roadWidth" => RoadWidth,
        "verge" => Verge,
        "minSeconds" => MinSeconds,
        "maxSeconds" => MaxSeconds,
        "branches" => Branches,
        "deadEndLength" => DeadEndLength,
        "obstacles" => Obstacles,
        _ => throw new ArgumentException($"Unknown generator parameter '{key}'.", nameof(key)),
    };

    public GeneratorParams With(string key, int value) => key switch
    {
        "roadWidth" => this with { RoadWidth = value },
        "verge" => this with { Verge = value },
        "minSeconds" => this with { MinSeconds = value },
        "maxSeconds" => this with { MaxSeconds = value },
        "branches" => this with { Branches = value },
        "deadEndLength" => this with { DeadEndLength = value },
        "obstacles" => this with { Obstacles = value },
        _ => throw new ArgumentException($"Unknown generator parameter '{key}'.", nameof(key)),
    };

    /// <summary>A copy with every value inside its limits; the longest time is at least 5 s above the shortest.</summary>
    public GeneratorParams Clamped()
    {
        var p = this;
        foreach (var info in new[] { RoadWidthParam, VergeParam, MinSecondsParam, MaxSecondsParam, BranchesParam, DeadEndParam, ObstaclesParam })
            p = p.With(info.Key, Math.Clamp(p.Get(info.Key), info.Min, info.Max));
        return p with
        {
            Size = Enum.IsDefined(Size) ? Size : MapSize.M,
            MaxSeconds = Math.Max(p.MaxSeconds, p.MinSeconds + 5),
        };
    }

    /// <summary>The parameters the template uses, for <c>meta.params</c>: <c>{size: "M", roadWidth: 8, …}</c>.</summary>
    public JsonObject ToJson(TrackTemplate template)
    {
        var json = new JsonObject { ["size"] = Size.ToString() };
        foreach (var info in For(template)) json[info.Key] = Get(info.Key);
        return json;
    }

    /// <summary>
    /// Reads <c>meta.params</c> back: the template's defaults, overridden by every known key that holds a whole number (the size
    /// as "S", "M" or "L"), then clamped. Unknown keys and values of the wrong type are ignored, so a hand-edited file still works.
    /// </summary>
    public static GeneratorParams FromJson(TrackTemplate template, JsonObject? json)
    {
        var p = Defaults(template);
        if (json is null) return p;
        if (json["size"] is JsonValue size && size.TryGetValue(out string? s) && Enum.TryParse<MapSize>(s, true, out var mapSize) && Enum.IsDefined(mapSize))
            p = p with { Size = mapSize };
        foreach (var info in For(template))
            if (json[info.Key] is JsonValue value && value.TryGetValue(out int v))
                p = p.With(info.Key, v);
        return p.Clamped();
    }
}

/// <summary>Names and one-line descriptions of the templates (PLAN §7 table).</summary>
public static class Templates
{
    public static readonly TrackTemplate[] All = Enum.GetValues<TrackTemplate>();

    public static string Name(TrackTemplate template) => template switch
    {
        TrackTemplate.WrongTurns => "Wrong turns",
        TrackTemplate.ObstacleField => "Obstacle field",
        _ => template.ToString(),
    };

    public static string Description(TrackTemplate template) => template switch
    {
        TrackTemplate.Sprint => "Mostly straight, gentle curves, wide road",
        TrackTemplate.Twisty => "Many turns, medium width",
        TrackTemplate.Labyrinth => "A maze with many dead ends",
        TrackTemplate.WrongTurns => "A route with dead-end branches that look like shortcuts",
        TrackTemplate.ObstacleField => "Wide road with wall blocks and danger patches",
        TrackTemplate.Mixed => "A random mix of the other templates' pieces",
        _ => "",
    };

    /// <summary>The template stored in a track's meta, or null when it has none (or an unknown one).</summary>
    public static TrackTemplate? FromMeta(TrackMeta meta) =>
        meta.Template is { } name && Enum.TryParse<TrackTemplate>(name, false, out var t) && Enum.IsDefined(t) ? t : null;
}
