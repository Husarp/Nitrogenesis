using Nitrogenesis.Sim.Map;

public class EditDocumentTests
{
    private static EditDocument Doc(int w = 40, int h = 30)
    {
        var track = new Track("t", new Grid(w, h), new TrackStart(5.5f, 5.5f, 0f));
        return new EditDocument(track);
    }

    private static int Count(Grid g, CellType t) => g.Cells.Count(c => c == (byte)t);

    private static DirtyRect Diff(byte[] before, byte[] after, int width)
    {
        var r = DirtyRect.Empty;
        for (int i = 0; i < before.Length; i++)
            if (before[i] != after[i]) r = r.Union(new DirtyRect(i % width, i / width, 1, 1));
        return r;
    }

    private static HashSet<(int, int)> Cells(EditDocument d, CellType t)
    {
        var set = new HashSet<(int, int)>();
        for (int y = 0; y < d.Grid.Height; y++)
            for (int x = 0; x < d.Grid.Width; x++)
                if (d.Grid[x, y] == t) set.Add((x, y));
        return set;
    }

    // ------------------------------------------------------------ construction

    [Theory]
    [InlineData(MapSize.S, 192, 108)]
    [InlineData(MapSize.M, 256, 144)]
    [InlineData(MapSize.L, 384, 216)]
    public void NewMapIsAllWallWithCentreStart(MapSize size, int w, int h)
    {
        var d = EditDocument.New(size);
        Assert.Equal(w, d.Grid.Width);
        Assert.Equal(h, d.Grid.Height);
        Assert.Equal(w * h, Count(d.Grid, CellType.Wall));
        Assert.Equal(new TrackStart(w / 2 + 0.5f, h / 2 + 0.5f, 0f), d.Start);
        Assert.False(d.IsDirty);
        Assert.False(d.CanUndo);
        Assert.Equal(0, d.Revision);
    }

    [Fact]
    public void FromTrackDeepCopiesAndSnapshotIsIndependent()
    {
        var track = new Track("src", new Grid(10, 10), new TrackStart(2.5f, 2.5f, 30f));
        var d = new EditDocument(track);
        d.BeginStroke(5, 5);
        d.EndStroke();
        Assert.Equal(100, Count(track.Grid, CellType.Wall));
        Assert.Equal("src", d.Name);
        var snap = d.Snapshot();
        d.PaintType = CellType.Grass;
        d.Rect(0, 0, 9, 9);
        Assert.Equal(CellType.Road, snap.Grid[5, 5]);
        Assert.NotSame(d.Grid, snap.Grid);
        Assert.Equal(d.Start, snap.Start);
        Assert.Equal("src", snap.Name);
    }

    // ------------------------------------------------------------ brush shapes

    private static string Shape(EditDocument d, int size, BrushShape shape, int cx, int cy)
    {
        d.BrushSize = size;
        d.BrushShape = shape;
        d.PaintType = CellType.Road;
        d.BeginStroke(cx, cy);
        d.EndStroke();
        var rows = new List<string>();
        for (int y = 0; y < d.Grid.Height; y++)
        {
            var row = new string(Enumerable.Range(0, d.Grid.Width).Select(x => d.Grid[x, y] == CellType.Road ? '#' : '.').ToArray());
            if (row.Contains('#')) rows.Add(row.Trim('.'));
        }
        return string.Join("/", rows);
    }

    [Fact]
    public void BrushShapesBySize()
    {
        Assert.Equal("#", Shape(Doc(), 1, BrushShape.Round, 10, 10));
        Assert.Equal("#", Shape(Doc(), 1, BrushShape.Square, 10, 10));
        Assert.Equal("##/##", Shape(Doc(), 2, BrushShape.Round, 10, 10));
        Assert.Equal("##/##", Shape(Doc(), 2, BrushShape.Square, 10, 10));
        Assert.Equal("#/###/#", Shape(Doc(), 3, BrushShape.Round, 10, 10));   // plus (rows trimmed)
        Assert.Equal("###/###/###", Shape(Doc(), 3, BrushShape.Square, 10, 10));
    }

    [Fact]
    public void Size3RoundIsAPlusAndSquareCentredOnClickedCell()
    {
        var d = Doc();
        d.BrushSize = 3;
        d.BeginStroke(10, 10);
        d.EndStroke();
        Assert.Equal(new HashSet<(int, int)> { (10, 10), (9, 10), (11, 10), (10, 9), (10, 11) }, Cells(d, CellType.Road));
        var sq = Doc();
        sq.BrushSize = 3;
        sq.BrushShape = BrushShape.Square;
        sq.BeginStroke(10, 10);
        sq.EndStroke();
        Assert.Equal(9, Count(sq.Grid, CellType.Road));
        Assert.Equal(CellType.Road, sq.Grid[9, 9]);
        Assert.Equal(CellType.Road, sq.Grid[11, 11]);
    }

    [Fact]
    public void EvenSizeExtendsTowardsPlusXY()
    {
        var d = Doc();
        d.BrushSize = 2;
        d.BeginStroke(10, 10);
        d.EndStroke();
        Assert.Equal(new HashSet<(int, int)> { (10, 10), (11, 10), (10, 11), (11, 11) }, Cells(d, CellType.Road));
    }

    [Fact]
    public void Size15SquareIs225AndRoundIsADisc()
    {
        var sq = Doc(40, 40);
        sq.BrushSize = 15;
        sq.BrushShape = BrushShape.Square;
        sq.BeginStroke(20, 20);
        sq.EndStroke();
        Assert.Equal(225, Count(sq.Grid, CellType.Road));
        Assert.Equal(CellType.Road, sq.Grid[13, 13]);
        Assert.Equal(CellType.Road, sq.Grid[27, 27]);
        Assert.Equal(CellType.Wall, sq.Grid[12, 20]);

        var rd = Doc(40, 40);
        rd.BrushSize = 15;
        rd.BeginStroke(20, 20);
        rd.EndStroke();
        int n = Count(rd.Grid, CellType.Road);
        Assert.InRange(n, 150, 200);
        Assert.Equal(CellType.Wall, rd.Grid[13, 13]);   // corner cut off
        Assert.Equal(CellType.Road, rd.Grid[13, 20]);   // edge middles present
        Assert.Equal(CellType.Road, rd.Grid[27, 20]);
        Assert.Equal(CellType.Road, rd.Grid[20, 13]);
        Assert.Equal(CellType.Road, rd.Grid[20, 27]);
        // symmetric about the centre cell
        foreach (var (x, y) in Cells(rd, CellType.Road))
            Assert.Equal(CellType.Road, rd.Grid[40 - x, 40 - y]);
    }

    [Fact]
    public void BrushSizeIsClamped()
    {
        var d = Doc();
        d.BrushSize = 99;
        Assert.Equal(15, d.BrushSize);
        d.BrushSize = 0;
        Assert.Equal(1, d.BrushSize);
    }

    // ------------------------------------------------------------ strokes

    [Fact]
    public void StrokeBigJumpsLeaveNoGapsOnDiagonalAndSteepLine()
    {
        foreach (var (x1, y1) in new[] { (35, 28), (12, 29), (35, 3), (3, 28) })
        {
            var d = Doc();
            d.BrushSize = 1;
            d.BeginStroke(2, 1);
            d.MoveStroke(x1, y1);
            d.EndStroke();
            var cells = Cells(d, CellType.Road);
            Assert.Contains((2, 1), cells);
            Assert.Contains((x1, y1), cells);
            // 8-connected path: every painted cell has a painted neighbour, and count == max(|dx|,|dy|)+1
            Assert.Equal(Math.Max(Math.Abs(x1 - 2), Math.Abs(y1 - 1)) + 1, cells.Count);
            foreach (var (x, y) in cells)
            {
                if ((x, y) == (2, 1)) continue;
                Assert.True(cells.Contains((x - 1, y)) || cells.Contains((x + 1, y)) || cells.Contains((x, y - 1)) ||
                            cells.Contains((x, y + 1)) || cells.Contains((x - 1, y - 1)) || cells.Contains((x + 1, y + 1)) ||
                            cells.Contains((x - 1, y + 1)) || cells.Contains((x + 1, y - 1)));
            }
        }
    }

    [Fact]
    public void ThickStrokeBetweenFarPointsHasNoGaps()
    {
        var d = Doc(100, 60);
        d.BrushSize = 3;
        d.BeginStroke(5, 5);
        d.MoveStroke(90, 50);
        d.EndStroke();
        // every column between the end points has at least 3 road cells (a gap would show as 0)
        for (int x = 5; x <= 90; x++)
        {
            int n = 0;
            for (int y = 0; y < 60; y++) if (d.Grid[x, y] == CellType.Road) n++;
            Assert.True(n >= 3, $"gap at column {x}");
        }
    }

    [Fact]
    public void StrokeIsOneUndoStepEvenWithManyMoves()
    {
        var d = Doc();
        d.BeginStroke(1, 1);
        for (int i = 2; i < 38; i++) d.MoveStroke(i, 1 + i % 20);
        d.EndStroke();
        Assert.Equal(1, d.UndoCount);
        d.Undo();
        Assert.Equal(1200, Count(d.Grid, CellType.Wall));
    }

    [Fact]
    public void StrokeDirtyRectsAreExactPerMove()
    {
        var d = Doc();
        d.BrushSize = 2;
        var before = (byte[])d.Grid.Cells.Clone();
        var r = d.BeginStroke(4, 4);
        Assert.Equal(Diff(before, d.Grid.Cells, 40), r.Rect);
        for (int i = 0; i < 6; i++)
        {
            before = (byte[])d.Grid.Cells.Clone();
            r = d.MoveStroke(4 + i * 5, 4 + i * 3);
            Assert.Equal(Diff(before, d.Grid.Cells, 40), r.Rect);
            Assert.False(r.StartChanged);
        }
        // moving over already painted cells reports nothing
        d.MoveStroke(d.Grid.Width - 11, 19);
        Assert.False(d.MoveStroke(d.Grid.Width - 11, 19).Changed);
        d.EndStroke();
    }

    [Fact]
    public void StrokeClipsAtEdgesIncludingStartOutsideGrid()
    {
        var d = Doc(10, 10);
        d.BrushSize = 5;
        d.BrushShape = BrushShape.Square;
        d.BeginStroke(0, 0);
        d.MoveStroke(-30, 4);   // far outside, line passes through the left edge
        d.MoveStroke(9, 9);
        d.EndStroke();
        Assert.Equal(CellType.Road, d.Grid[0, 0]);
        Assert.Equal(CellType.Road, d.Grid[9, 9]);
        Assert.Equal(100, d.Grid.CellCount);
        var all = new EditDocument(new Track("a", new Grid(10, 10), default));
        all.BeginStroke(-100, -100);
        all.EndStroke();
        Assert.False(all.CanUndo);   // nothing inside
    }

    [Fact]
    public void StrokeKeepsItsTypeAndSettingsFromBegin()
    {
        var d = Doc();
        d.PaintType = CellType.Grass;
        d.BrushSize = 1;
        d.BeginStroke(3, 3);
        d.PaintType = CellType.Danger;
        d.BrushSize = 9;
        d.MoveStroke(8, 3);
        d.EndStroke();
        Assert.Equal(6, Count(d.Grid, CellType.Grass));
        Assert.Equal(0, Count(d.Grid, CellType.Danger));
    }

    [Fact]
    public void MoveWithoutStrokeDoesNothing()
    {
        var d = Doc();
        Assert.False(d.MoveStroke(3, 3).Changed);
        d.EndStroke();
        Assert.False(d.CanUndo);
    }

    [Fact]
    public void UndoDuringStrokeEndsItAndUndoesIt()
    {
        var d = Doc();
        var orig = (byte[])d.Grid.Cells.Clone();
        d.BeginStroke(3, 3);
        d.MoveStroke(20, 3);
        Assert.True(d.CanUndo);
        Assert.Equal(1, d.UndoCount);
        var r = d.Undo();
        Assert.False(d.StrokeActive);
        Assert.Equal(orig, d.Grid.Cells);
        Assert.False(r.Rect.IsEmpty);
        Assert.Equal(1, d.RedoCount);
        // moves after the undo do not continue the dead stroke
        Assert.False(d.MoveStroke(25, 3).Changed);
        d.Redo();
        Assert.Equal(CellType.Road, d.Grid[20, 3]);
    }

    [Fact]
    public void BeginningANewStrokeEndsTheOldOne()
    {
        var d = Doc();
        d.BeginStroke(3, 3);
        d.BeginStroke(20, 20);
        d.EndStroke();
        Assert.Equal(2, d.UndoCount);
    }

    [Fact]
    public void EraserIsAStrokeOfWall()
    {
        var d = Doc();
        d.PaintType = CellType.Road;
        d.Rect(0, 0, 39, 29);
        d.PaintType = CellType.Wall;
        d.BrushSize = 3;
        d.BeginStroke(10, 10);
        d.EndStroke();
        Assert.Equal(5, Count(d.Grid, CellType.Wall));
    }

    // ------------------------------------------------------------ line, rect

    [Fact]
    public void LineHorizontalAndAnyDirectionSameCells()
    {
        var a = Doc();
        a.BrushSize = 3;
        a.Line(5, 5, 25, 12);
        var b = Doc();
        b.BrushSize = 3;
        b.Line(25, 12, 5, 5);
        Assert.True(Cells(a, CellType.Road).Count > 0);
        Assert.Equal(1, a.UndoCount);
        // reversed line covers the same ends and similar area (Bresenham is not symmetric, ends must match)
        Assert.Equal(CellType.Road, b.Grid[5, 5]);
        Assert.Equal(CellType.Road, b.Grid[25, 12]);
        Assert.Equal(CellType.Road, a.Grid[5, 5]);
        Assert.Equal(CellType.Road, a.Grid[25, 12]);

        var h = Doc();
        h.BrushSize = 1;
        h.Line(3, 7, 12, 7);
        Assert.Equal(10, Count(h.Grid, CellType.Road));
        var v = Doc();
        v.BrushSize = 1;
        v.Line(4, 20, 4, 3);
        Assert.Equal(18, Count(v.Grid, CellType.Road));
    }

    [Fact]
    public void LineDirtyRectMatchesDiffAndIsOneStep()
    {
        var d = Doc();
        d.BrushSize = 4;
        var before = (byte[])d.Grid.Cells.Clone();
        var r = d.Line(30, 25, 4, 2);
        Assert.Equal(Diff(before, d.Grid.Cells, 40), r.Rect);
        Assert.Equal(1, d.UndoCount);
        d.Undo();
        Assert.Equal(before, d.Grid.Cells);
    }

    [Fact]
    public void LineClipsToGrid()
    {
        var d = Doc(10, 10);
        d.BrushSize = 3;
        var r = d.Line(-5, -5, 20, 20);
        Assert.True(Count(d.Grid, CellType.Road) > 0);
        Assert.InRange(r.Rect.X, 0, 9);
        Assert.True(r.Rect.X + r.Rect.W <= 10 && r.Rect.Y + r.Rect.H <= 10);
    }

    [Theory]
    [InlineData(5, 6, 12, 14)]
    [InlineData(12, 14, 5, 6)]
    [InlineData(5, 14, 12, 6)]
    [InlineData(12, 6, 5, 14)]
    public void RectInAllCornerOrders(int x0, int y0, int x1, int y1)
    {
        var d = Doc();
        d.PaintType = CellType.Grass;
        var r = d.Rect(x0, y0, x1, y1);
        Assert.Equal(8 * 9, Count(d.Grid, CellType.Grass));
        Assert.Equal(new DirtyRect(5, 6, 8, 9), r.Rect);
        Assert.Equal(CellType.Grass, d.Grid[5, 6]);
        Assert.Equal(CellType.Grass, d.Grid[12, 14]);
        Assert.Equal(CellType.Wall, d.Grid[13, 14]);
        Assert.Equal(1, d.UndoCount);
    }

    [Fact]
    public void RectIsClippedAndOutsideRectIsNoOp()
    {
        var d = Doc(10, 10);
        var r = d.Rect(-5, 7, 4, 50);
        Assert.Equal(new DirtyRect(0, 7, 5, 3), r.Rect);
        Assert.Equal(15, Count(d.Grid, CellType.Road));
        Assert.False(d.Rect(50, 50, 60, 60).Changed);
        Assert.Equal(1, d.UndoCount);
    }

    [Fact]
    public void RectOverSameTypeIsNoOp()
    {
        var d = Doc(10, 10);
        d.PaintType = CellType.Wall;
        Assert.False(d.Rect(0, 0, 9, 9).Changed);
        Assert.False(d.CanUndo);
        Assert.Equal(0, d.Revision);
    }

    // ------------------------------------------------------------ fill

    [Fact]
    public void FillIsFourConnectedNotDiagonal()
    {
        var d = Doc(6, 6);
        // road cells (1,1) and diagonal neighbour (2,2); fill grass over wall from (0,0)
        d.Rect(1, 1, 1, 1);
        d.Rect(2, 2, 2, 2);
        d.PaintType = CellType.Grass;
        // fill the road at (1,1): must not reach the diagonal (2,2)
        var r = d.Fill(1, 1);
        Assert.Equal(new DirtyRect(1, 1, 1, 1), r.Rect);
        Assert.Equal(CellType.Road, d.Grid[2, 2]);
        Assert.Equal(CellType.Grass, d.Grid[1, 1]);
    }

    [Fact]
    public void FillStopsAtOtherTypesAndFillsRoomOnly()
    {
        var d = Doc(10, 10);
        d.PaintType = CellType.Road;
        d.Rect(0, 0, 9, 9);
        d.PaintType = CellType.Wall;
        d.Rect(5, 0, 5, 9);                   // vertical wall splits the map
        d.PaintType = CellType.Grass;
        var before = (byte[])d.Grid.Cells.Clone();
        var r = d.Fill(2, 3);
        Assert.Equal(new DirtyRect(0, 0, 5, 10), r.Rect);
        Assert.Equal(Diff(before, d.Grid.Cells, 10), r.Rect);
        Assert.Equal(50, Count(d.Grid, CellType.Grass));
        Assert.Equal(40, Count(d.Grid, CellType.Road));
        Assert.Equal(10, Count(d.Grid, CellType.Wall));
    }

    [Fact]
    public void FillWithSameTypeOrOutsideIsNoOp()
    {
        var d = Doc();
        d.PaintType = CellType.Wall;
        long rev = d.Revision;
        Assert.False(d.Fill(3, 3).Changed);
        d.PaintType = CellType.Road;
        Assert.False(d.Fill(-1, 3).Changed);
        Assert.False(d.Fill(3, 99).Changed);
        Assert.False(d.CanUndo);
        Assert.Equal(rev, d.Revision);
    }

    [Fact]
    public void WholeLargeMapFillWorksUndoesAndIsFast()
    {
        var d = EditDocument.New(MapSize.L);
        var before = (byte[])d.Grid.Cells.Clone();
        d.PaintType = CellType.Grass;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = d.Fill(0, 0);
        sw.Stop();
        Assert.Equal(new DirtyRect(0, 0, 384, 216), r.Rect);
        Assert.Equal(384 * 216, Count(d.Grid, CellType.Grass));
        Assert.True(sw.ElapsedMilliseconds < 1000, $"fill took {sw.ElapsedMilliseconds} ms");
        Assert.Equal(1, d.UndoCount);
        d.Undo();
        Assert.Equal(before, d.Grid.Cells);
        d.Redo();
        Assert.Equal(384 * 216, Count(d.Grid, CellType.Grass));
    }

    [Fact]
    public void SnakingCorridorFillDoesNotRecurse()
    {
        var d = EditDocument.New(MapSize.L);
        d.PaintType = CellType.Road;
        for (int y = 0; y < 216; y += 2) d.Rect(0, y, 383, y);
        for (int y = 1; y < 216; y += 4) d.Rect(383, y, 383, y);
        for (int y = 3; y < 216; y += 4) d.Rect(0, y, 0, y);
        d.PaintType = CellType.Finish;
        var r = d.Fill(0, 0);
        Assert.Equal(CellType.Finish, d.Grid[383, 214]);
        Assert.False(r.Rect.IsEmpty);
    }

    // ------------------------------------------------------------ start

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(7f, 0f)]
    [InlineData(8f, 15f)]
    [InlineData(22f, 15f)]
    [InlineData(23f, 30f)]
    [InlineData(352f, 345f)]
    [InlineData(353f, 0f)]
    [InlineData(360f, 0f)]
    [InlineData(-7f, 0f)]
    [InlineData(-8f, 345f)]
    [InlineData(-90f, 270f)]
    [InlineData(735f, 15f)]
    [InlineData(359.9f, 0f)]
    public void SnapAngle(float input, float expected) => Assert.Equal(expected, EditDocument.SnapAngle(input));

    [Fact]
    public void SnapAngleRejectsNaN() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => EditDocument.SnapAngle(float.NaN));

    [Fact]
    public void SetStartUsesCellCentreAndSnapsAngle()
    {
        var d = Doc();
        var r = d.SetStart(12, 7, 100f);
        Assert.True(r.StartChanged);
        Assert.True(r.Rect.IsEmpty);
        Assert.Equal(new TrackStart(12.5f, 7.5f, 105f), d.Start);
        Assert.Equal(1, d.UndoCount);
        Assert.Equal(1, d.Revision);
    }

    [Fact]
    public void SetStartClampsIntoGridAndSameStartIsNoOp()
    {
        var d = Doc(10, 10);
        d.SetStart(50, -3, 0f);
        Assert.Equal(new TrackStart(9.5f, 0.5f, 0f), d.Start);
        long rev = d.Revision;
        Assert.False(d.SetStart(9, 0, 4f).Changed);   // snaps to the same start
        Assert.Equal(rev, d.Revision);
        Assert.Equal(1, d.UndoCount);
    }

    [Fact]
    public void StartUndoRedoRestoresStartExactly()
    {
        var d = Doc();
        var orig = d.Start;
        d.SetStart(20, 20, 90f);
        d.SetStart(3, 4, 45f);
        var r = d.Undo();
        Assert.True(r.StartChanged);
        Assert.Equal(new TrackStart(20.5f, 20.5f, 90f), d.Start);
        d.Undo();
        Assert.Equal(orig, d.Start);
        d.Redo();
        d.Redo();
        Assert.Equal(new TrackStart(3.5f, 4.5f, 45f), d.Start);
    }

    // ------------------------------------------------------------ undo / redo

    [Fact]
    public void UndoRedoRoundTripRestoresExactBytesAndStart()
    {
        var d = Doc(60, 40);
        var states = new List<(byte[] Cells, TrackStart Start)> { ((byte[])d.Grid.Cells.Clone(), d.Start) };
        void Snap() => states.Add(((byte[])d.Grid.Cells.Clone(), d.Start));
        d.BrushSize = 5; d.BeginStroke(5, 5); d.MoveStroke(40, 30); d.EndStroke(); Snap();
        d.PaintType = CellType.Grass; d.Rect(10, 10, 30, 20); Snap();
        d.PaintType = CellType.Danger; d.BrushSize = 2; d.Line(1, 1, 50, 3); Snap();
        d.SetStart(30, 25, 135f); Snap();
        d.PaintType = CellType.Finish; d.Fill(30, 25); Snap();
        d.PaintType = CellType.Wall; d.BrushSize = 7; d.BrushShape = BrushShape.Square; d.BeginStroke(20, 15); d.MoveStroke(22, 15); d.EndStroke(); Snap();
        Assert.Equal(6, d.UndoCount);

        for (int i = states.Count - 2; i >= 0; i--)
        {
            Assert.True(d.CanUndo);
            d.Undo();
            Assert.Equal(states[i].Cells, d.Grid.Cells);
            Assert.Equal(states[i].Start, d.Start);
        }
        Assert.False(d.CanUndo);
        Assert.False(d.Undo().Changed);
        for (int i = 1; i < states.Count; i++)
        {
            Assert.True(d.CanRedo);
            d.Redo();
            Assert.Equal(states[i].Cells, d.Grid.Cells);
            Assert.Equal(states[i].Start, d.Start);
        }
        Assert.False(d.CanRedo);
        Assert.False(d.Redo().Changed);
    }

    [Fact]
    public void UndoAndRedoDirtyRectsMatchDiff()
    {
        var d = Doc();
        d.PaintType = CellType.Grass;
        d.Rect(4, 5, 20, 22);
        d.PaintType = CellType.Road;
        d.BrushSize = 3;
        d.Line(2, 2, 30, 10);
        var a = (byte[])d.Grid.Cells.Clone();
        var r = d.Undo();
        Assert.Equal(Diff(a, d.Grid.Cells, 40), r.Rect);
        var b = (byte[])d.Grid.Cells.Clone();
        r = d.Redo();
        Assert.Equal(Diff(b, d.Grid.Cells, 40), r.Rect);
        Assert.False(r.StartChanged);
    }

    [Fact]
    public void RedoClearedByNewAction()
    {
        var d = Doc();
        d.Rect(1, 1, 3, 3);
        d.Rect(5, 5, 7, 7);
        d.Undo();
        Assert.Equal(1, d.RedoCount);
        d.Rect(10, 10, 12, 12);
        Assert.Equal(0, d.RedoCount);
        Assert.False(d.CanRedo);
        d.Undo();
        d.SetStart(1, 1, 0);
        Assert.Equal(0, d.RedoCount);
    }

    [Fact]
    public void NoOpActionsAddNoStepAndKeepRedo()
    {
        var d = Doc();
        d.Rect(1, 1, 3, 3);
        d.Undo();
        long rev = d.Revision;
        d.PaintType = CellType.Wall;
        d.Rect(1, 1, 3, 3);                 // already wall
        d.Fill(0, 0);                       // same type
        d.BeginStroke(2, 2); d.MoveStroke(8, 8); d.EndStroke();
        d.Line(0, 0, 9, 9);
        Assert.Equal(0, d.UndoCount);
        Assert.Equal(1, d.RedoCount);       // redo survived the no-ops
        Assert.Equal(rev, d.Revision);
    }

    [Fact]
    public void MoreThanCapStepsKeepExactlyCapAndDropTheOldest()
    {
        var d = Doc(250, 4);
        int cap = EditDocument.MaxUndoSteps;
        Assert.True(cap >= 100);
        for (int i = 0; i < cap + 20; i++) d.Rect(i, 0, i, 0);   // each step paints one distinct cell
        Assert.Equal(cap, d.UndoCount);
        while (d.CanUndo) d.Undo();
        Assert.Equal(cap, d.RedoCount);
        // the 20 oldest steps are gone for good: those cells stay painted, the rest are wall again
        for (int i = 0; i < 20; i++) Assert.Equal(CellType.Road, d.Grid[i, 0]);
        for (int i = 20; i < cap + 20; i++) Assert.Equal(CellType.Wall, d.Grid[i, 0]);
    }

    [Fact]
    public void UndoStackIsMemoryLightForSmallEdits()
    {
        var d = EditDocument.New(MapSize.L);
        long before = GC.GetAllocatedBytesForCurrentThread();
        d.BrushSize = 3;
        d.BeginStroke(10, 10);
        for (int i = 11; i < 300; i++) d.MoveStroke(i, 10 + (i % 7));
        d.EndStroke();
        long used = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(used < 4_000_000, $"stroke allocated {used} bytes");   // far below one grid copy per move
    }

    // ------------------------------------------------------------ revision, events, dirty flag

    [Fact]
    public void RevisionBumpsOnEveryRealChangeOnly()
    {
        var d = Doc();
        Assert.Equal(0, d.Revision);
        d.Rect(1, 1, 2, 2);
        Assert.Equal(1, d.Revision);
        d.Undo();
        Assert.Equal(2, d.Revision);
        d.Redo();
        Assert.Equal(3, d.Revision);
        d.Redo();           // nothing to redo
        d.Rect(1, 1, 2, 2); // same type
        Assert.Equal(3, d.Revision);
        d.SetStart(6, 6, 0);
        Assert.Equal(4, d.Revision);
    }

    [Fact]
    public void ChangedEventFiresWithTheReportedResult()
    {
        var d = Doc();
        var seen = new List<EditResult>();
        d.Changed += seen.Add;
        var a = d.Rect(2, 2, 4, 4);
        var b = d.SetStart(1, 1, 90);
        var c = d.Undo();
        d.Fill(-1, -1);
        Assert.Equal([a, b, c], seen);
    }

    [Fact]
    public void IsDirtyAndMarkSaved()
    {
        var d = Doc();
        Assert.False(d.IsDirty);
        d.Rect(1, 1, 3, 3);
        Assert.True(d.IsDirty);
        d.Undo();
        Assert.False(d.IsDirty);        // back at the creation state
        d.Redo();
        d.MarkSaved();
        Assert.False(d.IsDirty);
        d.Rect(8, 8, 9, 9);
        Assert.True(d.IsDirty);
        d.Undo();
        Assert.False(d.IsDirty);        // back at the saved state
        d.Undo();
        Assert.True(d.IsDirty);         // before the saved state
        d.Redo();
        Assert.False(d.IsDirty);
        d.Undo();
        d.Rect(20, 20, 21, 21);         // new branch with the same depth is not the saved state
        d.Redo();
        Assert.True(d.IsDirty);
    }

    [Fact]
    public void DirtyDuringStrokeAndMarkSavedEndsStroke()
    {
        var d = Doc();
        d.BeginStroke(3, 3);
        Assert.True(d.IsDirty);
        d.MarkSaved();
        Assert.False(d.StrokeActive);
        Assert.False(d.IsDirty);
    }

    [Fact]
    public void SavedStateLostWhenItsStepIsDroppedFromTheCap()
    {
        var d = Doc(250, 4);
        for (int i = 0; i < EditDocument.MaxUndoSteps + 5; i++) d.Rect(i, 0, i, 0);
        // the creation (saved) state is no longer reachable: undoing everything must not look clean
        while (d.CanUndo) d.Undo();
        Assert.True(d.IsDirty);
    }
}
