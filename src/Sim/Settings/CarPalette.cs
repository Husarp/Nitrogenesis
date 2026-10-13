using Nitrogenesis.Sim.Rng;

namespace Nitrogenesis.Sim.Settings;

/// <summary>Car colours (PLAN §6.2): random HSV, per family (a child keeps its parent's colour with a small hue shift) or one colour.</summary>
public static class CarPalette
{
    public static readonly IReadOnlyList<string> ModeLabels = ["Random", "Per family", "Single colour"];
    public const int RandomMode = 0, FamilyMode = 1, SingleMode = 2;

    /// <summary>The "single colour" choices: name and RGB (0…1).</summary>
    public static readonly IReadOnlyList<(string Name, float R, float G, float B)> Singles =
    [
        ("Orange", 0.98f, 0.6f, 0.25f), ("Red", 0.9f, 0.25f, 0.22f), ("Yellow", 0.95f, 0.85f, 0.25f), ("Green", 0.35f, 0.8f, 0.4f),
        ("Cyan", 0.3f, 0.8f, 0.9f), ("Blue", 0.3f, 0.45f, 0.95f), ("Purple", 0.65f, 0.4f, 0.9f), ("White", 0.95f, 0.95f, 0.97f),
    ];

    /// <summary>The largest hue shift between parent and child (hue is 0…1).</summary>
    public const float MaxHueShift = 0.04f;

    /// <summary>A number in [0, 1) from the seed and an index (no state, no allocation).</summary>
    private static float Unit(ulong seed, ulong index) => (SeedHash.Derive(seed, index) >> 40) * (1f / 16777216f);

    /// <summary>Car <paramref name="car"/>'s random colour: any hue, saturation 0.6…0.9, value 0.8…1 (PLAN §6.2).</summary>
    public static void Random(ulong seed, int car, out float hue, out float saturation, out float value)
    {
        hue = Unit(seed, (ulong)car * 3);
        saturation = 0.6f + 0.3f * Unit(seed, (ulong)car * 3 + 1);
        value = 0.8f + 0.2f * Unit(seed, (ulong)car * 3 + 2);
    }

    /// <summary>The child's hue: the parent's plus a shift of at most <see cref="MaxHueShift"/>, drawn from <paramref name="seed"/> and the child index; wrapped into [0, 1).</summary>
    public static float ChildHue(float parentHue, ulong seed, int child)
    {
        float hue = parentHue + (Unit(seed, (ulong)child) - 0.5f) * 2f * MaxHueShift;
        hue -= MathF.Floor(hue);
        return hue >= 1f ? 0f : hue;
    }
}
