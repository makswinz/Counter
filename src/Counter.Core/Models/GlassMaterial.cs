namespace Counter.Core.Models;

/// <summary>
/// Which glass the panels are made of.
///
/// The three are not brightness settings. They are three different materials, and they differ in
/// the one thing a real backdrop filter would otherwise decide for them: how much of the desktop
/// is allowed through. A layered window cannot blur what is behind it, so whatever passes through
/// arrives at full sharpness - which means the choice between "this reads as glass" and "I can
/// read my browser through my timer" is a real one, and it belongs to whoever is looking at it
/// rather than to a constant in a palette.
/// </summary>
public enum GlassMaterial
{
    /// <summary>
    /// Dense smoked glass. What is behind the panel is a tone, never content. The default,
    /// because it is the only one that is legible over anything at all.
    /// </summary>
    Solid = 0,

    /// <summary>
    /// Frosted glass: a pale wash, a soft white rim rather than a drawn edge, a reflection along
    /// the far corner and a sheen off the top. Reads best over a photograph or a plain desktop.
    /// </summary>
    Frosted = 1,

    /// <summary>
    /// Liquid glass: almost no tint at all, a single soft inner light, an outward halo and a slow
    /// ripple through the surface. The most transparent of the three, and the most conditional on
    /// what happens to be behind it.
    /// </summary>
    Liquid = 2
}

/// <summary>Reading and writing the stored material, which is the enum's name and nothing else.</summary>
public static class GlassMaterials
{
    /// <summary>In the order the settings panel offers them.</summary>
    public static readonly IReadOnlyList<GlassMaterial> All = new[]
    {
        GlassMaterial.Solid, GlassMaterial.Frosted, GlassMaterial.Liquid
    };

    /// <summary>What a first run, or an unreadable setting, resolves to.</summary>
    public const GlassMaterial Default = GlassMaterial.Solid;

    /// <summary>
    /// Resolves a stored value. Anything unrecognised falls back rather than throwing: a bad
    /// preference is never a reason to refuse to draw the interface.
    /// </summary>
    public static GlassMaterial Parse(string? value) =>
        Enum.TryParse<GlassMaterial>((value ?? string.Empty).Trim(), ignoreCase: true, out var parsed)
        && All.Contains(parsed)
            ? parsed
            : Default;

    /// <summary>What the settings panel calls it.</summary>
    public static string DisplayName(GlassMaterial material) => material switch
    {
        GlassMaterial.Frosted => "Frosted",
        GlassMaterial.Liquid => "Liquid",
        _ => "Solid"
    };
}
