namespace Nitrogenesis.Sim.History;

/// <summary>The stored generations of one branch, oldest first.</summary>
/// <remarks>Generations are strictly increasing. Normally they are consecutive; a gap only appears where a damaged
/// save file could not be read (see <see cref="Io.SessionFolder"/>).</remarks>
public sealed class BranchHistory
{
    private readonly List<HistoryEntry> _entries = [];

    internal BranchHistory(string name, int order)
    {
        Name = name;
        Order = order;
    }

    public string Name { get; }
    /// <summary>Creation order in the store (ties in the thinning order go to the older branch first).</summary>
    internal int Order { get; }
    public int Count => _entries.Count;
    public IReadOnlyList<HistoryEntry> Entries => _entries;
    public HistoryEntry? Last => _entries.Count > 0 ? _entries[^1] : null;

    /// <summary>Index of <paramref name="generation"/> in <see cref="Entries"/>, or −1.</summary>
    public int IndexOf(int generation)
    {
        // Usually consecutive: try the direct index first.
        if (_entries.Count == 0) return -1;
        int guess = generation - _entries[0].Generation;
        if ((uint)guess < (uint)_entries.Count && _entries[guess].Generation == generation) return guess;
        int lo = 0, hi = _entries.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            int g = _entries[mid].Generation;
            if (g == generation) return mid;
            if (g < generation) lo = mid + 1;
            else hi = mid - 1;
        }
        return -1;
    }

    public HistoryEntry? Get(int generation)
    {
        int i = IndexOf(generation);
        return i < 0 ? null : _entries[i];
    }

    internal void Append(HistoryEntry entry)
    {
        if (_entries.Count > 0 && entry.Generation <= _entries[^1].Generation)
            throw new ArgumentException($"Generation {entry.Generation} of '{Name}' is not after the last one ({_entries[^1].Generation}).", nameof(entry));
        _entries.Add(entry);
    }

    internal void Replace(int index, HistoryEntry entry) => _entries[index] = entry;
}

/// <summary>
/// Every generation of every branch in memory (PLAN §4 "Memory cap", §5): best genome, recording and stats are
/// always kept; full-population snapshots are thinned when the history uses more than <see cref="MemoryLimit"/>.
/// Not thread-safe: the session locks it.
/// </summary>
/// <remarks>
/// <para>Thinning goes level by level, oldest first: level 1 keeps the snapshots of every 5th generation
/// (generation number divisible by 5) and drops the others, oldest first, only as many as needed; when only
/// multiples of 5 are left, level 2 keeps every 10th, then every 20th, 40th… Generation 0 is a multiple of every
/// spacing and stays last. "Oldest" is the lower generation number; on a tie the branch created first.</para>
/// <para>Dropped snapshots can be rebuilt from the nearest earlier one (see <see cref="Rebuilder"/>), so thinning
/// never loses a generation, it only makes forking from it slower.</para>
/// </remarks>
public sealed class HistoryStore
{
    /// <summary>Default "History memory limit" (PLAN §4): 1 GB.</summary>
    public const long DefaultMemoryLimit = 1L << 30;
    /// <summary>Level 1 keeps every 5th generation's snapshot; each further level doubles the spacing.</summary>
    public const int ThinBaseSpacing = 5;

    private readonly Dictionary<string, BranchHistory> _branches = new(StringComparer.Ordinal);
    private readonly List<BranchHistory> _order = [];
    // Entries that still hold a snapshot, in thinning order (oldest generation first, then the older branch).
    private readonly SortedSet<(int Generation, int Order)> _withSnapshot = [];
    // Per level: every snapshot of a generation below this one is a multiple of that level's spacing, so a thinning
    // pass starts here instead of at the oldest snapshot (steady state: one step per generation, not one per snapshot).
    private readonly int[] _cleanBelow = Enumerable.Repeat(int.MaxValue, MaxLevel + 1).ToArray();
    private const int MaxLevel = 47;

    public long MemoryLimit { get; set; } = DefaultMemoryLimit;

    /// <summary>Memory held by all entries (<see cref="HistoryEntry.Bytes"/>).</summary>
    public long Bytes { get; private set; }

    /// <summary>Memory held by the population snapshots alone.</summary>
    public long SnapshotBytes { get; private set; }

    public int SnapshotCount => _withSnapshot.Count;

    public IReadOnlyList<BranchHistory> Branches => _order;

    /// <summary>Snapshot spacing kept at thinning level <paramref name="level"/> (0 = all, 1 = every 5th, 2 = every 10th…).</summary>
    public static long Spacing(int level) => level == 0 ? 1 : (long)ThinBaseSpacing << (level - 1);

    public BranchHistory? Find(string branch) => _branches.GetValueOrDefault(branch);

    public BranchHistory GetOrAdd(string branch)
    {
        ArgumentNullException.ThrowIfNull(branch);
        if (!_branches.TryGetValue(branch, out var history))
        {
            history = new BranchHistory(branch, _order.Count);
            _branches.Add(branch, history);
            _order.Add(history);
        }
        return history;
    }

    public HistoryEntry? Get(string branch, int generation) => Find(branch)?.Get(generation);

    /// <summary>Appends a generation to a branch (after its last one). Does not thin; call <see cref="Thin"/>.</summary>
    public void Add(string branch, HistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var history = GetOrAdd(branch);
        history.Append(entry);
        Bytes += entry.Bytes;
        if (entry.Snapshot is not null)
        {
            SnapshotBytes += entry.SnapshotBytes;
            _withSnapshot.Add((entry.Generation, history.Order));
            for (int level = 1; level <= MaxLevel; level++)
                if (entry.Generation % Spacing(level) != 0 && entry.Generation < _cleanBelow[level]) _cleanBelow[level] = entry.Generation;
        }
    }

    /// <summary>
    /// Drops snapshots, level by level and oldest first, until <see cref="Bytes"/> ≤ <see cref="MemoryLimit"/> or
    /// none can go. <paramref name="thinned"/> is told each (branch, generation) whose snapshot was dropped. Returns
    /// how many were dropped.
    /// </summary>
    public int Thin(Action<string, int>? thinned = null)
    {
        int dropped = 0;
        if (Bytes <= MemoryLimit || _withSnapshot.Count == 0) return 0;
        var victims = new List<(int Generation, int Order)>();
        for (int level = 1; level <= MaxLevel && Bytes > MemoryLimit && _withSnapshot.Count > 0; level++)
        {
            long spacing = Spacing(level);
            long excess = Bytes - MemoryLimit, freed = 0;
            int newest = _withSnapshot.Max.Generation;
            victims.Clear();
            int clean = int.MaxValue; // where the next pass of this level has to start
            if (_cleanBelow[level] <= newest)
            {
                foreach (var key in _withSnapshot.GetViewBetween((_cleanBelow[level], int.MinValue), (int.MaxValue, int.MaxValue)))
                {
                    if (key.Generation % spacing == 0) continue;
                    if (freed >= excess)
                    {
                        clean = key.Generation;
                        break;
                    }
                    victims.Add(key);
                    freed += _order[key.Order].Get(key.Generation)!.SnapshotBytes;
                }
            }
            _cleanBelow[level] = clean;
            foreach (var (generation, order) in victims)
            {
                var history = _order[order];
                int index = history.IndexOf(generation);
                var entry = history.Entries[index];
                history.Replace(index, entry.WithoutSnapshot());
                Bytes -= entry.SnapshotBytes;
                SnapshotBytes -= entry.SnapshotBytes;
                _withSnapshot.Remove((generation, order));
                dropped++;
                thinned?.Invoke(history.Name, generation);
            }
            if (spacing > newest) break; // only generation 0 can be left; nothing more to drop
        }
        return dropped;
    }
}
