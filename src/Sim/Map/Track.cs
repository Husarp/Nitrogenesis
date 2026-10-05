using System.Text.Json.Nodes;

namespace Nitrogenesis.Sim.Map;

/// <summary>
/// Where the car starts: its centre in world units (cells) and its heading in degrees.
/// Angle 0 points along +x; positive angles turn towards +y (clockwise on screen, since y grows downward).
/// </summary>
public readonly record struct TrackStart(float X, float Y, float AngleDeg);

/// <summary>Optional generator info stored in a track's <c>meta</c> (PLAN §7: template, parameters, seed).</summary>
public sealed record TrackMeta(string? Template = null, JsonObject? Params = null, ulong? Seed = null);

/// <summary>A complete track: the grid, the start, a display name and generator metadata.</summary>
public sealed class Track
{
    public string Name { get; set; }
    public Grid Grid { get; }
    public TrackStart Start { get; set; }
    public TrackMeta Meta { get; set; }

    public Track(string name, Grid grid, TrackStart start, TrackMeta? meta = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(grid);
        Name = name;
        Grid = grid;
        Start = start;
        Meta = meta ?? new TrackMeta();
    }
}
