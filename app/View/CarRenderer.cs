using System;
using Godot;
using Nitrogenesis.Sim.Core;
using Nitrogenesis.Sim.Racing;

namespace Nitrogenesis.View;

/// <summary>
/// All cars as one MultiMesh (PLAN §2.1): a pre-allocated float buffer filled each frame and pushed with one
/// <c>RenderingServer.MultimeshSetBuffer</c> call; cars that are out of the run are left out through the visible
/// instance count. The best car is drawn last, on top of a gold silhouette one size bigger (its outline); a
/// selected car (clicked, or the player's) gets a wider outline in <see cref="SelectedColor"/>.
/// </summary>
/// <remarks>
/// Per instance 16 floats: Transform2D (8), colour (4), custom (4; x = 1 for an outline, drawn as a silhouette in
/// the instance colour). The pixel-art car texture marks tinted pixels with alpha ½; the shader multiplies those by
/// the instance colour, which the shader takes in <c>vertex()</c> (in <c>fragment()</c> COLOR already carries the
/// texel, which would darken the outline silhouette to the sprite's dark rim and tyres). Car colours: random HSV with S 0.6–0.9, V 0.8–1 (PLAN §6.2), fixed per car index for the
/// view's lifetime, or one fixed colour.
/// </remarks>
public partial class CarRenderer : MultiMeshInstance2D
{
    private const int Floats = 16;
    /// <summary>Gold outline size relative to the car: about 0.35 cell on every side.</summary>
    private const float HaloScaleX = (CarSize.Length + 0.7f) / CarSize.Length, HaloScaleY = (CarSize.Width + 0.7f) / CarSize.Width;
    /// <summary>The selected car's outline: about 0.7 cell on every side (outside the gold one when it is also the best).</summary>
    private const float SelectScaleX = (CarSize.Length + 1.4f) / CarSize.Length, SelectScaleY = (CarSize.Width + 1.4f) / CarSize.Width;
    public static readonly Color Gold = new(1f, 0.8f, 0.2f);
    /// <summary>Outline of the selected / followed car.</summary>
    public static readonly Color Selected = new(0.45f, 0.9f, 1f);

    /// <summary>Colour of the selected car's outline (training: light blue; driving: the player's accent).</summary>
    public Color SelectedColor { get; set; } = Selected;

    private const string ShaderCode = @"
shader_type canvas_item;
varying float outline;
varying vec4 inst;
void vertex() {
    outline = INSTANCE_CUSTOM.x;
    inst = COLOR;
}
void fragment() {
    vec4 t = texture(TEXTURE, UV);
    if (t.a < 0.25) discard;
    COLOR = outline > 0.5 ? vec4(inst.rgb, 1.0) : vec4(t.a < 0.75 ? t.rgb * inst.rgb : t.rgb, 1.0);
}";

    private float[] _buffer = Array.Empty<float>();
    private float[] _colors = Array.Empty<float>();
    private int _count;
    private Rid _rid;

    /// <summary>Instances drawn in the last <see cref="Render"/> (cars plus the outline).</summary>
    public int Drawn { get; private set; }

    /// <summary>Sets up for <paramref name="count"/> cars on a map of <paramref name="worldSize"/> cells. Allocates everything the per-frame path needs.</summary>
    /// <param name="count">Cars.</param>
    /// <param name="colorSeed">Seed of the random car colours.</param>
    /// <param name="worldSize">Map size, cells.</param>
    /// <param name="fixedColor">One colour for every car instead of random ones.</param>
    public void Setup(int count, ulong colorSeed, Vector2 worldSize, Color? fixedColor = null)
    {
        _count = count;
        _buffer = new float[(count + 2) * Floats]; // the cars plus two outlines
        _colors = new float[count * 3];
        var rng = new Random((int)(colorSeed ^ (colorSeed >> 32)));
        for (int i = 0; i < count; i++)
        {
            Color c = fixedColor ?? Color.FromHsv((float)rng.NextDouble(), 0.6f + 0.3f * (float)rng.NextDouble(), 0.8f + 0.2f * (float)rng.NextDouble());
            _colors[i * 3] = c.R;
            _colors[i * 3 + 1] = c.G;
            _colors[i * 3 + 2] = c.B;
        }

        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform2D,
            UseColors = true,
            UseCustomData = true,
            Mesh = CarMesh(),
            // The buffer is pushed straight to the RenderingServer, so the automatic bounds would stay those of the
            // first (empty) buffer and the cars would be culled: cover the whole map (plus a margin) instead.
            CustomAabb = new Aabb(new Vector3(-8, -8, -1), new Vector3(worldSize.X + 16, worldSize.Y + 16, 2)),
        };
        mm.InstanceCount = count + 2;
        mm.VisibleInstanceCount = 0;
        Multimesh = mm;
        Texture = CarTexture();
        TextureFilter = TextureFilterEnum.Nearest;
        Material = new ShaderMaterial { Shader = new Shader { Code = ShaderCode } };
        _rid = mm.GetRid();
    }

    /// <summary>
    /// Draws the cars at the given poses. Cars whose status is not Running or Finished are hidden. The
    /// <paramref name="selected"/> car (−1 = none) is drawn on top with its outline, the <paramref name="leader"/>
    /// (−1 = none) above everything with the gold one. Does not allocate.
    /// </summary>
    public void Render(float[] x, float[] y, float[] angle, AgentStatus[] status, int leader, int selected = -1)
    {
        if (leader >= _count || leader >= 0 && !IsShown(status[leader])) leader = -1;
        if (selected >= _count || selected >= 0 && !IsShown(status[selected])) selected = -1;
        int n = 0;
        for (int i = 0; i < _count; i++)
        {
            if (i == leader || i == selected || !IsShown(status[i])) continue;
            WriteCar(n++, i, x, y, angle);
        }
        if (selected >= 0)
        {
            Color c = SelectedColor;
            Write(n++, x[selected], y[selected], angle[selected], SelectScaleX, SelectScaleY, c.R, c.G, c.B, 1f);
            if (selected != leader) WriteCar(n++, selected, x, y, angle);
        }
        if (leader >= 0)
        {
            Write(n++, x[leader], y[leader], angle[leader], HaloScaleX, HaloScaleY, Gold.R, Gold.G, Gold.B, 1f);
            WriteCar(n++, leader, x, y, angle);
        }
        RenderingServer.MultimeshSetBuffer(_rid, _buffer);
        RenderingServer.MultimeshSetVisibleInstances(_rid, n);
        Drawn = n;
    }

    private void WriteCar(int slot, int i, float[] x, float[] y, float[] angle) =>
        Write(slot, x[i], y[i], angle[i], 1f, 1f, _colors[i * 3], _colors[i * 3 + 1], _colors[i * 3 + 2], 0f);

    /// <summary>The colour of car <paramref name="i"/> (for its route trail).</summary>
    public Color CarColor(int i) => new(_colors[i * 3], _colors[i * 3 + 1], _colors[i * 3 + 2]);

    private static bool IsShown(AgentStatus s) => s is AgentStatus.Running or AgentStatus.Finished;

    private void Write(int slot, float px, float py, float a, float sx, float sy, float r, float g, float b, float outline)
    {
        float cos = MathF.Cos(a), sin = MathF.Sin(a);
        int o = slot * Floats;
        float[] buf = _buffer;
        buf[o] = cos * sx;       // x.x
        buf[o + 1] = -sin * sy;  // y.x
        buf[o + 2] = 0f;
        buf[o + 3] = px;         // origin.x
        buf[o + 4] = sin * sx;   // x.y
        buf[o + 5] = cos * sy;   // y.y
        buf[o + 6] = 0f;
        buf[o + 7] = py;         // origin.y
        buf[o + 8] = r;
        buf[o + 9] = g;
        buf[o + 10] = b;
        buf[o + 11] = 1f;
        buf[o + 12] = outline;
        buf[o + 13] = 0f;
        buf[o + 14] = 0f;
        buf[o + 15] = 0f;
    }

    /// <summary>A quad the size of the hitbox (PLAN §3.3), centred, nose along +x, in cells.</summary>
    private static ArrayMesh CarMesh()
    {
        float hl = CarSize.HalfLength, hw = CarSize.HalfWidth;
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = new Vector2[] { new(-hl, -hw), new(hl, -hw), new(hl, hw), new(-hl, hw) };
        arrays[(int)Mesh.ArrayType.TexUV] = new Vector2[] { new(0, 0), new(1, 0), new(1, 1), new(0, 1) };
        arrays[(int)Mesh.ArrayType.Index] = new int[] { 0, 1, 2, 0, 2, 3 };
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    /// <summary>
    /// 18 × 11 px car, nose to the right (≈ 6 px per cell like the map). Legend: '#' outline, 'b' body (tinted),
    /// 'r' roof (tinted, darker), 'w' windscreen, 'q' rear window, 'h' headlight, 't' tail light, 'k' tyre.
    /// </summary>
    private static ImageTexture CarTexture()
    {
        string[] rows =
        [
            "...kkk.....kkk....",
            ".################.",
            "#tbbbbbbbbbbbbbbh#",
            "#bbbqrrrrrrwwbbbb#",
            "#bbbqrrrrrrwwbbbb#",
            "#bbbqrrrrrrwwbbbb#",
            "#bbbqrrrrrrwwbbbb#",
            "#bbbqrrrrrrwwbbbb#",
            "#tbbbbbbbbbbbbbbh#",
            ".################.",
            "...kkk.....kkk....",
        ];
        int w = rows[0].Length, h = rows.Length;
        var img = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                Color c = rows[y][x] switch
                {
                    '#' => new Color(0.06f, 0.06f, 0.08f, 1f),
                    'b' => new Color(1f, 1f, 1f, 0.5f),
                    'r' => new Color(0.78f, 0.78f, 0.78f, 0.5f),
                    'w' => new Color(0.55f, 0.72f, 0.85f, 1f),
                    'q' => new Color(0.35f, 0.45f, 0.55f, 1f),
                    'h' => new Color(1f, 0.95f, 0.7f, 1f),
                    't' => new Color(0.8f, 0.15f, 0.12f, 1f),
                    'k' => new Color(0.1f, 0.1f, 0.1f, 1f),
                    _ => new Color(0, 0, 0, 0),
                };
                img.SetPixel(x, y, c);
            }
        }
        return ImageTexture.CreateFromImage(img);
    }
}
