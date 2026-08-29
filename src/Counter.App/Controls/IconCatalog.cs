using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Media;

namespace Counter.App.Controls;

/// <summary>
/// The hand-maintained half of the icon table: variant fallback, optical corrections, and the
/// geometry lookup. Everything else about an icon - which resource, which viewBox, which
/// upstream file, and the path data itself - is generated into IconCatalog.g.cs by
/// tools\Sync-FluentIcons.ps1.
/// </summary>
public static partial class IconCatalog
{
    /// <summary>
    /// Per-icon optical nudges, in the icon's own rendered pixels, applied to every use.
    ///
    /// This table exists so that a correction is made once and everywhere rather than as a
    /// margin in whichever view somebody happened to be looking at. Nothing here may exceed one
    /// pixel: a larger number means the icon is wrong, not that it is off-centre.
    ///
    /// It has exactly one entry, and that was decided by measurement rather than by eye. Every
    /// bundled geometry's ink bounds were compared against its own viewBox centre, and all of
    /// them land within a quarter of a unit of it except the pin, which is a diagonal shape, and
    /// the play triangle.
    ///
    /// The triangle is the one that needs correcting, and not for the reason a bounding box
    /// suggests. Its ink runs from x=5 to x=18 on the 20 grid, so the box is already pushed
    /// right; but a triangle's optical centre is its centroid, at (5+5+18)/3 = 9.33, which is
    /// two thirds of a unit left of the middle. At the 14 px the play glyph renders at, that is
    /// half a pixel - which is what is corrected here, and why the number is not a guess.
    /// </summary>
    private static readonly Dictionary<(IconKind, IconVariant), Vector> Offsets = new()
    {
        [(IconKind.Play, IconVariant.Filled)] = new Vector(0.5, 0)
    };

    /// <summary>The largest optical correction the catalog will accept, in pixels.</summary>
    public const double MaximumOpticalOffset = 1.0;

    private static readonly ConcurrentDictionary<string, Geometry?> GeometryCache = new();

    /// <summary>
    /// Finds the glyph for a kind, falling back to the other variant when only one was bundled.
    ///
    /// The fallback is what lets a template ask for Filled on a selected state without every
    /// icon in the family needing both weights drawn. Returns null for <see cref="IconKind.None"/>
    /// and for anything genuinely absent, and an absent icon draws nothing rather than throwing:
    /// a missing glyph must never be able to take the window down.
    /// </summary>
    public static IconGlyph? Resolve(IconKind kind, IconVariant variant)
    {
        if (kind == IconKind.None)
        {
            return null;
        }

        if (Glyphs.TryGetValue((kind, variant), out var exact))
        {
            return exact;
        }

        var other = variant == IconVariant.Filled ? IconVariant.Regular : IconVariant.Filled;

        return Glyphs.TryGetValue((kind, other), out var fallback) ? fallback : null;
    }

    /// <summary>Every kind and variant that was actually bundled. Used by the tests.</summary>
    public static IEnumerable<(IconKind Kind, IconVariant Variant, IconGlyph Glyph)> All =>
        Glyphs.Select(pair => (pair.Key.Item1, pair.Key.Item2, pair.Value));

    /// <summary>The centralised optical correction for one icon, in rendered pixels.</summary>
    public static Vector OpticalOffset(IconKind kind, IconVariant variant) =>
        Offsets.TryGetValue((kind, variant), out var offset) ? offset : default;

    /// <summary>
    /// The frozen geometry behind a resource key.
    ///
    /// The path data is compiled into the assembly rather than loaded from a resource
    /// dictionary, so there is no pack URI to resolve, nothing that can fail to be found at
    /// runtime, and nothing a same-named key added elsewhere could shadow. Each geometry is
    /// parsed once, frozen and kept: after the first draw an icon costs a dictionary hit and one
    /// drawing instruction.
    /// </summary>
    public static Geometry? Geometry(string resourceKey) =>
        GeometryCache.GetOrAdd(resourceKey, static key =>
        {
            if (!Paths.TryGetValue(key, out var data))
            {
                return null;
            }

            var geometry = System.Windows.Media.Geometry.Parse(data);
            geometry.Freeze();
            return geometry;
        });
}
