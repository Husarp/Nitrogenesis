using Nitrogenesis.Sim.Map;
using Nitrogenesis.Sim.Racing;
using Nitrogenesis.Sim.Rng;

namespace Nitrogenesis.Sim.Gen;

/// <summary>
/// Makes a track from a template, its parameters and a seed (PLAN §7). Deterministic: the same inputs give the same cells on
/// every platform (integer RNG; only + − × ÷ and √ on doubles; <see cref="TrackBuilder"/> paints by exact squared distances).
/// </summary>
/// <remarks>
/// <para>Method (§7): a full-wall map; a route on a coarse grid (a self-avoiding random walk with backtracking, or a maze for
/// Labyrinth), smoothed by corner cutting (Chaikin); road carved with a round brush and a grass verge around it; the
/// template's features (dead ends, obstacles); the start with run-off behind it and the finish zone at the route's end.</para>
/// <para>The route is sized for the middle of the target time from the template's typical reference speed
/// (<see cref="ExpectedSpeed"/>); <see cref="Validate"/> then checks the real time.</para>
/// <para><b>No grass between corridors.</b> A coarse cell is big enough that two corridors which are not joined always keep
/// wall between their verges, also where smoothing pulls two corners towards each other (each by up to 1/8 of a cell per
/// axis): cell ≥ (2 × (road radius + verge) + 2) × 4/3. So grass never links two parts of the route, and the only grass a
/// car can cut across is a verge on the inside of a bend, which is never 10 % faster.</para>
/// </remarks>
public static class TrackGenerator
{
    /// <summary>Seeds tried by <see cref="GenerateValid"/> before it gives up (PLAN §7: retry with the next seed, up to 50 times).</summary>
    public const int MaxAttempts = 50;

    /// <summary>Corner-cutting passes on the route (Labyrinth: none, its corridors stay straight).</summary>
    private const int SmoothPasses = 3;
    /// <summary>A smoothed 90° turn is this share of a coarse cell shorter than the two half-legs it replaces.</summary>
    private const double TurnShortening = 0.19;
    /// <summary>Walk steps (forward and back) before a walk gives up.</summary>
    private const int MaxWalkSteps = 100_000;
    /// <summary>The finish zone: the road from this far before the route's end point to the road's end (a straight line across).</summary>
    private const double FinishDepth = 3;
    /// <summary>Obstacles: the lane left free beside a block (cells, over 2 × the car width, as on the hand-made obstacle field) and the least spacing along the road.</summary>
    private const double Lane = 4, MinObstacleSpacing = 24;
    /// <summary>Wall blocks: core half-thickness and the danger lining around it (so the road never touches the wall).</summary>
    private const double WallCore = 2.5, Lining = 1.5;
    /// <summary>Danger patches (islands with a lane on both sides) and the small danger bars of Mixed's chicanes.</summary>
    private const double IslandRadius = 3, BumpRadius = 1.2;

    /// <summary>
    /// Route length (as the walk counts it: coarse steps, a bit less per turn) per second of reference time at the default
    /// physics, per template; the route is made (target time × this) long. Measured with <c>SimBench gen-check</c>: the times
    /// then centre on the middle of the target (spread about ±3 s).
    /// </summary>
    internal static double ExpectedSpeed(TrackTemplate template) => template switch
    {
        TrackTemplate.Sprint => 17.5,
        TrackTemplate.Twisty => 17.5,
        TrackTemplate.Labyrinth => 19,
        TrackTemplate.WrongTurns => 17.5,
        TrackTemplate.ObstacleField => 17,
        _ => 17.5,
    };

    /// <summary>
    /// One attempt: the track for this seed, or null when the route does not fit on the map (too small for the road width and
    /// the target time). Not validated: see <see cref="Validate"/> and <see cref="GenerateValid"/>. The meta holds the
    /// template, the (clamped) parameters and the seed.
    /// </summary>
    public static Track? Generate(TrackTemplate template, GeneratorParams parameters, ulong seed)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var p = parameters.Clamped();
        var b = new Builder(template, p, seed);
        if (!b.Build()) return null;
        return new Track($"{Templates.Name(template)} {seed}", b.Grid, b.Start, new TrackMeta(template.ToString(), p.ToJson(template), seed));
    }

    /// <summary>
    /// The §7 checks: finishable with one car length of road behind the start, no grass shortcut (the editor's analysis), and
    /// the reference driver finishes within the target time.
    /// </summary>
    public static GenCheck Validate(Track track, GeneratorParams parameters, RacingSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(parameters);
        var p = parameters.Clamped();
        settings ??= new RacingSettings();
        var a = EditorAnalysis.Run(track, settings);
        if (!a.IsFinishable) return new(GenProblem.NotFinishable, 0, $"not finishable ({a.State})");
        if (!a.HasRoadBehindStart) return new(GenProblem.NoRoadBehindStart, 0, "less than one car length of road behind the start");
        if (a.IsGrassShortcut) return new(GenProblem.GrassShortcut, 0, $"a grass route is more than 10 % faster ({a.WithGrassCost:0} vs {a.RoadOnlyCost:0} on road)");
        var r = ReferenceDriver.Run(new RacingTrack(track, settings));
        string time = r.TimeSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        if (!r.Finished) return new(GenProblem.DriverFailed, r.TimeSeconds, $"the reference driver did not finish ({r.EndStatus.ToString().ToLowerInvariant()} after {time} s)");
        if (r.TimeSeconds < p.MinSeconds) return new(GenProblem.TooShort, r.TimeSeconds, $"the reference driver took {time} s (under {p.MinSeconds} s)");
        if (r.TimeSeconds > p.MaxSeconds) return new(GenProblem.TooLong, r.TimeSeconds, $"the reference driver took {time} s (over {p.MaxSeconds} s)");
        return new(GenProblem.None, r.TimeSeconds, $"the reference driver finished in {time} s");
    }

    /// <summary>
    /// Generates and validates seed, seed + 1, … until a track passes, at most <paramref name="maxAttempts"/> seeds (PLAN §7). The
    /// valid track's meta holds the seed that made it, so it can be made again. <paramref name="progress"/> gets (attempt, seed)
    /// before each attempt; <paramref name="ct"/> is checked between attempts.
    /// </summary>
    public static GenResult GenerateValid(TrackTemplate template, GeneratorParams parameters, ulong seed, RacingSettings? settings = null,
        int maxAttempts = MaxAttempts, Action<int, ulong>? progress = null, CancellationToken ct = default) =>
        GenerateValid(template, parameters, seed, maxAttempts, progress, ct, track => Validate(track, parameters, settings));

    /// <summary>The retry loop with any validation (tests).</summary>
    internal static GenResult GenerateValid(TrackTemplate template, GeneratorParams parameters, ulong seed, int maxAttempts,
        Action<int, ulong>? progress, CancellationToken ct, Func<Track, GenCheck> validate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        var failed = new List<(ulong Seed, GenCheck Check)>();
        for (int i = 0; i < maxAttempts; i++)
        {
            ct.ThrowIfCancellationRequested();
            ulong s = unchecked(seed + (ulong)i);
            progress?.Invoke(i + 1, s);
            var track = Generate(template, parameters, s);
            var check = track is null ? new GenCheck(GenProblem.NoRoom, 0, "the route did not fit on the map") : validate(track);
            if (check.Valid) return new GenResult(track, s, i + 1, check, failed);
            failed.Add((s, check));
        }
        return new GenResult(null, seed, maxAttempts, failed[^1].Check, failed);
    }

    /// <summary>One attempt's work: the coarse layout, the route, carving and features.</summary>
    private sealed class Builder
    {
        private enum Piece { Straight, Twisty, Chicane, WrongTurn, DeadEnds }

        private static readonly (int Dx, int Dy)[] Dirs = [(1, 0), (0, 1), (-1, 0), (0, -1)];

        private readonly TrackTemplate _t;
        private readonly GeneratorParams _p;
        private readonly Xoshiro128StarStar _rng;
        private readonly TrackBuilder _b;
        private readonly double _r, _v;
        private readonly int _cell, _cols, _rows, _offX, _offY;
        private readonly bool[] _used;
        private readonly List<List<(double X, double Y)>> _corridors = [];
        private List<(int C, int R)> _route = [];
        private List<(double X, double Y)> _line = [];
        private double[] _arc = [];

        public Grid Grid => _b.Grid;
        public TrackStart Start { get; private set; }

        public Builder(TrackTemplate template, GeneratorParams p, ulong seed)
        {
            _t = template;
            _p = p;
            _rng = new Xoshiro128StarStar(seed);
            var (w, h) = EditDocument.Dimensions(p.Size);
            _b = new TrackBuilder(new Grid(w, h));
            _r = p.RoadWidth / 2.0;
            _v = p.Verge;
            double spacing = 2 * (_r + _v) + 2;
            int least = (int)Math.Ceiling(template == TrackTemplate.Labyrinth ? spacing : spacing * 4 / 3);
            // Smoothed corners on a smaller cell are too tight to take near full speed, so the route sized by ExpectedSpeed runs
            // over the time window (road width 4 with verge 1 made 11-cell cells: Twisty failed 9 seeds in 10).
            _cell = Math.Max(least, template switch { TrackTemplate.Sprint => 28, TrackTemplate.Labyrinth => 16, _ => 14 });
            _cols = w / _cell;
            _rows = h / _cell;
            _offX = (w - _cols * _cell) / 2;
            _offY = (h - _rows * _cell) / 2;
            _used = new bool[Math.Max(0, _cols * _rows)];
        }

        public bool Build()
        {
            if (_cols < 2 || _rows < 2) return false;
            double target = (_p.MinSeconds + _p.MaxSeconds) / 2.0 * ExpectedSpeed(_t);
            List<Piece>? pieces = null;
            switch (_t)
            {
                case TrackTemplate.Labyrinth:
                    if (!Labyrinth(target)) return false;
                    break;
                case TrackTemplate.Mixed:
                    var steps = Pieces();
                    pieces = steps;
                    _route = Walk(target, i => steps[Math.Min(i, steps.Count - 1)] switch
                    {
                        Piece.Straight or Piece.Chicane => 0.85,
                        Piece.Twisty => 0.15,
                        _ => 0.5,
                    }) ?? [];
                    break;
                default:
                    double straightness = _t switch { TrackTemplate.Sprint or TrackTemplate.ObstacleField => 0.85, TrackTemplate.Twisty => 0.15, _ => 0.5 };
                    _route = Walk(target, _ => straightness) ?? [];
                    break;
            }
            if (_route.Count < 2) return false;

            var centres = _route.ConvertAll(Centre);
            _line = _t == TrackTemplate.Labyrinth ? centres : Smooth(centres, SmoothPasses);
            _arc = new double[_line.Count];
            for (int i = 1; i < _line.Count; i++) _arc[i] = _arc[i - 1] + Distance(_line[i - 1], _line[i]);
            _corridors.Add(_line);

            if (_t == TrackTemplate.WrongTurns) WrongTurns();
            if (pieces is not null) MixedBranches(pieces);

            foreach (var c in _corridors) _b.Polyline(c, _r + _v, CellType.Grass, onlyOver: CellType.Wall);
            foreach (var c in _corridors) _b.Polyline(c, _r, CellType.Road);

            if (_t == TrackTemplate.ObstacleField) Obstacles();
            if (pieces is not null) Chicanes(pieces);

            // The last leg is straight along a grid axis (smoothing keeps the end's direction), so the finish line is too.
            var (ex, ey) = _line[^1];
            int fx = _route[^1].C - _route[^2].C, fy = _route[^1].R - _route[^2].R;
            double across = _r + 1;
            _b.Rect((int)Math.Floor(ex - fx * FinishDepth - fy * across), (int)Math.Floor(ey - fy * FinishDepth - fx * across),
                (int)Math.Floor(ex + fx * across + fy * across), (int)Math.Floor(ey + fy * across + fx * across), CellType.Finish, onlyOver: CellType.Road);
            // Start on the first leg, facing along it, with 1.5 car lengths of road behind its centre (the road's round end).
            var (sx, sy) = _line[0];
            int dx = _route[1].C - _route[0].C, dy = _route[1].R - _route[0].R;
            double ahead = Math.Max(0, CarSize.HalfLength + CarSize.Length + 1 - _r);
            Start = new TrackStart((float)(sx + dx * ahead), (float)(sy + dy * ahead), dx == 1 ? 0 : dy == 1 ? 90 : dx == -1 ? 180 : 270);
            return true;
        }

        // ------------------------------------------------------------ coarse grid

        private bool In((int C, int R) c) => (uint)c.C < (uint)_cols && (uint)c.R < (uint)_rows;
        private int Index((int C, int R) c) => c.R * _cols + c.C;

        /// <summary>World centre of a coarse cell, on a cell centre (x.5) so roads are symmetric.</summary>
        private (double X, double Y) Centre((int C, int R) c) => (_offX + _cell * c.C + _cell / 2 + 0.5, _offY + _cell * c.R + _cell / 2 + 0.5);

        /// <summary>A random cell on the coarse grid's border and a direction pointing inwards from it.</summary>
        private ((int C, int R) Cell, (int Dx, int Dy) Dir) BorderStart()
        {
            var border = new List<(int, int)>();
            for (int r = 0; r < _rows; r++)
                for (int c = 0; c < _cols; c++)
                    if (c == 0 || r == 0 || c == _cols - 1 || r == _rows - 1) border.Add((c, r));
            var (sc, sr) = border[_rng.NextInt(border.Count)];
            var inward = new List<(int, int)>();
            if (sc == 0) inward.Add((1, 0));
            if (sc == _cols - 1) inward.Add((-1, 0));
            if (sr == 0) inward.Add((0, 1));
            if (sr == _rows - 1) inward.Add((0, -1));
            return ((sc, sr), inward[_rng.NextInt(inward.Count)]);
        }

        /// <summary>
        /// A self-avoiding walk from the border until its smoothed length reaches <paramref name="target"/>; backs up out of
        /// dead ends. At step i it goes straight on first with probability straightness(i), otherwise it tries the two turns
        /// first. Null when no such walk is found.
        /// </summary>
        private List<(int C, int R)>? Walk(double target, Func<int, double> straightness)
        {
            var (start, dir) = BorderStart();
            var path = new List<(int C, int R)> { start };
            var arrived = new List<(int Dx, int Dy)> { dir };
            var length = new List<double> { 0 };
            var options = new List<(int Dx, int Dy)[]> { Order(dir, straightness(0)) };
            var next = new List<int> { 0 };
            _used[Index(start)] = true;
            for (int step = 0; step < MaxWalkSteps; step++)
            {
                int top = path.Count - 1;
                if (length[top] >= target) return path;
                if (next[top] == options[top].Length)
                {
                    _used[Index(path[top])] = false;
                    if (top == 0) return null;
                    path.RemoveAt(top);
                    arrived.RemoveAt(top);
                    length.RemoveAt(top);
                    options.RemoveAt(top);
                    next.RemoveAt(top);
                    continue;
                }
                var d = options[top][next[top]++];
                var cell = (path[top].C + d.Dx, path[top].R + d.Dy);
                if (!In(cell) || _used[Index(cell)]) continue;
                _used[Index(cell)] = true;
                path.Add(cell);
                arrived.Add(d);
                length.Add(length[top] + _cell * (d == arrived[top] ? 1 : 1 - TurnShortening));
                options.Add(Order(d, straightness(path.Count - 1)));
                next.Add(0);
            }
            return null;
        }

        /// <summary>Straight on, left and right (never back), in the order to try them.</summary>
        private (int Dx, int Dy)[] Order((int Dx, int Dy) d, double straightness)
        {
            (int, int) a = (-d.Dy, d.Dx), b = (d.Dy, -d.Dx);
            if (_rng.NextInt(2) == 1) (a, b) = (b, a);
            return _rng.NextFloat() < straightness ? [d, a, b] : [a, b, d];
        }

        // ------------------------------------------------------------ Labyrinth

        /// <summary>
        /// A recursive-backtracker maze over the whole coarse grid. The route is, of all pairs of maze cells about target / cell
        /// steps apart, the one whose path passes the most side passages (the backtracker makes long corridors with few forks,
        /// so a random pair would have few dead ends). Kept are that route plus every dead end up to
        /// <see cref="GeneratorParams.DeadEndLength"/> cells off it (none at the start or the finish).
        /// </summary>
        private bool Labyrinth(double target)
        {
            int n = _cols * _rows;
            var links = new List<int>[n];
            for (int i = 0; i < n; i++) links[i] = new List<int>(4);
            var seen = new bool[n];
            var stack = new Stack<int>();
            int root = Index(BorderStart().Cell);
            stack.Push(root);
            seen[root] = true;
            var open = new List<int>(4);
            while (stack.Count > 0)
            {
                int cur = stack.Peek();
                open.Clear();
                foreach (var (dx, dy) in Dirs)
                {
                    var nb = (cur % _cols + dx, cur / _cols + dy);
                    if (In(nb) && !seen[Index(nb)]) open.Add(Index(nb));
                }
                if (open.Count == 0)
                {
                    stack.Pop();
                    continue;
                }
                int pick = open[_rng.NextInt(open.Count)];
                seen[pick] = true;
                links[cur].Add(pick);
                links[pick].Add(cur);
                stack.Push(pick);
            }

            int steps = Math.Max(2, (int)Math.Round(target / _cell));
            var order = Enumerable.Range(0, n).ToList();
            Shuffle(order);
            var prev = new int[n];
            var dist = new int[n];
            int start = -1, finish = -1, mostForks = -1;
            foreach (int a in order)
            {
                Distances(links, [a], prev, dist);
                foreach (int b in order)
                {
                    if (Math.Abs(dist[b] - steps) > 1) continue;
                    int forks = 0; // a route cell has 2 route neighbours; the rest are side passages
                    for (int i = prev[b]; i != a; i = prev[i]) forks += links[i].Count - 2;
                    if (forks > mostForks) (start, finish, mostForks) = (a, b, forks);
                }
            }
            if (start < 0) return false;

            Distances(links, [start], prev, dist);
            var route = new List<int> { finish };
            while (route[^1] != start) route.Add(prev[route[^1]]);
            route.Reverse();
            _route = route.ConvertAll(Cell);

            // Dead ends: the cells near the route, each joined to its neighbour one step closer to the route.
            Distances(links, route, prev, dist);
            for (int i = 0; i < n; i++)
            {
                if (dist[i] == 0 || dist[i] > _p.DeadEndLength) continue;
                int anchor = i;
                while (dist[anchor] > 0) anchor = prev[anchor];
                if (anchor == start || anchor == finish) continue;
                _corridors.Add([Centre(Cell(prev[i])), Centre(Cell(i))]);
            }
            return true;
        }

        private (int C, int R) Cell(int index) => (index % _cols, index / _cols);

        /// <summary>Breadth-first steps from the source cells over the maze's links, and each cell's neighbour one step closer.</summary>
        private static void Distances(List<int>[] links, IReadOnlyList<int> sources, int[] prev, int[] dist)
        {
            Array.Fill(dist, int.MaxValue);
            var queue = new Queue<int>();
            foreach (int s in sources)
            {
                dist[s] = 0;
                prev[s] = -1;
                queue.Enqueue(s);
            }
            while (queue.Count > 0)
            {
                int cur = queue.Dequeue();
                foreach (int nb in links[cur])
                {
                    if (dist[nb] != int.MaxValue) continue;
                    dist[nb] = dist[cur] + 1;
                    prev[nb] = cur;
                    queue.Enqueue(nb);
                }
            }
        }

        // ------------------------------------------------------------ branches (Wrong turns, Mixed)

        /// <summary><see cref="GeneratorParams.Branches"/> dead ends of 2–3 cells, each from its own route cell, heading for the finish.</summary>
        private void WrongTurns()
        {
            var junctions = new List<int>();
            for (int i = 2; i < _route.Count - 2; i++) junctions.Add(i);
            Shuffle(junctions);
            int made = 0;
            foreach (int j in junctions)
            {
                if (made == _p.Branches) break;
                if (Branch(j, 2 + _rng.NextInt(2), towardFinish: true)) made++;
            }
        }

        /// <summary>A dead end from route cell <paramref name="junction"/>: towards the finish (it must get closer at its first step) or random.</summary>
        private bool Branch(int junction, int length, bool towardFinish)
        {
            var finish = _route[^1];
            var tip = _route[junction];
            var cells = new List<(int C, int R)>();
            var options = new List<(int C, int R)>(4);
            for (int k = 0; k < length; k++)
            {
                options.Clear();
                foreach (var (dx, dy) in Dirs)
                {
                    var nb = (tip.C + dx, tip.R + dy);
                    if (In(nb) && !_used[Index(nb)]) options.Add(nb);
                }
                if (options.Count == 0) break;
                (int C, int R) pick;
                if (towardFinish)
                {
                    pick = options[0];
                    foreach (var o in options)
                        if (Dist2(o, finish) < Dist2(pick, finish)) pick = o;
                    if (k == 0 && Dist2(pick, finish) >= Dist2(tip, finish)) return false;
                }
                else pick = options[_rng.NextInt(options.Count)];
                _used[Index(pick)] = true;
                cells.Add(pick);
                tip = pick;
            }
            if (cells.Count == 0) return false;
            var at = Centre(_route[junction]);
            var corridor = new List<(double X, double Y)> { Nearest(at), at };
            corridor.AddRange(cells.ConvertAll(Centre));
            _corridors.Add(corridor);
            return true;

            static int Dist2((int C, int R) a, (int C, int R) b) => (a.C - b.C) * (a.C - b.C) + (a.R - b.R) * (a.R - b.R);
        }

        private void Shuffle(List<int> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int k = _rng.NextInt(i + 1);
                (list[i], list[k]) = (list[k], list[i]);
            }
        }

        // ------------------------------------------------------------ Mixed

        /// <summary>Mixed: the piece kind of every route step; pieces are 3–6 steps long, each of another kind than the one before.</summary>
        private List<Piece> Pieces()
        {
            var steps = new List<Piece>();
            Piece? last = null;
            while (steps.Count < 400)
            {
                var kind = (Piece)_rng.NextInt(5);
                if (kind == last) continue;
                last = kind;
                for (int i = 3 + _rng.NextInt(4); i > 0; i--) steps.Add(kind);
            }
            return steps;
        }

        /// <summary>A Wrong-turn piece gets one branch towards the finish, a Dead-ends piece two short random ones.</summary>
        private void MixedBranches(List<Piece> pieces)
        {
            foreach (var (kind, from, to) in Runs(pieces))
            {
                int lo = Math.Max(from, 2), hi = Math.Min(to, _route.Count - 2); // junctions lo … hi − 1
                if (hi <= lo) continue;
                if (kind == Piece.WrongTurn) Branch(lo + _rng.NextInt(hi - lo), 2, towardFinish: true);
                if (kind == Piece.DeadEnds)
                    for (int i = 0; i < 2; i++) Branch(lo + _rng.NextInt(hi - lo), 1 + _rng.NextInt(2), towardFinish: false);
            }
        }

        /// <summary>A Chicane piece: small danger bars from alternate edges every 14 cells where the road runs straight (the lane beside each is 1.5 cells over the road's half-width).</summary>
        private void Chicanes(List<Piece> pieces)
        {
            foreach (var (kind, from, to) in Runs(pieces))
            {
                if (kind != Piece.Chicane) continue;
                double s0 = Math.Max(NodeArc(from), 30), s1 = Math.Min(NodeArc(Math.Min(to, _route.Count - 1)), _arc[^1] - FinishDepth - 20);
                int side = _rng.NextInt(2) == 0 ? 1 : -1;
                for (double s = s0; s <= s1; s += 14)
                {
                    if (!Straight(s, 2 * _r + 10, 10)) continue;
                    Bar(s, side, _r + 1.5, BumpRadius, wall: false);
                    side = -side;
                }
            }
        }

        /// <summary>The pieces that the route reaches, as (kind, first route cell, end route cell).</summary>
        private IEnumerable<(Piece Kind, int From, int To)> Runs(List<Piece> pieces)
        {
            int n = Math.Min(pieces.Count, _route.Count);
            for (int i = 0; i < n;)
            {
                int j = i;
                while (j < n && pieces[j] == pieces[i]) j++;
                yield return (pieces[i], i, j);
                i = j;
            }
        }

        // ------------------------------------------------------------ Obstacle field

        /// <summary>
        /// <see cref="GeneratorParams.Obstacles"/> obstacles spread along the route (not near the start or finish, only where it
        /// runs straight): wall blocks lined with danger that reach in from alternate edges, leaving a <see cref="Lane"/> on the
        /// other side, and danger patches in the middle with a lane on both sides.
        /// </summary>
        private void Obstacles()
        {
            var spots = new List<double>(); // arc positions (every 2 cells) where the road runs straight
            for (double s = 30; s <= _arc[^1] - _r - FinishDepth - 25; s += 2)
                if (Straight(s, 2 * _r + 12, 12)) spots.Add(s);
            if (spots.Count == 0) return;
            double spacing = Math.Max(MinObstacleSpacing, spots.Count * 2.0 / _p.Obstacles), last = double.NegativeInfinity;
            int side = _rng.NextInt(2) == 0 ? 1 : -1, placed = 0;
            foreach (double s in spots)
            {
                if (placed == _p.Obstacles) break;
                if (s - last < spacing) continue;
                if (_r >= Lane + IslandRadius && _rng.NextInt(5) < 2) Island(s);
                else
                {
                    Bar(s, side, Lane, WallCore + Lining, wall: true);
                    side = -side;
                }
                last = s;
                placed++;
            }
        }

        /// <summary>
        /// A bar of half-thickness <paramref name="radius"/> across the road from the outer edge of one verge (side ±1 picks the
        /// edge) to where a lane of <paramref name="lane"/> cells is left at the other edge. A wall bar is a wall core lined with
        /// danger, so the road never touches the wall; otherwise it is all danger, over road and verge.
        /// </summary>
        private void Bar(double s, int side, double lane, double radius, bool wall)
        {
            var (qx, qy, tx, ty) = At(s);
            double nx = -ty * side, ny = tx * side;
            double outer = _r + _v, inner = -_r + lane + radius;
            if (inner >= outer) return;
            double ax = qx + nx * outer, ay = qy + ny * outer, bx = qx + nx * inner, by = qy + ny * inner;
            _b.Line(ax, ay, bx, by, radius, CellType.Danger, onlyOver: CellType.Road);
            if (wall) _b.Line(ax, ay, bx, by, WallCore, CellType.Wall);
            else _b.Line(ax, ay, bx, by, radius, CellType.Danger, onlyOver: CellType.Grass);
        }

        /// <summary>A danger disc somewhere across the middle of the road, leaving at least a <see cref="Lane"/> on both sides.</summary>
        private void Island(double s)
        {
            var (qx, qy, tx, ty) = At(s);
            double room = _r - Lane - IslandRadius; // the centre may sit up to this far off the middle
            double u = (_rng.NextFloat() * 2 - 1) * room;
            _b.Circle(qx - ty * u, qy + tx * u, IslandRadius, CellType.Danger, onlyOver: CellType.Road);
        }

        /// <summary>
        /// The route keeps its direction (within about 14°) from <paramref name="before"/> cells before arc position s to
        /// <paramref name="after"/> cells after it. An obstacle needs a straight run-up: a car leaving a bend is still swung out.
        /// </summary>
        private bool Straight(double s, double before, double after)
        {
            var (_, _, tx, ty) = At(s);
            foreach (double d in new[] { -before, -before / 2, after / 2, after })
            {
                var (_, _, ux, uy) = At(s + d);
                if (tx * ux + ty * uy < 0.97) return false;
            }
            return true;
        }

        // ------------------------------------------------------------ geometry

        /// <summary>Point and unit tangent at arc length s along the route (clamped to its ends).</summary>
        private (double X, double Y, double Tx, double Ty) At(double s)
        {
            int i = 1;
            while (i < _line.Count - 1 && _arc[i] < s) i++;
            var (ax, ay) = _line[i - 1];
            var (bx, by) = _line[i];
            double len = _arc[i] - _arc[i - 1];
            double t = len > 0 ? Math.Clamp((s - _arc[i - 1]) / len, 0, 1) : 0;
            return (ax + (bx - ax) * t, ay + (by - ay) * t, len > 0 ? (bx - ax) / len : 1, len > 0 ? (by - ay) / len : 0);
        }

        /// <summary>Arc length of the smoothed route's point nearest to route cell i's centre.</summary>
        private double NodeArc(int i)
        {
            var c = Centre(_route[i]);
            int best = 0;
            for (int k = 1; k < _line.Count; k++)
                if (Distance2(_line[k], c) < Distance2(_line[best], c)) best = k;
            return _arc[best];
        }

        private (double X, double Y) Nearest((double X, double Y) p)
        {
            var best = _line[0];
            foreach (var q in _line)
                if (Distance2(q, p) < Distance2(best, p)) best = q;
            return best;
        }

        /// <summary>Chaikin corner cutting, keeping both ends (the first and last legs keep their direction).</summary>
        private static List<(double X, double Y)> Smooth(List<(double X, double Y)> pts, int passes)
        {
            for (int k = 0; k < passes; k++)
            {
                var next = new List<(double X, double Y)>(pts.Count * 2) { pts[0] };
                for (int i = 0; i + 1 < pts.Count; i++)
                {
                    var (ax, ay) = pts[i];
                    var (bx, by) = pts[i + 1];
                    next.Add((0.75 * ax + 0.25 * bx, 0.75 * ay + 0.25 * by));
                    next.Add((0.25 * ax + 0.75 * bx, 0.25 * ay + 0.75 * by));
                }
                next.Add(pts[^1]);
                pts = next;
            }
            return pts;
        }

        private static double Distance2((double X, double Y) a, (double X, double Y) b) => (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y);
        private static double Distance((double X, double Y) a, (double X, double Y) b) => Math.Sqrt(Distance2(a, b));
    }
}

/// <summary>Why a generated track was rejected (<see cref="GenProblem.None"/> = valid).</summary>
public enum GenProblem { None, NoRoom, NotFinishable, NoRoadBehindStart, GrassShortcut, DriverFailed, TooShort, TooLong }

/// <summary>The result of <see cref="TrackGenerator.Validate"/>: the problem, the reference time (0 when it did not drive) and a sentence.</summary>
public sealed record GenCheck(GenProblem Problem, float ReferenceSeconds, string Text)
{
    public bool Valid => Problem == GenProblem.None;
}

/// <summary>
/// The result of <see cref="TrackGenerator.GenerateValid"/>: the valid track (null after <see cref="Attempts"/> failures), the
/// seed that made it, the number of seeds tried, the last check and every rejected seed with its reason.
/// </summary>
public sealed record GenResult(Track? Track, ulong Seed, int Attempts, GenCheck Check, IReadOnlyList<(ulong Seed, GenCheck Check)> Failed)
{
    /// <summary>For a failure: what went wrong how often, e.g. "No valid track in 50 tries (seeds 1–50): reference time over 40 s 31×, …".</summary>
    public string Summary
    {
        get
        {
            if (Track is not null) return $"Seed {Seed} ({Attempts} {(Attempts == 1 ? "try" : "tries")}): {Check.Text}.";
            var counts = Failed.GroupBy(f => f.Check.Problem).OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
                .Select(g => $"{Describe(g.Key)} {g.Count()}×");
            ulong last = unchecked(Seed + (ulong)Attempts - 1);
            return $"No valid track in {Attempts} {(Attempts == 1 ? "try" : "tries")} (seeds {Seed}–{last}): {string.Join(", ", counts)}.";
        }
    }

    /// <summary>For a failure: what to change, after the most common problem.</summary>
    public string Hint => Failed.GroupBy(f => f.Check.Problem).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).FirstOrDefault()?.Key switch
    {
        null => "",
        GenProblem.NoRoom => "The route does not fit: try a bigger map, a narrower road, a thinner verge or shorter times.",
        GenProblem.TooShort or GenProblem.TooLong => "Try a wider time range.",
        GenProblem.DriverFailed => "Try a wider road.",
        _ => "Try other parameters.",
    };

    public static string Describe(GenProblem problem) => problem switch
    {
        GenProblem.NoRoom => "the route did not fit on the map",
        GenProblem.NotFinishable => "not finishable",
        GenProblem.NoRoadBehindStart => "too little road behind the start",
        GenProblem.GrassShortcut => "grass shortcut",
        GenProblem.DriverFailed => "the reference driver did not finish",
        GenProblem.TooShort => "reference time under the shortest time",
        GenProblem.TooLong => "reference time over the longest time",
        _ => "valid",
    };
}
