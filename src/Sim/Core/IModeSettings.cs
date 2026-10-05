namespace Nitrogenesis.Sim.Core;

/// <summary>A mode's settings as far as <see cref="SimConfig"/> needs them: an id and a stable hash contribution.</summary>
public interface IModeSettings
{
    /// <summary>Stable mode name ("racing"); part of the hash so two modes never share a config hash.</summary>
    string ModeId { get; }

    /// <summary>
    /// Writes every value that affects a trajectory or a score (settings and the mode's fixed constants)
    /// in a fixed order. Changing what is written changes every stored config hash: only do it together with
    /// a <see cref="SimInfo.SimVersion"/> bump.
    /// </summary>
    void WriteHash(ConfigHashWriter writer);
}
