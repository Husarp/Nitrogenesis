using System.Globalization;
using Nitrogenesis.Sim.History;

namespace Nitrogenesis.Sim.Io;

/// <summary>
/// A session on disk (PLAN §5.1): the folder <c>sessions\&lt;name&gt;\</c> with <c>header.json</c> and
/// <c>history\&lt;branch&gt;\gen-&lt;from&gt;-&lt;to&gt;.bin</c> chunks of up to <see cref="ChunkSize"/> records.
/// </summary>
/// <remarks>
/// <para>Chunk k of a branch holds generations 25k … 25k+24 (from the branch's first generation when it starts
/// inside the chunk). A complete chunk is written once and never rewritten. The newest records that do not fill
/// a chunk yet are saved as a shorter chunk with the same <c>from</c>; a later save writes the longer file first
/// and only then deletes the shorter one, so a crash at any point leaves a readable history (on load the longest
/// file per <c>from</c> wins).</para>
/// <para>Every file is written to a temp file first and then moved into place (the header with
/// <see cref="File.Replace(string, string, string?)"/>), so a killed autosave never leaves a half-written file.</para>
/// </remarks>
public sealed class SessionFolder
{
    public const string HeaderFileName = "header.json";
    public const string HistoryFolderName = "history";
    public const int ChunkSize = 25;
    private const string TempSuffix = ".tmp";

    public SessionFolder(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Path = path;
    }

    public string Path { get; }
    public string HeaderPath => System.IO.Path.Combine(Path, HeaderFileName);

    public bool Exists => File.Exists(HeaderPath);

    public string BranchFolder(string branch) => System.IO.Path.Combine(Path, HistoryFolderName, CheckBranchName(branch));

    /// <summary>Chunk file name for generations <paramref name="from"/> … <paramref name="to"/>.</summary>
    public static string ChunkFileName(int from, int to) =>
        string.Create(CultureInfo.InvariantCulture, $"gen-{from}-{to}.bin");

    /// <summary>Writes header.json atomically: a temp file, then <see cref="File.Replace(string, string, string?)"/> (a move the first time).</summary>
    public void SaveHeader(SessionHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);
        Directory.CreateDirectory(Path);
        string temp = HeaderPath + TempSuffix;
        File.WriteAllText(temp, header.Serialize());
        if (File.Exists(HeaderPath)) File.Replace(temp, HeaderPath, null);
        else File.Move(temp, HeaderPath);
    }

    /// <exception cref="InvalidDataException">The header is missing or not valid.</exception>
    public SessionHeader LoadHeader()
    {
        if (!File.Exists(HeaderPath)) throw new InvalidDataException($"No session header in {Path}.");
        return SessionHeader.Deserialize(File.ReadAllText(HeaderPath));
    }

    /// <summary>
    /// Saves a branch's history: every chunk not on disk yet, plus the newest partial chunk. Chunks already
    /// written are left alone. <paramref name="records"/> are consecutive generations, oldest first, from the
    /// branch's first generation on (or at least from the start of the first chunk not saved yet).
    /// </summary>
    public void SaveHistory(string branch, IReadOnlyList<GenerationRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        string folder = BranchFolder(branch);
        Directory.CreateDirectory(folder);
        Dictionary<int, List<int>> existing = ListChunks(folder);

        int i = 0;
        while (i < records.Count)
        {
            int from = records[i].Generation;
            int chunkEnd = (from / ChunkSize + 1) * ChunkSize - 1;
            int j = i;
            while (j + 1 < records.Count && records[j + 1].Generation <= chunkEnd)
            {
                if (records[j + 1].Generation != records[j].Generation + 1)
                    throw new ArgumentException("Records must be consecutive generations.", nameof(records));
                j++;
            }
            int to = records[j].Generation;
            existing.TryGetValue(from, out List<int>? tos);
            if (tos is null || !tos.Contains(to))
            {
                if (tos is not null && tos.Max() > to)
                    throw new InvalidOperationException($"History of '{branch}' on disk already goes past generation {to}.");
                var chunk = new GenerationRecord[j - i + 1];
                for (int k = i; k <= j; k++) chunk[k - i] = records[k];
                WriteAtomically(System.IO.Path.Combine(folder, ChunkFileName(from, to)), HistoryChunk.Encode(chunk));
                // The longer file is safely on disk: drop the shorter ones it replaces.
                if (tos is not null)
                    foreach (int shorter in tos)
                        File.Delete(System.IO.Path.Combine(folder, ChunkFileName(from, shorter)));
            }
            i = j + 1;
        }
    }

    /// <summary>Loads a branch's history, oldest first (empty when there is none).</summary>
    /// <exception cref="InvalidDataException">A chunk is damaged or generations are missing.</exception>
    public List<GenerationRecord> LoadHistory(string branch)
    {
        string folder = BranchFolder(branch);
        var records = new List<GenerationRecord>();
        if (!Directory.Exists(folder)) return records;
        foreach (var (from, tos) in ListChunks(folder).OrderBy(p => p.Key))
        {
            List<GenerationRecord> chunk = HistoryChunk.Decode(File.ReadAllBytes(System.IO.Path.Combine(folder, ChunkFileName(from, tos.Max()))));
            if (chunk[0].Generation != from || chunk[^1].Generation != tos.Max())
                throw new InvalidDataException($"History chunk {ChunkFileName(from, tos.Max())} holds other generations than its name says.");
            if (records.Count > 0 && from != records[^1].Generation + 1)
                throw new InvalidDataException($"History of '{branch}' is missing generations {records[^1].Generation + 1}…{from - 1}.");
            records.AddRange(chunk);
        }
        return records;
    }

    /// <summary>Returns the name if it is safe as a folder name (1–64 of letters, digits, '-', '_'), else throws.</summary>
    public static string CheckBranchName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length is < 1 or > 64 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw new ArgumentException($"Branch name '{name}' must be 1–64 letters, digits, '-' or '_'.", nameof(name));
        return name;
    }

    /// <summary>Chunk files in a branch folder by first generation (several when an old partial chunk is still there).</summary>
    private static Dictionary<int, List<int>> ListChunks(string folder)
    {
        var chunks = new Dictionary<int, List<int>>();
        foreach (string file in Directory.EnumerateFiles(folder, "gen-*-*.bin"))
        {
            string[] parts = System.IO.Path.GetFileNameWithoutExtension(file).Split('-');
            if (parts.Length != 3
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int from)
                || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int to)
                || to < from || to / ChunkSize != from / ChunkSize)
                continue;
            if (!chunks.TryGetValue(from, out var tos)) chunks[from] = tos = [];
            tos.Add(to);
        }
        return chunks;
    }

    private static void WriteAtomically(string path, byte[] data)
    {
        string temp = path + TempSuffix;
        File.WriteAllBytes(temp, data);
        File.Move(temp, path, overwrite: true);
    }
}
