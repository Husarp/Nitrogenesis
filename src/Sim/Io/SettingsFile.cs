using System.Text.Json.Nodes;

namespace Nitrogenesis.Sim.Io;

/// <summary>
/// <c>settings.json</c> (PLAN §5.1): <c>{formatVersion, values:{key:value}}</c>. Readers pass the default with
/// every lookup, so a missing key, or a value of the wrong type, falls back to that default. Unknown keys
/// are kept and written back, so settings from a newer version survive a round trip through an older one.
/// </summary>
public sealed class SettingsFile
{
    public const int FormatVersion = 1;

    private readonly JsonObject _values;

    public SettingsFile() : this(new JsonObject()) { }

    private SettingsFile(JsonObject values) => _values = values;

    public IEnumerable<string> Keys => _values.Select(p => p.Key);

    public bool Contains(string key) => _values.ContainsKey(key);

    public int GetInt(string key, int fallback) => TryGet(key, out int v) ? v : fallback;
    public double GetDouble(string key, double fallback) => TryGet(key, out double v) ? v : fallback;
    public bool GetBool(string key, bool fallback) => TryGet(key, out bool v) ? v : fallback;
    public string GetString(string key, string fallback) => TryGet(key, out string? v) && v is not null ? v : fallback;

    public void Set(string key, int value) => _values[key] = value;
    public void Set(string key, double value) => _values[key] = value;
    public void Set(string key, bool value) => _values[key] = value;
    public void Set(string key, string value) => _values[key] = value;

    /// <summary>Removes a key, so it falls back to its default ("reset to default").</summary>
    public bool Remove(string key) => _values.Remove(key);

    private bool TryGet<T>(string key, out T? value)
    {
        value = default;
        return _values[key] is JsonValue v && v.TryGetValue(out value);
    }

    /// <summary>Loads settings; a missing file gives empty settings (all defaults).</summary>
    /// <exception cref="InvalidDataException">The file exists but is not a valid settings file.</exception>
    public static SettingsFile Load(string path) =>
        File.Exists(path) ? Deserialize(File.ReadAllText(path)) : new SettingsFile();

    /// <summary>Writes via a temp file and a rename, so a crash never leaves a half-written settings file.</summary>
    public void Save(string path)
    {
        string temp = path + ".tmp";
        File.WriteAllText(temp, Serialize());
        File.Move(temp, path, overwrite: true);
    }

    public string Serialize()
    {
        var root = new JsonObject
        {
            ["formatVersion"] = FormatVersion,
            ["values"] = _values.DeepClone(),
        };
        return root.ToJsonString(JsonFields.Indented);
    }

    public static SettingsFile Deserialize(string json)
    {
        const string what = "Settings file";
        JsonObject root = JsonFields.ParseObject(json, what);
        int version = JsonFields.Required<int>(root, "formatVersion", what);
        if (version < 1 || version > FormatVersion)
            throw new InvalidDataException($"{what}: format version {version} is not supported (this app reads up to {FormatVersion}).");
        JsonNode? values = root["values"];
        if (values is null) return new SettingsFile();
        if (values is not JsonObject obj) throw new InvalidDataException($"{what}: 'values' must be an object.");
        return new SettingsFile((JsonObject)obj.DeepClone());
    }
}
