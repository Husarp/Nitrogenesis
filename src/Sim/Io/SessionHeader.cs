using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Nitrogenesis.Sim.Brain;
using Nitrogenesis.Sim.Core;
using Nitrogenesis.Sim.Evolution;
using Nitrogenesis.Sim.Map;
using Nitrogenesis.Sim.Racing;

namespace Nitrogenesis.Sim.Io;

/// <summary>One history segment: a run of generations under one <see cref="SimConfig"/> (PLAN §5).</summary>
/// <param name="Id">Segment number, unique in the session.</param>
/// <param name="Branch">Branch the segment belongs to.</param>
/// <param name="FirstGeneration">First generation trained under this config.</param>
/// <param name="Config">The config. Its <see cref="SimConfig.Hash"/> identifies the segment.</param>
/// <param name="Track">A copy of the track, so the session still works when the track file changes or goes away.</param>
public sealed record SegmentEntry(int Id, string Branch, int FirstGeneration, SimConfig Config, Track Track);

/// <summary>A training branch (PLAN §5, "Fork").</summary>
/// <param name="Name">Folder-safe name (letters, digits, '-', '_').</param>
/// <param name="Parent">Branch it was forked from, or null for the first branch.</param>
/// <param name="ForkGeneration">Generation of the parent it was forked from, or null.</param>
/// <param name="Seed">Training seed of the branch (generation g uses <c>SeedHash.Derive(Seed, g)</c>).</param>
public sealed record BranchEntry(string Name, string? Parent, int? ForkGeneration, ulong Seed);

/// <summary>The population to continue from: generation <see cref="Generation"/> of <see cref="Branch"/>, not yet evaluated.</summary>
/// <param name="Params">Evolution parameters that bred it (they go into its GenerationRecord).</param>
public sealed record PopulationEntry(string Branch, int Generation, int Segment, EvolutionParams Params, Population Population);

/// <summary>
/// What a SimVersion mismatch allows (PLAN §5.1): recordings always play; re-simulation, fork and resume
/// need the same physics version.
/// </summary>
public readonly record struct SessionCompatibility(int SessionSimVersion)
{
    public bool SimVersionMatches => SessionSimVersion == SimInfo.SimVersion;
    public bool CanPlayRecordings => true;
    public bool CanResimulate => SimVersionMatches;
    public bool CanFork => SimVersionMatches;
    public bool CanResume => SimVersionMatches;

    /// <summary>Why re-simulation, fork and resume are disabled, or null when they are allowed.</summary>
    public string? Message => SimVersionMatches ? null
        : SessionSimVersion < SimInfo.SimVersion
            ? "This session was made with an older physics version. Recordings still play; watching a whole generation, forking and resuming are disabled."
            : "This session was made with a newer physics version. Recordings still play; watching a whole generation, forking and resuming are disabled.";
}

/// <summary>
/// <c>header.json</c> of a session folder (PLAN §5.1): format version, SimVersion, current branch, the segment
/// list (each with its SimConfig, track hash and a track copy), the branch table, the evolution settings and
/// the current population.
/// </summary>
/// <remarks>
/// The stagnation state is not stored: it follows from the generation stats in the history
/// (<see cref="StagnationTracker"/> replays them). Weights are base64 of little-endian float32.
/// </remarks>
public sealed class SessionHeader
{
    public const int FormatVersion = 1;

    /// <summary>SimVersion the session was trained with.</summary>
    public int SimVersion { get; init; } = SimInfo.SimVersion;
    public string CurrentBranch { get; set; } = "main";
    public List<SegmentEntry> Segments { get; init; } = [];
    public List<BranchEntry> Branches { get; init; } = [];
    public EvolutionSettings Evolution { get; set; } = new();
    public PopulationEntry? Population { get; set; }

    public SessionCompatibility Compatibility => new(SimVersion);

    private static readonly JsonSerializerOptions ValueOptions = new()
    {
        IgnoreReadOnlyProperties = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public string Serialize()
    {
        var root = new JsonObject
        {
            ["formatVersion"] = FormatVersion,
            ["simVersion"] = SimVersion,
            ["currentBranch"] = CurrentBranch,
            ["evolution"] = JsonSerializer.SerializeToNode(Evolution, ValueOptions),
            ["segments"] = new JsonArray(Segments.Select(WriteSegment).ToArray<JsonNode?>()),
            ["branches"] = new JsonArray(Branches.Select(WriteBranch).ToArray<JsonNode?>()),
        };
        if (Population is { } p)
        {
            root["population"] = new JsonObject
            {
                ["branch"] = p.Branch,
                ["generation"] = p.Generation,
                ["segment"] = p.Segment,
                ["params"] = JsonSerializer.SerializeToNode(p.Params, ValueOptions),
                ["brain"] = WriteShape(p.Population.Shape),
                ["weights"] = Convert.ToBase64String(FloatBytes(p.Population.Weights)),
            };
        }
        return root.ToJsonString(JsonFields.Indented);
    }

    /// <summary>
    /// Parses a header. Throws <see cref="InvalidDataException"/> on any problem. A header from another
    /// SimVersion still loads (see <see cref="Compatibility"/>); for the current one every segment's stored
    /// config hash must match the recomputed one.
    /// </summary>
    public static SessionHeader Deserialize(string json)
    {
        const string what = "Session header";
        JsonObject root = JsonFields.ParseObject(json, what);
        int version = JsonFields.Required<int>(root, "formatVersion", what);
        if (version < 1 || version > FormatVersion)
            throw new InvalidDataException($"{what}: format version {version} is not supported (this app reads up to {FormatVersion}).");
        try
        {
            int simVersion = JsonFields.Required<int>(root, "simVersion", what);
            var header = new SessionHeader
            {
                SimVersion = simVersion,
                CurrentBranch = JsonFields.Required<string>(root, "currentBranch", what),
                Evolution = (Value<EvolutionSettings>(root, "evolution") ?? new EvolutionSettings()).Clamped(),
                Segments = Array(root, "segments").Select(n => ReadSegment(Object(n), simVersion)).ToList(),
                Branches = Array(root, "branches").Select(n => ReadBranch(Object(n))).ToList(),
            };
            if (root["population"] is JsonObject pop)
            {
                BrainShape shape = ReadShape(Object(pop["brain"]));
                float[] weights = FromFloatBytes(Convert.FromBase64String(JsonFields.Required<string>(pop, "weights", what)));
                header.Population = new PopulationEntry(
                    JsonFields.Required<string>(pop, "branch", what),
                    JsonFields.Required<int>(pop, "generation", what),
                    JsonFields.Required<int>(pop, "segment", what),
                    Value<EvolutionParams>(pop, "params"),
                    new Population(shape, weights));
            }
            return header;
        }
        catch (Exception e) when (e is JsonException or FormatException or ArgumentException or NotSupportedException)
        {
            throw new InvalidDataException($"{what} holds an invalid value: {e.Message}", e);
        }
    }

    private static JsonObject WriteSegment(SegmentEntry s) => new()
    {
        ["id"] = s.Id,
        ["branch"] = s.Branch,
        ["firstGeneration"] = s.FirstGeneration,
        ["configHash"] = s.Config.Hash,
        ["simVersion"] = s.Config.SimVersion,
        ["mode"] = s.Config.Mode.ModeId,
        ["settings"] = JsonSerializer.SerializeToNode(s.Config.Mode, s.Config.Mode.GetType(), ValueOptions),
        ["brain"] = WriteShape(s.Config.Brain),
        ["timeLimitTicks"] = s.Config.TimeLimitTicks,
        ["trackHash"] = s.Config.TrackHash,
        ["track"] = JsonNode.Parse(TrackFile.Serialize(s.Track)),
    };

    private static SegmentEntry ReadSegment(JsonObject o, int sessionSimVersion)
    {
        const string what = "Session segment";
        string modeId = JsonFields.Required<string>(o, "mode", what);
        IModeSettings settings = modeId switch
        {
            "racing" => Value<RacingSettings>(o, "settings") ?? throw new InvalidDataException($"{what}: settings are missing."),
            _ => throw new InvalidDataException($"{what}: unknown mode '{modeId}'."),
        };
        int simVersion = JsonFields.Required<int>(o, "simVersion", what);
        var config = new SimConfig(JsonFields.Required<string>(o, "trackHash", what), settings, ReadShape(Object(o["brain"])),
            JsonFields.Required<int>(o, "timeLimitTicks", what), simVersion);
        string storedHash = JsonFields.Required<string>(o, "configHash", what);
        if (simVersion == SimInfo.SimVersion && sessionSimVersion == SimInfo.SimVersion && config.Hash != storedHash)
            throw new InvalidDataException($"{what}: stored config hash does not match its settings.");
        Track track = TrackFile.Deserialize(Object(o["track"]).ToJsonString());
        if (TrackHash.Compute(track) != config.TrackHash) throw new InvalidDataException($"{what}: the track copy does not match its track hash.");
        return new SegmentEntry(JsonFields.Required<int>(o, "id", what), JsonFields.Required<string>(o, "branch", what),
            JsonFields.Required<int>(o, "firstGeneration", what), config, track);
    }

    private static JsonObject WriteBranch(BranchEntry b) => new()
    {
        ["name"] = b.Name,
        ["parent"] = b.Parent,
        ["forkGeneration"] = b.ForkGeneration,
        ["seed"] = b.Seed.ToString("x16"), // as text: JSON numbers lose precision above 2^53
    };

    private static BranchEntry ReadBranch(JsonObject o)
    {
        const string what = "Session branch";
        string seed = JsonFields.Required<string>(o, "seed", what);
        return new BranchEntry(SessionFolder.CheckBranchName(JsonFields.Required<string>(o, "name", what)),
            JsonFields.Optional<string>(o, "parent", what), o["forkGeneration"] is null ? null : JsonFields.Required<int>(o, "forkGeneration", what),
            ulong.Parse(seed, System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture));
    }

    private static JsonObject WriteShape(BrainShape s) => new()
    {
        ["inputs"] = s.Inputs,
        ["hidden1"] = s.Hidden1,
        ["hidden2"] = s.Hidden2,
        ["outputs"] = s.Outputs,
    };

    private static BrainShape ReadShape(JsonObject o)
    {
        const string what = "Brain shape";
        return new BrainShape(JsonFields.Required<int>(o, "inputs", what), JsonFields.Required<int>(o, "hidden1", what),
            JsonFields.Required<int>(o, "hidden2", what), JsonFields.Required<int>(o, "outputs", what));
    }

    private static T? Value<T>(JsonObject o, string key) => o[key] is { } node ? node.Deserialize<T>(ValueOptions) : default;

    private static JsonArray Array(JsonObject o, string key) =>
        o[key] as JsonArray ?? throw new InvalidDataException($"Session header: '{key}' must be an array.");

    private static JsonObject Object(JsonNode? node) =>
        node as JsonObject ?? throw new InvalidDataException("Session header: an object was expected.");

    private static byte[] FloatBytes(float[] values)
    {
        var bytes = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
            System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), values[i]);
        return bytes;
    }

    private static float[] FromFloatBytes(byte[] bytes)
    {
        if (bytes.Length % 4 != 0) throw new InvalidDataException("Session header: weights are not whole float32 values.");
        var values = new float[bytes.Length / 4];
        for (int i = 0; i < values.Length; i++)
            values[i] = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * 4));
        return values;
    }
}
