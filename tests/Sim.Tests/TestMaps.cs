using Nitrogenesis.Sim.Map;

/// <summary>
/// Builds small tracks from ASCII art for tests. Legend: '#' wall, '.' road, 'g' grass, 'x' danger,
/// 'F' finish, 'S' road with the start at its centre (angle 0).
/// </summary>
internal static class TestMaps
{
    public static Track Parse(params string[] rows)
    {
        int h = rows.Length, w = rows[0].Length;
        var grid = new Grid(w, h);
        var start = new TrackStart(-1, -1, 0);
        for (int y = 0; y < h; y++)
        {
            Assert.Equal(w, rows[y].Length);
            for (int x = 0; x < w; x++)
            {
                char c = rows[y][x];
                grid[x, y] = c switch
                {
                    '#' => CellType.Wall,
                    '.' or 'S' => CellType.Road,
                    'g' => CellType.Grass,
                    'x' => CellType.Danger,
                    'F' => CellType.Finish,
                    _ => throw new ArgumentException($"Unknown map char '{c}'."),
                };
                if (c == 'S') start = new TrackStart(x + 0.5f, y + 0.5f, 0);
            }
        }
        return new Track("test", grid, start);
    }
}
