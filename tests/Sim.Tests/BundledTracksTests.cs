using Nitrogenesis.Sim.Brain;
using Nitrogenesis.Sim.Map;
using Nitrogenesis.Sim.Racing;

/// <summary>Checks the committed tracks in <c>tracks/</c> against the authoring rules (PLAN §3.1, §3.2, §7).</summary>
public class BundledTracksTests
{
    private const double GrassCost = 1 / 0.45;

    /// <summary>
    /// Pinned track hashes (cross-checked in Python). A change means the bundled tracks changed, which breaks
    /// ghost matching and learn-suite comparisons: only update on purpose (regenerate with SimBench make-tracks).
    /// </summary>
    public static TheoryData<string, string> Tracks => new()
    {
        { "sprint.track", "65ef250c5d5e6f41be8bd1cebb1285984dbf9029d60c7c66fdb5fc8fb33c98f8" },
        { "s_curve.track", "7eeb245f69294a7b6216ce29748025629a1f88ca24cfbc6a1687e4ec2ffe199f" },
        { "hairpins.track", "b3cc5fa8485e4d90ee92b99c191d9d507e506ef5580ce9fa4cece2ce5371af7a" },
        { "wrong_turn.track", "bfbe1b800980ba7e5b3b6d247be2f914a87109765463dbf84c7c55e78b6df6b3" },
        { "grass_shortcut.track", "fc2be0274c475fe3c2f26aecdc1bb8e604cc7770b3d088581f51d9099764dd4b" },
        { "labyrinth.track", "0c0799f53bf6437e2d62d1f5c4389c97a650bd201edd49f4cc6a8fd1872bfd6f" },
        { "obstacle_field.track", "716e0de770c8809eb2c27feacbc6539823b94299427301001b6ceede4ef27c93" },
    };

    private static Track Load(string file) => TestTracks.Load(file);

    [Theory]
    [MemberData(nameof(Tracks))]
    public void HashIsPinned(string file, string hash) => Assert.Equal(hash, TrackHash.Compute(Load(file)));

    [Theory]
    [MemberData(nameof(Tracks))]
    public void IsMediumSizeSolvableAndHasNoGrassShortcut(string file, string _)
    {
        var t = Load(file);
        Assert.Equal((256, 144), (t.Grid.Width, t.Grid.Height));
        Assert.Equal(SolvabilityResult.Solvable, Solvability.Check(t, GrassCost));
        var shortcut = GrassShortcut.Check(t, GrassCost);
        Assert.False(shortcut.IsShortcut, $"road {shortcut.RoadOnly}, grass {shortcut.WithGrass}");
    }

    [Theory]
    [MemberData(nameof(Tracks))]
    public void RoadAloneIsAtLeastTwiceTheCarWidthFromStartToFinish(string file, string _)
    {
        // Turn grass into wall and require a corridor of clearance ≥ car width (so width ≥ 2 × car width).
        var t = Load(file);
        var roadOnly = t.Grid.Clone();
        new TrackBuilder(roadOnly).Fill(CellType.Wall, onlyOver: CellType.Grass);
        var clearance = Clearance.Compute(roadOnly);
        var field = DistanceField.ForRacing(roadOnly, clearance, CarSize.Width, GrassCost);
        var copy = new Track(t.Name, roadOnly, t.Start);
        Assert.Equal(SolvabilityResult.Solvable, Solvability.Check(copy, clearance, field, CarSize.Width));
    }

    [Theory]
    [MemberData(nameof(Tracks))]
    public void StartHasACarLengthOfRoadBehindIt(string file, string _)
    {
        var t = Load(file);
        float a = t.Start.AngleDeg * FastMath.DegToRad;
        float bx = -FastMath.Cos(a), by = -FastMath.Sin(a);
        // From the car centre back past its rear (half a length) plus one full car length.
        for (float d = 0; d <= CarSize.HalfLength + CarSize.Length; d += 0.25f)
        {
            float x = t.Start.X + bx * d, y = t.Start.Y + by * d;
            Assert.Equal(CellType.Road, t.Grid[(int)MathF.Floor(x), (int)MathF.Floor(y)]);
        }
    }

    /// <summary>
    /// The grass-shortcut regression track really tempts: over grass the route is far shorter in distance (grass
    /// counted like road), yet at the real grass cost it is no shortcut, so the road route is the right answer.
    /// </summary>
    [Fact]
    public void GrassShortcutTrackHasAShorterGrassRouteThatIsNotFaster()
    {
        var t = Load("grass_shortcut.track");
        var distance = GrassShortcut.Check(t, 1.0);
        Assert.True(distance.WithGrass < 0.85f * distance.RoadOnly, $"grass {distance.WithGrass}, road {distance.RoadOnly}");
        var time = GrassShortcut.Check(t, GrassCost);
        Assert.False(time.IsShortcut);
        Assert.Equal(time.RoadOnly, time.WithGrass); // the best route uses no grass at all
    }

    [Theory]
    [MemberData(nameof(Tracks))]
    public void VergesAreOneToThreeCellsAndRoadNeverTouchesWall(string file, string _)
    {
        var g = Load(file).Grid;
        bool apron = file == "grass_shortcut.track"; // its grass apron is the point of the track
        bool hasGrass = false;
        for (int y = 0; y < g.Height; y++)
            for (int x = 0; x < g.Width; x++)
            {
                CellType c = g[x, y];
                if (c == CellType.Grass)
                {
                    hasGrass = true;
                    Assert.True(apron || Near(g, x, y, 3, t => t is CellType.Road or CellType.Finish), $"grass at ({x},{y}) is over 3 cells from road");
                }
                else if (c is CellType.Road or CellType.Finish)
                {
                    Assert.False(Near(g, x, y, 1, t => t == CellType.Wall), $"road at ({x},{y}) touches a wall");
                }
            }
        Assert.True(hasGrass);
        Assert.Contains((byte)CellType.Finish, g.Cells);
    }

    private static bool Near(Grid g, int x, int y, int radius, Func<CellType, bool> match)
    {
        for (int dy = -radius; dy <= radius; dy++)
            for (int dx = -radius; dx <= radius; dx++)
                if (match(g[x + dx, y + dy])) return true;
        return false;
    }
}
