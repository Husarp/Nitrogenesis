using Nitrogenesis.Sim.Brain;
using Nitrogenesis.Sim.Map;

/// <summary>
/// The hand-made tracks shipped in <c>tracks/</c> (PLAN §8, M1; one per §9 learn-suite row plus the
/// grass-shortcut regression). All are M size (256×144), start as wall, have road at least 2× the car width,
/// grass verges of 2–3 cells (grass_shortcut also has a wide grass apron), at least one car length of road
/// behind the start, and a painted Finish zone. Coordinates are world units; centrelines sit on cell centres
/// (x.5) so roads are symmetric.
/// </summary>
internal static class BundledTracks
{
    private const int Width = 256, Height = 144;

    public static IEnumerable<(string File, Track Track)> All()
    {
        yield return ("sprint.track", Sprint());
        yield return ("s_curve.track", SCurve());
        yield return ("hairpins.track", Hairpins());
        yield return ("wrong_turn.track", WrongTurn());
        yield return ("grass_shortcut.track", GrassShortcut());
        yield return ("labyrinth.track", Labyrinth());
        yield return ("obstacle_field.track", ObstacleField());
    }

    /// <summary>Mostly straight, wide road with two gentle bends.</summary>
    private static Track Sprint()
    {
        var b = New();
        Road(b, [(8, 72.5), (60, 72.5), (130, 58.5), (196, 80.5), (244, 72.5)], radius: 6, verge: 2);
        b.Rect(236, 60, 251, 85, CellType.Finish, onlyOver: CellType.Road);
        return new Track("Sprint", b.Grid, new TrackStart(22.5f, 72.5f, 0));
    }

    /// <summary>One full sine period between straight lead-in and lead-out: an S.</summary>
    private static Track SCurve()
    {
        var points = new List<(double, double)> { (8, 72.5) };
        for (int x = 40; x <= 216; x += 4)
            points.Add((x, 72.5 - 44 * FastMath.Sin(FastMath.TwoPi * (x - 40) / 176f)));
        points.Add((246, 72.5));
        var b = New();
        Road(b, points, radius: 5, verge: 2);
        b.Rect(234, 60, 252, 85, CellType.Finish, onlyOver: CellType.Road);
        return new Track("S-curve", b.Grid, new TrackStart(20.5f, 72.5f, 0));
    }

    /// <summary>Four rows joined by three 180° turns of radius 12 (right, left, right); finish at the left.</summary>
    private static Track Hairpins()
    {
        double[] rows = [36.5, 60.5, 84.5, 108.5];
        const double left = 68, right = 188, turn = 12;
        var points = new List<(double, double)> { (56, rows[0]) };
        for (int r = 0; r < rows.Length - 1; r++)
        {
            double cy = rows[r] + turn;
            if (r % 2 == 0) points.AddRange(Arc(right, cy, turn, -90, 90));   // clockwise round the right end
            else points.AddRange(Arc(left, cy, turn, 270, 90));               // anticlockwise round the left end
        }
        points.Add((52, rows[^1]));
        var b = New();
        Road(b, points, radius: 4, verge: 2);
        b.Rect(44, 100, 60, 117, CellType.Finish, onlyOver: CellType.Road);
        return new Track("Hairpins", b.Grid, new TrackStart(66.5f, 36.5f, 0));
    }

    /// <summary>
    /// The real route goes up, across the top and down to the finish. Two dead ends look like shortcuts:
    /// one straight on from the first corner towards the finish, one dropping from the top road towards it.
    /// </summary>
    private static Track WrongTurn()
    {
        var b = New();
        Road(b, [(8, 72.5), (40, 72.5), (40, 18.5), (216, 18.5), (216, 72.5), (248, 72.5)], radius: 4, verge: 2);
        Road(b, [(40, 72.5), (112, 72.5)], radius: 4, verge: 2);
        Road(b, [(150, 18.5), (150, 58.5)], radius: 4, verge: 2);
        b.Rect(236, 60, 253, 85, CellType.Finish, onlyOver: CellType.Road);
        return new Track("Wrong turn", b.Grid, new TrackStart(20.5f, 72.5f, 0));
    }

    /// <summary>
    /// Regression for grass cutting (PLAN §3.2, §9): east along the top, into a sharp V (legs 70° apart, apex on
    /// the right), and back west along the bottom. The inside of the V is grass. Straight across it is much
    /// shorter in distance, but at 70° the crossing is over half as long as the two legs it skips, and grass costs
    /// 1 / 0.45 per cell, so it is slower than the road: the best route must stay on the road. With grass as fast
    /// as road the cut wins (the learn-suite's control).
    /// </summary>
    private static Track GrassShortcut()
    {
        const double top = 12.5, bottom = 132.5, mouthX = 130, apexX = 216;
        var b = New();
        Road(b, [(8, top), (mouthX, top), (apexX, (top + bottom) / 2), (mouthX, bottom), (8, bottom)], radius: 5, verge: 3);
        double slope = (bottom - top) / 2 / (apexX - mouthX);
        for (int x = (int)mouthX; x < apexX; x++) // grass inside the V, column by column
        {
            double inset = (x + 0.5 - mouthX) * slope;
            b.Line(x + 0.5, top + inset, x + 0.5, bottom - inset, 0.6, CellType.Grass, onlyOver: CellType.Wall);
        }
        b.Rect(4, 120, 20, 143, CellType.Finish, onlyOver: CellType.Road);
        return new Track("Grass shortcut", b.Grid, new TrackStart(22.5f, (float)top, 0));
    }

    /// <summary>
    /// A maze on a 16-cell coarse grid (PLAN §7 "Labyrinth"): one route with eight turns, and dead ends of two or
    /// more coarse cells at its junctions, on both sides (so neither "keep left" nor "keep right" gets through)
    /// and some pointing towards the finish.
    /// </summary>
    private static Track Labyrinth()
    {
        // Coarse node (c, r) is the world point (8.5 + 16c, 8.5 + 16r).
        (int, int)[][] corridors =
        [
            [(0, 4), (2, 4), (2, 1), (5, 1), (5, 5), (8, 5), (8, 2), (11, 2), (11, 6), (15, 6)], // the route
            [(2, 4), (4, 4), (4, 3)],   // straight on at the first corner, towards the finish
            [(2, 4), (2, 7), (0, 7)],   // right at the first corner
            [(2, 1), (0, 1), (0, 2)],   // left at the second corner
            [(5, 1), (7, 1)],           // straight on, towards the finish
            [(5, 5), (5, 7), (7, 7)],   // straight on
            [(5, 5), (3, 5)],           // right
            [(8, 5), (10, 5), (10, 7)], // straight on, towards the finish
            [(8, 2), (8, 0)],           // straight on before the last turn
            [(8, 3), (6, 3)],           // left, in the middle of a leg
            [(11, 2), (13, 2), (13, 0)], // straight on
            [(11, 4), (13, 4)],         // left, towards the finish
            [(11, 6), (11, 8), (14, 8)], // straight on, alongside the finish
        ];
        var b = New();
        foreach (var corridor in corridors)
            Road(b, corridor.Select(n => (8.5 + 16 * n.Item1, 8.5 + 16 * n.Item2)).ToList(), radius: 3, verge: 2);
        b.Rect(238, 96, 253, 113, CellType.Finish, onlyOver: CellType.Road);
        return new Track("Labyrinth", b.Grid, new TrackStart(13.5f, 72.5f, 0));
    }

    /// <summary>
    /// A wide U-shaped road with wall blocks (PLAN §7 "Obstacle field"): east along the top, round, and back west
    /// along the bottom. Six blocks per side stick out alternately from the two edges, leaving 4-cell lanes (just
    /// over 2× the car width) that cars must weave through, and each is lined with danger: a car that bumps a
    /// block is destroyed instead of sliding past it. Narrow lanes are what defeats random search (the control).
    /// </summary>
    private static Track ObstacleField()
    {
        const int radius = 14, topRow = 36, bottomRow = 108; // road centrelines at y = 36.5 and 108.5
        const double turnX = 200;
        var points = new List<(double, double)> { (17, topRow + 0.5) };
        points.AddRange(Arc(turnX, (topRow + bottomRow + 1) / 2.0, (bottomRow - topRow) / 2.0, -90, 90));
        points.Add((17, bottomRow + 0.5));
        var b = New();
        Road(b, points, radius: radius, verge: 2);
        foreach (int row in new[] { topRow, bottomRow })
        {
            bool eastbound = row == topRow;
            for (int i = 0; i < 6; i++)
            {
                int left = eastbound ? 40 + 26 * i : 171 - 26 * i; // wall core columns left … left + 7
                bool fromUpperEdge = i % 2 == 0;
                int y0 = fromUpperEdge ? row - radius - 3 : row + radius - 22, y1 = fromUpperEdge ? row - radius + 22 : row + radius + 3;
                b.Rect(left - 2, y0 - 2, left + 9, y1 + 2, CellType.Danger, onlyOver: CellType.Road); // danger lining
                b.Rect(left, y0, left + 7, y1, CellType.Wall);
            }
        }
        b.Rect(4, bottomRow - radius, 20, bottomRow + radius, CellType.Finish, onlyOver: CellType.Road);
        return new Track("Obstacle field", b.Grid, new TrackStart(20.5f, topRow + 0.5f, 0));
    }

    private static TrackBuilder New() => new(new Grid(Width, Height));

    /// <summary>Road of the given radius along the points, with a grass verge that only replaces wall.</summary>
    private static void Road(TrackBuilder b, IReadOnlyList<(double, double)> points, double radius, double verge)
    {
        b.Polyline(points, radius + verge, CellType.Grass, onlyOver: CellType.Wall);
        b.Polyline(points, radius, CellType.Road);
    }

    /// <summary>Points on a circular arc every 5°, angles in degrees (0 = +x, 90 = +y, i.e. down on screen).</summary>
    private static IEnumerable<(double, double)> Arc(double cx, double cy, double radius, int fromDeg, int toDeg)
    {
        int step = toDeg > fromDeg ? 5 : -5;
        for (int a = fromDeg; a != toDeg + step; a += step)
            yield return (cx + radius * FastMath.Cos(a * FastMath.DegToRad), cy + radius * FastMath.Sin(a * FastMath.DegToRad));
    }
}
