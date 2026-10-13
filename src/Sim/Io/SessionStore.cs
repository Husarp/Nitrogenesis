using System.Globalization;
using System.Text;

namespace Nitrogenesis.Sim.Io;

/// <summary>One row of the Sessions list (PLAN §6.1).</summary>
/// <param name="Name">The session's name (= its folder name).</param>
/// <param name="Path">Its folder.</param>
/// <param name="SavedAtUtc">When it was last saved.</param>
/// <param name="TrackName">Track of its current segment.</param>
/// <param name="Generation">The generation it continues from.</param>
/// <param name="Branch">Its active branch.</param>
/// <param name="Branches">How many branches it has.</param>
/// <param name="SimVersion">SimVersion it was trained with.</param>
/// <param name="Problem">Why it cannot be opened, or null.</param>
public sealed record SessionInfo(string Name, string Path, DateTime SavedAtUtc, string TrackName, int Generation, string Branch,
    int Branches, int SimVersion, string? Problem)
{
    public SessionCompatibility Compatibility => new(SimVersion);
    /// <summary>It can be opened (maybe read-only, see <see cref="Compatibility"/>).</summary>
    public bool CanOpen => Problem is null;
}

/// <summary>
/// The sessions folder (<see cref="AppPaths.Sessions"/>): one folder per session, named by the session's name
/// (PLAN §5.1). Lists, creates, renames and deletes them; "Continue" opens the one saved last.
/// </summary>
public sealed class SessionStore
{
    /// <summary>Longest session name (folder names stay well inside Windows path limits).</summary>
    public const int MaxNameLength = 60;

    public SessionStore(string root)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        Root = root;
    }

    /// <summary>The store at the default place (%APPDATA%\Nitrogenesis\sessions).</summary>
    public static SessionStore Default() => new(AppPaths.Sessions);

    public string Root { get; }

    public SessionFolder Folder(string name) => new(System.IO.Path.Combine(Root, CheckName(name)));

    /// <summary>Every session folder, newest save first; damaged ones (with their <see cref="SessionInfo.Problem"/>) after the others.</summary>
    public List<SessionInfo> List()
    {
        var result = new List<SessionInfo>();
        if (!Directory.Exists(Root)) return result;
        foreach (string dir in Directory.EnumerateDirectories(Root))
        {
            var folder = new SessionFolder(dir);
            if (!folder.Exists) continue; // not a session (yet): a new one dies before its first header
            try
            {
                var h = folder.LoadHeader();
                var current = h.Population;
                var segment = current is null ? null : h.Segments.Find(s => s.Id == current.Segment);
                result.Add(new SessionInfo(folder.Name, dir, h.SavedAtUtc, segment?.Track.Name ?? "?", current?.Generation ?? 0,
                    h.CurrentBranch, h.Branches.Count, h.SimVersion, current is null ? "it has no population" : null));
            }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                result.Add(new SessionInfo(folder.Name, dir, Directory.GetLastWriteTimeUtc(dir), "?", 0, "", 0, 0, e.Message));
            }
        }
        return result.OrderByDescending(s => s.CanOpen).ThenByDescending(s => s.SavedAtUtc).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The session saved last that can be opened and trained on, or null ("Continue (last session)"). Sessions of
    /// another SimVersion are left out: they open read-only (PLAN §5.1), so there is nothing to continue.
    /// </summary>
    public SessionInfo? Latest() => List().FirstOrDefault(s => s.CanOpen && s.Compatibility.CanResume);

    /// <summary>A new, empty session folder named after <paramref name="desiredName"/> (made safe and unique).</summary>
    public SessionFolder Create(string desiredName)
    {
        Directory.CreateDirectory(Root);
        string name = UniqueName(desiredName);
        string path = System.IO.Path.Combine(Root, name);
        Directory.CreateDirectory(path);
        return new SessionFolder(path);
    }

    /// <summary>Renames a session (its folder); returns the name it got.</summary>
    /// <exception cref="SessionLockedException">The session is open (being trained) in a window.</exception>
    public string Rename(string name, string newName)
    {
        string from = System.IO.Path.Combine(Root, CheckName(name));
        if (!Directory.Exists(from)) throw new DirectoryNotFoundException($"There is no session '{name}'.");
        EnsureClosed(from);
        string clean = Sanitize(newName);
        if (string.Equals(clean, name, StringComparison.Ordinal)) return name;
        string target = string.Equals(clean, name, StringComparison.OrdinalIgnoreCase) ? clean : UniqueName(newName);
        string to = System.IO.Path.Combine(Root, target);
        if (string.Equals(target, name, StringComparison.OrdinalIgnoreCase))
        {
            // Only the letter case changes: move through a temporary name (Windows treats both names as one folder).
            string temp = System.IO.Path.Combine(Root, $"{name}.renaming-{Guid.NewGuid():N}");
            Directory.Move(from, temp);
            Directory.Move(temp, to);
        }
        else
        {
            Directory.Move(from, to);
        }
        return target;
    }

    /// <summary>Deletes a session and its history for good.</summary>
    /// <exception cref="SessionLockedException">The session is open (being trained) in a window.</exception>
    public void Delete(string name)
    {
        string path = System.IO.Path.Combine(Root, CheckName(name));
        if (!Directory.Exists(path)) return;
        EnsureClosed(path);
        Directory.Delete(path, recursive: true);
    }

    /// <summary>Throws <see cref="SessionLockedException"/> when a training host holds the session's lock (a rename or delete would pull the folder from under its saves).</summary>
    private static void EnsureClosed(string path)
    {
        using (new SessionFolder(path).AcquireLock()) { }
    }

    /// <summary>A safe, unused folder name for <paramref name="desiredName"/>: "name", else "name (2)", "name (3)"…</summary>
    public string UniqueName(string desiredName)
    {
        string name = Sanitize(desiredName);
        if (!Taken(name)) return name;
        for (int n = 2; ; n++)
        {
            string suffix = string.Create(CultureInfo.InvariantCulture, $" ({n})");
            string candidate = (name.Length + suffix.Length > MaxNameLength ? name[..(MaxNameLength - suffix.Length)].TrimEnd(' ', '.') : name) + suffix;
            if (!Taken(candidate)) return candidate;
        }
    }

    /// <summary>A default name for a new training: "&lt;track&gt; yyyy-MM-dd HH.mm" (local time).</summary>
    public static string DefaultName(string trackName, DateTime local) =>
        Sanitize($"{trackName} {local.ToString("yyyy-MM-dd HH.mm", CultureInfo.InvariantCulture)}");

    /// <summary>
    /// A name that is a valid folder name on Windows: letters (any language), digits, spaces and a few marks;
    /// anything else becomes '_'. No leading/trailing spaces or dots, no reserved device names (CON, NUL, COM1…),
    /// at most <see cref="MaxNameLength"/> characters, never empty.
    /// </summary>
    public static string Sanitize(string name)
    {
        var sb = new StringBuilder();
        foreach (char c in (name ?? "").Trim())
        {
            if (sb.Length >= MaxNameLength) break;
            bool ok = char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '(' or ')' or '[' or ']' or ',' or '.' or '+' or '\'' or '&' or '!' or '#';
            sb.Append(ok ? c : '_');
        }
        string result = sb.ToString().Trim(' ', '.');
        if (result.Length == 0) return "Session";
        string stem = result.Split('.')[0].TrimEnd(' ');
        if (IsReservedDeviceName(stem)) result = "_" + result;
        return result;
    }

    private static bool IsReservedDeviceName(string s)
    {
        string u = s.ToUpperInvariant();
        return u is "CON" or "PRN" or "AUX" or "NUL"
               || (u.Length == 4 && (u.StartsWith("COM", StringComparison.Ordinal) || u.StartsWith("LPT", StringComparison.Ordinal)) && u[3] is >= '0' and <= '9');
    }

    private bool Taken(string name) =>
        Directory.Exists(Root) && Directory.EnumerateDirectories(Root).Any(d => string.Equals(System.IO.Path.GetFileName(d), name, StringComparison.OrdinalIgnoreCase));

    private static string CheckName(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (name != Sanitize(name)) throw new ArgumentException($"'{name}' is not a session name.", nameof(name));
        return name;
    }
}
