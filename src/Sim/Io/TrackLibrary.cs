using System.Text;
using Nitrogenesis.Sim.Map;

namespace Nitrogenesis.Sim.Io;

/// <summary>One file in the user's track folder. <see cref="Problem"/> is set when the file cannot be read (then it is shown disabled).</summary>
public sealed record UserTrackInfo(string Name, string Path, DateTime LastWriteUtc, int Width, int Height, string? Problem)
{
    public bool CanOpen => Problem is null;
}

/// <summary>
/// The user's own tracks (<see cref="AppPaths.Tracks"/>, PLAN §5.1): one <c>&lt;name&gt;.track</c> file per track name.
/// Names are compared case-insensitively (Windows file names are), so "Foo" and "foo" are one track. Saving is atomic:
/// a temp file in the same folder is written and flushed, then moved over the target, so a failed save leaves the old
/// file as it was and a half-written track can never be seen under the real name.
/// </summary>
public sealed class TrackLibrary
{
    public const int MaxNameLength = 60;
    public const string TempSuffix = ".saving";

    public TrackLibrary(string root)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        Root = root;
    }

    public static TrackLibrary Default() => new(AppPaths.Tracks);

    public string Root { get; }

    /// <summary>Test hook: called with the temp file's path after it is complete and before it replaces the target; may throw to simulate a failure.</summary>
    public Action<string>? FaultBeforeCommit { get; set; }

    /// <summary>The file name for a track name: <c>name.track</c>.</summary>
    public static string FileName(string name) => name + TrackFile.Extension;

    public string PathFor(string name) => System.IO.Path.Combine(Root, FileName(name));

    /// <summary>
    /// Why <paramref name="name"/> cannot be a track name (a sentence for the user), or null when it can. Same rules as session
    /// names: not empty, letters/digits/spaces and - _ ( ) [ ] , . + ' &amp; ! # only, at most <see cref="MaxNameLength"/> characters,
    /// no dot or space at the end, not a Windows device name (CON, NUL, COM1…).
    /// </summary>
    public static string? CheckName(string? name)
    {
        string wanted = (name ?? "").Trim();
        if (wanted.Length == 0) return "Type a name for the track.";
        if (wanted.Length > MaxNameLength) return $"The name is too long (at most {MaxNameLength} characters).";
        if (wanted.TrimEnd('.', ' ').Length != wanted.Length) return "The name cannot end with a dot or a space.";
        if (SessionStore.Sanitize(wanted) != wanted)
        {
            var bad = new StringBuilder();
            foreach (char c in wanted)
                if (!(char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '(' or ')' or '[' or ']' or ',' or '.' or '+' or '\'' or '&' or '!' or '#') && bad.ToString().IndexOf(c) < 0) bad.Append(c);
            return bad.Length > 0
                ? $"'{bad}' cannot be used in a track name. Use letters, digits, spaces and - _ ( ) [ ] , . + ' & ! # only."
                : $"'{wanted}' is a reserved Windows name. Pick another name.";
        }
        return null;
    }

    /// <summary>The name as it will be used (trimmed).</summary>
    public static string Normalize(string name) => (name ?? "").Trim();

    /// <summary>The existing file for this name (compared ignoring case), or null. Directories do not count.</summary>
    public string? FindExisting(string name)
    {
        if (!Directory.Exists(Root)) return null;
        string wanted = FileName(Normalize(name));
        foreach (string file in Directory.EnumerateFiles(Root))
            if (string.Equals(System.IO.Path.GetFileName(file), wanted, StringComparison.OrdinalIgnoreCase)) return file;
        return null;
    }

    public bool Exists(string name) => FindExisting(name) is not null;

    /// <summary>Every <c>.track</c> file, sorted by name. A file that cannot be read is listed with its problem instead of failing the list.</summary>
    public List<UserTrackInfo> List()
    {
        var result = new List<UserTrackInfo>();
        if (!Directory.Exists(Root)) return result;
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(Root, "*" + TrackFile.Extension).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return result;
        }
        foreach (string file in files)
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(file);
            DateTime when = DateTime.MinValue;
            try
            {
                when = File.GetLastWriteTimeUtc(file);
                var track = TrackFile.Load(file);
                result.Add(new UserTrackInfo(name, file, when, track.Grid.Width, track.Grid.Height, null));
            }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                result.Add(new UserTrackInfo(name, file, when, 0, 0, e.Message));
            }
        }
        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    /// <summary>Loads a track file; its name is the file's name (the one the user sees in the list).</summary>
    /// <exception cref="InvalidDataException">The file is damaged.</exception>
    public static Track Load(string path)
    {
        var track = TrackFile.Load(path);
        track.Name = System.IO.Path.GetFileNameWithoutExtension(path);
        return track;
    }

    /// <summary>
    /// Saves <paramref name="track"/> as <paramref name="name"/> (replacing the file that has this name, whatever its letter case):
    /// temp file in the same folder → flush to disk → move over the target. On any failure the temp file is removed, the old file
    /// is untouched and the exception (IOException / UnauthorizedAccessException) goes to the caller. Returns the file's path.
    /// The caller asks the user before overwriting (<see cref="Exists"/>).
    /// </summary>
    public string Save(Track track, string name)
    {
        ArgumentNullException.ThrowIfNull(track);
        string clean = Normalize(name);
        if (CheckName(clean) is { } problem) throw new ArgumentException(problem, nameof(name));
        Directory.CreateDirectory(Root);
        string target = FindExisting(clean) ?? PathFor(clean);
        string temp = target + TempSuffix;
        var copy = new Track(clean, track.Grid, track.Start, track.Meta);
        try
        {
            byte[] data = new UTF8Encoding(false).GetBytes(TrackFile.Serialize(copy));
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(data);
                stream.Flush(flushToDisk: true);
            }
            FaultBeforeCommit?.Invoke(temp);
            if (File.Exists(target)) File.Replace(temp, target, null);
            else File.Move(temp, target, overwrite: false);
        }
        catch
        {
            try { File.Delete(temp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
        return target;
    }
}
