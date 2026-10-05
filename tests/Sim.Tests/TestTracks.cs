using Nitrogenesis.Sim.Io;
using Nitrogenesis.Sim.Map;

/// <summary>Loads the bundled tracks from the repository's <c>tracks/</c> folder.</summary>
internal static class TestTracks
{
    public static readonly string[] Files =
        ["sprint.track", "s_curve.track", "hairpins.track", "wrong_turn.track", "grass_shortcut.track", "labyrinth.track", "obstacle_field.track"];

    public static Track Load(string file) => TrackFile.Load(Path.Combine(Dir(), file));

    /// <summary>
    /// Finds the repository's <c>tracks/</c> folder by walking up from the test binaries to the repository root,
    /// recognised by its <c>VERSION</c> file (the build scripts use the same marker).
    /// </summary>
    public static string Dir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "VERSION")) && Directory.Exists(Path.Combine(dir.FullName, "tracks")))
                return Path.Combine(dir.FullName, "tracks");
        throw new DirectoryNotFoundException("Repository tracks/ folder not found above " + AppContext.BaseDirectory);
    }
}
