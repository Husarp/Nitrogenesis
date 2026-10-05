using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nitrogenesis.Sim.Io;

/// <summary>Small helpers for reading typed fields out of a parsed JSON object, failing with clear messages.</summary>
internal static class JsonFields
{
    /// <summary>
    /// Indented output for human-readable files. Relaxed escaping writes base64 '+' and non-ASCII names
    /// as-is instead of \uXXXX (the files are UTF-8 and never embedded in HTML).
    /// </summary>
    public static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static JsonObject ParseObject(string json, string what)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{what} is not valid JSON: {e.Message}", e);
        }
        return node as JsonObject ?? throw new InvalidDataException($"{what} must be a JSON object.");
    }

    /// <summary>Reads a required field of type T, or throws <see cref="InvalidDataException"/>.</summary>
    public static T Required<T>(JsonObject obj, string key, string what)
    {
        if (obj[key] is JsonValue value && value.TryGetValue(out T? result) && result is not null) return result;
        throw new InvalidDataException($"{what}: field '{key}' is missing or not a {typeof(T).Name}.");
    }

    /// <summary>Reads an optional field of type T: default when absent or null, throws when present with the wrong type.</summary>
    public static T? Optional<T>(JsonObject obj, string key, string what)
    {
        JsonNode? node = obj[key];
        if (node is null) return default;
        if (node is JsonValue value && value.TryGetValue(out T? result)) return result;
        throw new InvalidDataException($"{what}: field '{key}' is not a {typeof(T).Name}.");
    }
}
