using System.Collections.Generic;
using Godot;
using Nitrogenesis.Sim.Io;
using Nitrogenesis.Sim.Map;

namespace Nitrogenesis.Ui;

/// <summary>
/// The tracks shipped with the app, read from <c>res://tracks/</c> (copied from the repository's tracks/ by the
/// build and packed into the export, see Nitrogenesis.csproj and export_presets.cfg).
/// </summary>
public static class BundledTracks
{
    public const string Folder = "res://tracks";

    /// <summary>Every bundled track, sorted by name. A file that does not load is skipped with an error in the log.</summary>
    public static List<Track> LoadAll()
    {
        var tracks = new List<Track>();
        foreach (string file in DirAccess.GetFilesAt(Folder))
        {
            if (!file.EndsWith(TrackFile.Extension, System.StringComparison.OrdinalIgnoreCase)) continue;
            string path = $"{Folder}/{file}";
            try
            {
                tracks.Add(TrackFile.Deserialize(FileAccess.GetFileAsString(path)));
            }
            catch (System.Exception e)
            {
                GD.PushError($"Bundled track {path} did not load: {e.Message}");
            }
        }
        tracks.Sort((a, b) => string.Compare(a.Name, b.Name, System.StringComparison.OrdinalIgnoreCase));
        return tracks;
    }
}
