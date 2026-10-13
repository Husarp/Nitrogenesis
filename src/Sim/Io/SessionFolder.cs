using System.Globalization;
using Nitrogenesis.Sim.History;

namespace Nitrogenesis.Sim.Io;

/// <summary>What <see cref="SessionFolder.Load"/> read.</summary>
/// <param name="Header">The header (from the backup when the main one was damaged).</param>
/// <param name="History">Each branch's stored generations in [first, end), oldest first.</param>
/// <param name="Warnings">What could not be read, in words for the UI.</param>
/// <param name="FromBackup">header.json was missing or damaged and the previous save's header was used.</param>
public sealed record LoadedSession(SessionHeader Header, IReadOnlyDictionary<string, List<HistoryEntry>> History,
    IReadOnlyList<string> Warnings, bool FromBackup);

/// <summary>Thrown by the test hook <see cref="SessionFolder.FaultInjection"/> to stop a save at a chosen step, as if the app died there.</summary>
public sealed class SimulatedCrashException(string step) : Exception($"Simulated crash at: {step}");

/// <summary>The session is open in another window (its lock file is held).</summary>
public sealed class SessionLockedException(string message, Exception inner) : IOException(message, inner);

/// <summary>The session on disk is no longer the save a loaded session came from (another window saved, renamed or deleted it).</summary>
public sealed class SessionChangedException(string message) : IOException(message);

/// <summary>
/// A session on disk (PLAN §5.1): the folder <c>sessions\&lt;name&gt;\</c> with <c>header.json</c> and
/// <c>history\&lt;branch&gt;\gen-&lt;from&gt;-&lt;to&gt;.bin</c> chunks of up to <see cref="ChunkSize"/> records.
/// </summary>
/// <remarks>
/// <para><b>Crash safety.</b> Every file is written to a temp file, flushed to disk and then moved over the old
/// one (the header with <see cref="File.Replace(string, string, string?)"/>, keeping the previous header as
/// <c>header.json.bak</c>), so a file is always either the old or the new complete version. A save writes the
/// history chunks first and the header last: the header is the commit point. It lists each branch's history end,
/// and on load generations at or after it are ignored, so chunks written by a save that died before its header
/// do no harm. Chunk files and the header's population carry CRC-32s; a damaged header falls back to the backup,
/// a damaged chunk leaves a gap (reported, the rest loads).</para>
/// <para><b>Chunks.</b> Window w of a branch holds generations 25w … 25w+24 (from the branch's first generation
/// when it starts inside the window). A save rewrites only the windows with new generations or thinned snapshots
/// (PLAN §5.1: "Thinning rewrites a chunk without its snapshots"); a complete, unthinned window is written once.
/// Each consecutive run of a window's records is one file named by its range; after writing it, the other files of
/// the window whose committed generations it covers are deleted (shorter partial chunks, leftovers of a save that
/// never committed). On load, files with a lower start win, then the longer one.</para>
/// </remarks>
public sealed class SessionFolder
{
    public const string HeaderFileName = "header.json";
    public const string BackupSuffix = ".bak";
    public const string LockFileName = "session.lock";
    public const string HistoryFolderName = "history";
    public const int ChunkSize = 25;
    private const string TempSuffix = ".tmp";
    private bool _headerDamaged;

    public SessionFolder(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Path = path;
    }

    public string Path { get; }
    /// <summary>The session's name: its folder name.</summary>
    public string Name => System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(Path));
    public string HeaderPath => System.IO.Path.Combine(Path, HeaderFileName);
    public string BackupHeaderPath => HeaderPath + BackupSuffix;

    public bool Exists => File.Exists(HeaderPath) || File.Exists(BackupHeaderPath);

    /// <summary>
    /// Test hook: called with a description at every step of a save (before a temp file is created, after half and
    /// after all of its bytes, after each move and delete). Returning true throws <see cref="SimulatedCrashException"/>
    /// right there, leaving the files as a killed process would.
    /// </summary>
    public Func<string, bool>? FaultInjection { get; set; }

    public string BranchFolder(string branch) => System.IO.Path.Combine(Path, HistoryFolderName, CheckBranchName(branch));

    /// <summary>Chunk file name for generations <paramref name="from"/> … <paramref name="to"/>.</summary>
    public static string ChunkFileName(int from, int to) =>
        string.Create(CultureInfo.InvariantCulture, $"gen-{from}-{to}.bin");

    // ---------------------------------------------------------------- saving

    /// <summary>
    /// Writes a save: clears folders of branches never saved before, writes every planned chunk window, then the
    /// header (the commit point). Any failure leaves the previous save loadable.
    /// </summary>
    public void Save(SavePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Directory.CreateDirectory(Path);
        foreach (string branch in plan.NewBranches)
        {
            string folder = BranchFolder(branch);
            if (!Directory.Exists(folder)) continue;
            Directory.Delete(folder, recursive: true); // uncommitted leftovers of a fork whose header never got written
            Step("rmdir " + folder);
        }
        foreach (var window in plan.Windows) WriteWindow(window);
        if (plan.ReplacesDamagedHeader) _headerDamaged = true;
        SaveHeader(plan.Header);
    }

    /// <summary>
    /// Writes header.json atomically (temp file, flush, then <see cref="File.Replace(string, string, string?)"/>
    /// keeping the previous header as the backup). After a load that had to use the backup, the damaged header is
    /// deleted first, so it never replaces the good backup.
    /// </summary>
    public void SaveHeader(SessionHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);
        Directory.CreateDirectory(Path);
        byte[] data = System.Text.Encoding.UTF8.GetBytes(header.Serialize());
        if (_headerDamaged && File.Exists(HeaderPath))
        {
            File.Delete(HeaderPath);
            Step("delete " + HeaderPath);
        }
        WriteAtomically(HeaderPath, data, BackupHeaderPath);
        _headerDamaged = false;
    }

    /// <summary>Writes one chunk window: a file per consecutive run of its records, then deletes the files they supersede.</summary>
    public void WriteWindow(WindowWrite window)
    {
        ArgumentNullException.ThrowIfNull(window);
        string folder = BranchFolder(window.Branch);
        Directory.CreateDirectory(folder);
        int lo = Math.Max(window.Window * ChunkSize, window.First), hi = Math.Min(window.Window * ChunkSize + ChunkSize - 1, window.End - 1);
        var present = new HashSet<int>();
        foreach (var e in window.Entries)
        {
            if (e.Generation < lo || e.Generation > hi) throw new ArgumentException($"Generation {e.Generation} is outside window {window.Window}.", nameof(window));
            present.Add(e.Generation);
        }
        var written = new HashSet<string>(StringComparer.Ordinal);
        int i = 0;
        while (i < window.Entries.Count)
        {
            int j = i;
            while (j + 1 < window.Entries.Count && window.Entries[j + 1].Generation == window.Entries[j].Generation + 1) j++;
            var run = new HistoryEntry[j - i + 1];
            for (int k = i; k <= j; k++) run[k - i] = window.Entries[k];
            string name = ChunkFileName(run[0].Generation, run[^1].Generation);
            WriteAtomically(System.IO.Path.Combine(folder, name), HistoryChunk.Encode(run));
            written.Add(name);
            i = j + 1;
        }
        foreach (var file in ListChunks(folder))
        {
            if (file.From / ChunkSize != window.Window || written.Contains(System.IO.Path.GetFileName(file.Path))) continue;
            int from = Math.Max(file.From, window.First), to = Math.Min(file.To, window.End - 1);
            bool covered = true;
            for (int g = from; g <= to && covered; g++) covered = present.Contains(g);
            if (!covered) continue; // it still holds generations we do not have (a damaged file): keep it
            File.Delete(file.Path);
            Step("delete " + file.Path);
        }
    }

    // ---------------------------------------------------------------- loading

    /// <summary>
    /// Reads the session: the header (or its backup when it is missing or damaged) and every branch's history up to
    /// the header's history end. Damaged chunk files are skipped and reported in <see cref="LoadedSession.Warnings"/>.
    /// </summary>
    /// <param name="backupOnly">Read only the backup header (the main one was readable but inconsistent).</param>
    /// <param name="problem">Why the backup is used, for the warning.</param>
    /// <exception cref="InvalidDataException">Neither the header nor its backup can be read.</exception>
    public LoadedSession Load(bool backupOnly = false, string? problem = null)
    {
        var warnings = new List<string>();
        SessionHeader header;
        bool fromBackup = backupOnly;
        if (backupOnly)
        {
            if (!File.Exists(BackupHeaderPath)) throw new InvalidDataException($"No backup header in {Path}.");
            header = SessionHeader.Deserialize(File.ReadAllText(BackupHeaderPath));
        }
        else
        {
            header = LoadHeader(out fromBackup, out problem);
        }
        if (fromBackup)
        {
            _headerDamaged = true;
            warnings.Add($"The newest save of this session was damaged ({problem}); the save before it was loaded.");
        }
        var history = new Dictionary<string, List<HistoryEntry>>(StringComparer.Ordinal);
        foreach (var b in header.Branches)
        {
            int end = header.HistoryEnds.TryGetValue(b.Name, out int e) ? e : int.MaxValue;
            history[b.Name] = LoadBranch(b.Name, b.FirstGeneration, end, warnings);
        }
        return new LoadedSession(header, history, warnings, fromBackup);
    }

    /// <summary>The header (or its backup when the header is missing or damaged).</summary>
    /// <exception cref="InvalidDataException">Neither can be read.</exception>
    public SessionHeader LoadHeader() => LoadHeader(out _, out _);

    private SessionHeader LoadHeader(out bool fromBackup, out string? problem)
    {
        fromBackup = false;
        problem = null;
        try
        {
            if (File.Exists(HeaderPath)) return SessionHeader.Deserialize(File.ReadAllText(HeaderPath));
            problem = "header.json is missing";
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
        {
            problem = e.Message;
        }
        if (!File.Exists(BackupHeaderPath)) throw new InvalidDataException($"No readable session header in {Path}: {problem}");
        try
        {
            var header = SessionHeader.Deserialize(File.ReadAllText(BackupHeaderPath));
            fromBackup = true;
            return header;
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"No readable session header in {Path}: {problem}; backup: {e.Message}", e);
        }
    }

    /// <summary>
    /// A branch's stored generations in [<paramref name="first"/>, <paramref name="end"/>), oldest first. Files are
    /// read in order of start (lower first), then length (longer first); the first file that holds a generation wins.
    /// Missing generations (a damaged or missing file) are reported in <paramref name="warnings"/>.
    /// </summary>
    public List<HistoryEntry> LoadBranch(string branch, int first, int end, List<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(warnings);
        string folder = BranchFolder(branch);
        var result = new SortedDictionary<int, HistoryEntry>();
        var damaged = new List<string>();
        if (Directory.Exists(folder))
        {
            var files = ListChunks(folder).Where(f => f.From < end && f.To >= first).OrderBy(f => f.From).ThenByDescending(f => f.To);
            foreach (var file in files)
            {
                int lo = Math.Max(file.From, first), hi = Math.Min(file.To, end - 1);
                bool needed = false;
                for (int g = lo; g <= hi && !needed; g++) needed = !result.ContainsKey(g);
                if (!needed) continue;
                List<HistoryEntry> entries;
                try
                {
                    entries = HistoryChunk.Decode(File.ReadAllBytes(file.Path));
                    if (entries[0].Generation != file.From || entries[^1].Generation != file.To)
                        throw new InvalidDataException("it holds other generations than its name says");
                }
                catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    damaged.Add($"{System.IO.Path.GetFileName(file.Path)} ({e.Message})");
                    continue;
                }
                foreach (var entry in entries)
                    if (entry.Generation >= lo && entry.Generation <= hi) result.TryAdd(entry.Generation, entry);
            }
        }
        int expectedEnd = end != int.MaxValue ? end : result.Count > 0 ? result.Keys.Max() + 1 : first;
        var missing = new List<string>();
        int? gapStart = null;
        for (int g = first; g <= expectedEnd; g++)
        {
            bool have = g < expectedEnd && result.ContainsKey(g);
            if (!have && g < expectedEnd) gapStart ??= g;
            else if (gapStart is int s)
            {
                missing.Add(s == g - 1 ? $"{s}" : $"{s}–{g - 1}");
                gapStart = null;
            }
        }
        if (missing.Count > 0)
            warnings.Add($"Branch '{branch}': generations {string.Join(", ", missing)} could not be read and are missing from its history" +
                         (damaged.Count > 0 ? $" (damaged: {string.Join("; ", damaged)})." : "."));
        return [.. result.Values];
    }

    // ---------------------------------------------------------------- lock

    /// <summary>
    /// Holds the session's lock file open with no sharing while the session is trained, so a second window cannot
    /// open it too (the OS releases it when the process ends, also after a crash).
    /// </summary>
    /// <exception cref="SessionLockedException">Another window has the session open.</exception>
    public IDisposable AcquireLock()
    {
        Directory.CreateDirectory(Path);
        try
        {
            return new FileStream(System.IO.Path.Combine(Path, LockFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException e) when (e is not FileNotFoundException and not DirectoryNotFoundException)
        {
            throw new SessionLockedException($"The session '{Name}' is open in another Nitrogenesis window.", e);
        }
    }

    /// <summary>
    /// <see cref="AcquireLock"/> for a loaded session: succeeds only while the save on disk is still the one it was loaded
    /// from (the header with <paramref name="savedAtUtc"/>; the backup header if the load had to use it). Otherwise
    /// releases the lock, leaves nothing behind that this call created (no recreated folder, no lock file in a folder
    /// without a session) and throws <see cref="SessionChangedException"/>.
    /// </summary>
    /// <exception cref="SessionLockedException">Another window has the session open.</exception>
    /// <exception cref="SessionChangedException">The session changed on disk after it was loaded.</exception>
    public IDisposable AcquireLockForLoaded(DateTime savedAtUtc, bool loadedFromBackup, string? headerStamp = null)
    {
        if (!Directory.Exists(Path)) throw ChangedException();
        var held = AcquireLock();
        if (headerStamp is not null ? HeaderStamp() == headerStamp : IsSavedAt(savedAtUtc, loadedFromBackup)) return held;
        held.Dispose();
        if (!Exists)
        {
            // Nothing of a session is here (it was deleted and the lock recreated the folder): remove what the lock made.
            try
            {
                File.Delete(System.IO.Path.Combine(Path, LockFileName));
                if (!Directory.EnumerateFileSystemEntries(Path).Any()) Directory.Delete(Path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        throw ChangedException();
    }

    /// <summary>A fingerprint of header.json's bytes (empty when it is missing); tells whether another save replaced it.</summary>
    public string HeaderStamp()
    {
        try
        {
            return File.Exists(HeaderPath) ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(HeaderPath))) : "";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "?";
        }
    }

    private SessionChangedException ChangedException() =>
        new($"The session '{Name}' changed on disk while it was opening (another window saved, renamed or deleted it). Open it again.");

    private bool IsSavedAt(DateTime savedAtUtc, bool backup)
    {
        try
        {
            string path = backup ? BackupHeaderPath : HeaderPath;
            return File.Exists(path) && SessionHeader.Deserialize(File.ReadAllText(path)).SavedAtUtc == savedAtUtc;
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Returns the name if it is safe as a folder name (1–64 of letters, digits, '-', '_'), else throws.</summary>
    public static string CheckBranchName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length is < 1 or > 64 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw new ArgumentException($"Branch name '{name}' must be 1–64 letters, digits, '-' or '_'.", nameof(name));
        return name;
    }

    private readonly record struct ChunkFile(int From, int To, string Path);

    /// <summary>Chunk files in a branch folder (temp files and other names are ignored).</summary>
    private static List<ChunkFile> ListChunks(string folder)
    {
        var chunks = new List<ChunkFile>();
        foreach (string file in Directory.EnumerateFiles(folder, "gen-*-*.bin"))
        {
            string[] parts = System.IO.Path.GetFileNameWithoutExtension(file).Split('-');
            if (parts.Length != 3
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int from)
                || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int to)
                || to < from || to / ChunkSize != from / ChunkSize)
                continue;
            chunks.Add(new ChunkFile(from, to, file));
        }
        return chunks;
    }

    /// <summary>Temp file → flush to disk → move over the target (<see cref="File.Replace(string, string, string?)"/> when a backup is wanted).</summary>
    private void WriteAtomically(string path, byte[] data, string? backup = null)
    {
        string temp = path + TempSuffix;
        Step("create " + temp);
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            int half = data.Length / 2;
            stream.Write(data, 0, half);
            if (FaultInjection is not null)
            {
                stream.Flush();
                Step("half " + temp);
            }
            stream.Write(data, half, data.Length - half);
            stream.Flush(flushToDisk: true);
        }
        Step("written " + temp);
        if (backup is not null && File.Exists(path)) File.Replace(temp, path, backup);
        else File.Move(temp, path, overwrite: true);
        Step("moved " + path);
    }

    private void Step(string what)
    {
        if (FaultInjection?.Invoke(what) == true) throw new SimulatedCrashException(what);
    }
}
