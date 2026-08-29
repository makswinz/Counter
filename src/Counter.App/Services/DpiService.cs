using System.Windows;

namespace Counter.App.Services;

/// <summary>
/// Keeps the interface's hairlines exactly one physical pixel wide, whatever the display is
/// scaled to.
///
/// A WPF logical pixel is a physical pixel only at 100 percent. At 150 percent a one-unit border
/// is one and a half device pixels, which the rasteriser resolves as a two-pixel line at
/// three-quarter strength: soft, uneven, and visibly thicker on some edges of a rounded rectangle
/// than others. Since the contour around the tool is the single most structural line in the
/// design - the thing that separates it from the desktop - that is not a detail worth leaving to
/// chance.
///
/// So the thickness is a resource rather than a constant: one over the scale factor, recomputed
/// whenever the window moves to a display with a different scale. Every hairline in the
/// application - the outer contour, the inner edge, dividers, the progress track, the checkbox
/// stroke, popover edges - resolves it dynamically, so they all change together and none of them
/// can be left behind.
/// </summary>
public static class DpiService
{
    /// <summary>The border thickness for a one-physical-pixel line.</summary>
    public const string ThicknessKey = "HairlineThickness";

    /// <summary>The same value as a number, for a stroke or a shape that wants a double.</summary>
    public const string ScalarKey = "HairlinePixel";

    /// <summary>
    /// The contour when the tool is active. Slightly heavier so an accent edge holds against a
    /// bright wallpaper, and capped at one and a half physical pixels so it can never start
    /// reading as a neon tube.
    /// </summary>
    public const string AccentThicknessKey = "ContourAccentThickness";

    /// <summary>What the resources currently describe. One at 100 percent scaling.</summary>
    public static double Scale { get; private set; } = 1.0;

    /// <summary>
    /// Recomputes the hairline for a scale factor and pushes it into the dictionary.
    ///
    /// Returns true when something actually changed, so a caller can skip the work of relaying
    /// out for a DPI notification that did not move the scale - Windows sends those when a window
    /// crosses between two displays that happen to match.
    /// </summary>
    public static bool Apply(ResourceDictionary resources, double scale)
    {
        if (scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale))
        {
            scale = 1.0;
        }

        if (Math.Abs(scale - Scale) < 0.001 && resources[ScalarKey] is double)
        {
            return false;
        }

        Scale = scale;

        var hairline = 1.0 / scale;

        // The cap in the design is one and a half physical pixels. This sits just under it, so
        // rounding at an awkward scale factor cannot push it over.
        var accent = 1.4 / scale;

        resources[ThicknessKey] = new Thickness(hairline);
        resources[ScalarKey] = hairline;
        resources[AccentThicknessKey] = new Thickness(accent);

        Diag.Write("dpi", "hairline", ("scale", scale), ("thickness", hairline));

        return true;
    }
}
