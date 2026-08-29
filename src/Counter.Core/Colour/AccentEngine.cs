namespace Counter.Core.Colour;

/// <summary>
/// One complete accent, generated from a single base colour. Every value is an opaque
/// eight-digit hex string, and every one of them shares the base colour's hue exactly.
/// </summary>
/// <param name="Highlight">The lit edge, where the light meets the material. 0 percent.</param>
/// <param name="Light">Still lit, but the colour is legible in it. 18 percent.</param>
/// <param name="Base">The colour the user actually chose. 48 percent.</param>
/// <param name="Strong">Turning away from the light. 76 percent.</param>
/// <param name="Deep">The shadowed edge, at the lower right. 100 percent.</param>
/// <param name="Shadow">Darker than the gradient goes. Contact shadows and pressed states.</param>
/// <param name="Glow">The halo colour. High chroma, and only ever used at low opacity.</param>
/// <param name="Foreground">Text laid over the ramp: whichever of white or near-black reads.</param>
/// <param name="Glyph">The same question for an icon, which needs less contrast than a word.</param>
public sealed record AccentRamp(
    string Highlight,
    string Light,
    string Base,
    string Strong,
    string Deep,
    string Shadow,
    string Glow,
    string Foreground,
    string Glyph);

/// <summary>
/// Turns one chosen colour into a complete illuminated palette.
///
/// The rule the whole engine exists to enforce is that a gradient must look like one material
/// under one light, not like a blend between two colours. So every stop keeps the base hue to
/// the radian, and only two things move: lightness, which is what light does, and chroma, which
/// is what happens at the extremes because a very light or very dark colour cannot hold as much
/// of it. Nothing anywhere in the application picks its own accent colour; everything reads a
/// stop off this ramp.
///
/// The shape is measured rather than invented. The reference palettes for the six families were
/// analysed in OKLCH, and two patterns came out of them: the lit stops converge on an absolute
/// lightness - roughly 0.91 and 0.86 - because a highlight is the light source rather than the
/// material, while the shadowed stops sit at a fixed distance below the base, because a shadow
/// is the material with less light on it. Fitting those constants against all nine reference
/// ramps leaves a mean error of about 0.03 in OKLab, which is under a just-noticeable
/// difference. The one place the reference deliberately disagrees is orange, whose designed
/// highlight is rotated about twenty-four degrees toward gold; rotating hue with lightness is a
/// painterly trick that does not generalise - the same rotation applied to blue produces
/// purple - so the hue is held and the small difference is accepted.
///
/// Nothing here is orange-specific, or blue-specific. There is one code path.
/// </summary>
public static class AccentEngine
{
    /// <summary>
    /// The band a base colour has to sit in for a five-stop ramp to fit around it. A colour
    /// paler or darker than this is brought to the nearest edge rather than refused: the result
    /// is still recognisably the colour that was asked for, and it can still carry a gradient.
    /// </summary>
    public const double MinimumBaseLightness = 0.32;

    public const double MaximumBaseLightness = 0.88;

    /// <summary>Dark ink, for the accents too pale to carry white.</summary>
    public const string DarkForeground = "#FF111318";

    public const string LightForeground = "#FFFFFFFF";

    /// <summary>
    /// Generates the complete ramp for one base colour.
    ///
    /// The lightness sequence is guaranteed strictly descending: each shadowed stop is placed at
    /// its designed distance below the base but never closer than fourteen thousandths to the
    /// stop above it, so even a nearly black base still produces a gradient rather than five
    /// copies of one colour. That guarantee is what lets the interface use the ramp without
    /// checking it.
    /// </summary>
    public static AccentRamp Derive(string baseColour)
    {
        var source = Perceptual.FromHex(baseColour);
        var hue = source.H;
        var chroma = source.C;
        var lightness = Math.Clamp(source.L, MinimumBaseLightness, MaximumBaseLightness);

        // Lit side: an absolute target, because a highlight is the colour of the light rather
        // than of the material, with a floor that keeps it above the base for a pale accent.
        var highlightL = Math.Max(lightness + 0.060, Math.Min(0.912, lightness + 0.300));
        var lightL = Math.Max(lightness + 0.035, Math.Min(0.862, lightness + 0.190));

        // Shadowed side: a fixed distance below the base, floored so a dark accent does not run
        // out of room, then held apart so the sequence can never collapse.
        var strongL = Math.Min(Math.Max(0.26, lightness - 0.070), lightness - 0.014);
        var deepL = Math.Min(Math.Max(0.22, lightness - 0.165), strongL - 0.014);
        var shadowL = Math.Min(Math.Max(0.14, lightness - 0.315), deepL - 0.014);

        string Stop(double l, double c) => Perceptual.ToHex(new Oklch(l, c, hue));

        // Chroma falls away at the lit end - sRGB simply cannot hold much of it up there, and a
        // highlight that stays saturated reads as a second colour rather than as light - and
        // swells slightly just past the base, which is where a real material looks richest.
        var ramp = new
        {
            Highlight = Stop(highlightL, Math.Min(chroma * 0.45, 0.070)),
            Light = Stop(lightL, Math.Min(chroma * 0.80, 0.095)),
            Base = Stop(lightness, chroma),
            Strong = Stop(strongL, chroma * 1.06),
            Deep = Stop(deepL, chroma),
            Shadow = Stop(shadowL, chroma * 0.82),
            Glow = Stop(Math.Min(0.90, lightness + 0.060), chroma * 1.12)
        };

        return new AccentRamp(
            ramp.Highlight, ramp.Light, ramp.Base, ramp.Strong, ramp.Deep, ramp.Shadow, ramp.Glow,
            Foreground(ramp.Base, ramp.Light),
            Glyph(ramp.Base, ramp.Strong));
    }

    /// <summary>
    /// Picks the ink for text and glyphs drawn on the ramp.
    ///
    /// A centred glyph on a small control sits over the base, but a longer label crosses into
    /// the lit half, so both are measured and the worse of the two decides. Whichever of white
    /// and near-black survives that comparison better is the one used. This is what stops a pale
    /// yellow or a light cyan accent from being handed white text nobody can read.
    /// </summary>
    public static string Foreground(string baseColour, string lightColour)
    {
        var white = Math.Min(
            Perceptual.Contrast(LightForeground, baseColour),
            Perceptual.Contrast(LightForeground, lightColour));

        var ink = Math.Min(
            Perceptual.Contrast(DarkForeground, baseColour),
            Perceptual.Contrast(DarkForeground, lightColour));

        return white >= ink ? LightForeground : DarkForeground;
    }

    /// <summary>
    /// Picks the ink for a filled glyph drawn on the ramp - a play triangle, a checkmark - which
    /// is a different question from the one <see cref="Foreground"/> answers.
    ///
    /// A word and a shape do not need the same contrast. WCAG asks four and a half to one of
    /// text, because reading is a fine-detail task, and three to one of a graphical object, which
    /// a solid filled triangle at seventeen pixels comfortably is. So this takes white whenever
    /// white genuinely clears that bar across the region a centred glyph actually covers - the
    /// base through the strong stop - and only falls to dark ink when it does not.
    ///
    /// The result is the conventional white play button on blue, purple and pink, and dark ink on
    /// cyan, green, orange and any pale colour somebody picks, where white would be a smear. It is
    /// still measured rather than chosen, it still has no idea which colour it was handed, and it
    /// still never forces white onto a gradient that cannot carry it.
    /// </summary>
    public static string Glyph(string baseColour, string strongColour)
    {
        const double GraphicalMinimum = 3.0;

        var white = Math.Min(
            Perceptual.Contrast(LightForeground, baseColour),
            Perceptual.Contrast(LightForeground, strongColour));

        if (white >= GraphicalMinimum)
        {
            return LightForeground;
        }

        var ink = Math.Min(
            Perceptual.Contrast(DarkForeground, baseColour),
            Perceptual.Contrast(DarkForeground, strongColour));

        return white >= ink ? LightForeground : DarkForeground;
    }
}
