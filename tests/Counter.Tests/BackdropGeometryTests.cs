using System.Windows;
using Counter.App.Services;
using Counter.App.Theme;
using Counter.Core.Colour;
using Counter.Core.Models;
using Xunit;

namespace Counter.Tests;

/// <summary>
/// Where the blurred window goes, and whether anybody can read what is written on top of it.
///
/// Two separate promises, both of which were broken and both of which were only found by
/// photographing the running application rather than by reasoning about it.
/// </summary>
public class BackdropGeometryTests
{
    /// <summary>
    /// The radius DWM rounds a window at, in physical pixels. Not configurable, not published,
    /// and the number the inset in <see cref="AcrylicBackdrop.Fit"/> is solved against.
    /// </summary>
    private const int DwmRadius = 8;

    private static (int, int, int, int) Notch => (2, 2, 13, 13);
    private static (int, int, int, int) Card => (14, 14, 14, 14);
    private static (int, int, int, int) Popover => (12, 12, 12, 12);

    /// <summary>
    /// Whether the backdrop's own rounded outline lies entirely inside the panel's.
    ///
    /// Two rounded rectangles nest when every straight edge is inside the corresponding edge and
    /// every pair of corner arcs nests, and two circles nest when the distance between their
    /// centres is at most the difference of their radii. That is the whole test: the reason the
    /// corners showed was that the two curves crossed, and nesting is the condition that says
    /// they do not.
    /// </summary>
    private static bool Nests(Int32Rect fitted, Int32Rect panel, (int TL, int TR, int BR, int BL) radius)
    {
        if (fitted.X < panel.X || fitted.Y < panel.Y
            || fitted.X + fitted.Width > panel.X + panel.Width
            || fitted.Y + fitted.Height > panel.Y + panel.Height)
        {
            return false;
        }

        var corners = new[]
        {
            (px: panel.X + radius.TL, py: panel.Y + radius.TL, r: radius.TL,
                fx: fitted.X + DwmRadius, fy: fitted.Y + DwmRadius),
            (px: panel.X + panel.Width - radius.TR, py: panel.Y + radius.TR, r: radius.TR,
                fx: fitted.X + fitted.Width - DwmRadius, fy: fitted.Y + DwmRadius),
            (px: panel.X + panel.Width - radius.BR, py: panel.Y + panel.Height - radius.BR, r: radius.BR,
                fx: fitted.X + fitted.Width - DwmRadius, fy: fitted.Y + fitted.Height - DwmRadius),
            (px: panel.X + radius.BL, py: panel.Y + panel.Height - radius.BL, r: radius.BL,
                fx: fitted.X + DwmRadius, fy: fitted.Y + fitted.Height - DwmRadius)
        };

        foreach (var corner in corners)
        {
            if (corner.r < DwmRadius)
            {
                // The backdrop's curve is the wider one here, so it cuts into the panel rather
                // than escaping it. That is a bite out of the blur, not blur in clear air, and
                // the flush-with-the-screen case below is the one place it would be visible.
                continue;
            }

            var dx = (double)corner.fx - corner.px;
            var dy = (double)corner.fy - corner.py;

            if (Math.Sqrt(dx * dx + dy * dy) > corner.r - DwmRadius + 0.001)
            {
                return false;
            }
        }

        return true;
    }

    [Theory]
    [InlineData(330, 44)]
    [InlineData(540, 260)]
    [InlineData(300, 150)]
    [InlineData(632, 900)]
    public void A_rounded_backdrop_never_reaches_past_the_panel(int width, int height)
    {
        // Every surface the application has, on a display whose top edge is far away, so the
        // flush-with-the-screen exception is not what is being measured.
        foreach (var radius in new[] { Notch, Card, Popover })
        {
            var panel = new Int32Rect(700, 300, width, height);
            var fitted = AcrylicBackdrop.Fit(panel, radius, monitorTop: 0, rounded: true);

            Assert.True(Nests(fitted, panel, radius),
                radius + " at " + width + "x" + height + " leaves blur outside the panel.");
        }
    }

    [Fact]
    public void A_panel_against_the_top_of_the_screen_is_blurred_all_the_way_up()
    {
        // The notch meets the bezel square. Rounding it there would take two bites out of the
        // corners the panel does not have, so the backdrop is pushed above the screen instead
        // and DWM rounds it where there is nothing to see.
        var panel = new Int32Rect(795, 0, 330, 44);
        var fitted = AcrylicBackdrop.Fit(panel, Notch, monitorTop: 0, rounded: true);

        Assert.True(fitted.Y <= -DwmRadius, "the backdrop stops at the screen edge and gets rounded there.");
        Assert.True(fitted.Y + fitted.Height < panel.Y + panel.Height, "the bottom edge is not inset.");
    }

    [Fact]
    public void A_panel_on_a_second_display_is_measured_against_that_display()
    {
        // A monitor above the primary one has a negative top. A backdrop that decided it was
        // flush by comparing with zero would be extended on a floating panel and not on the
        // notch, which is exactly backwards.
        var panel = new Int32Rect(200, -1080, 330, 44);

        Assert.True(AcrylicBackdrop.Fit(panel, Notch, monitorTop: -1080, rounded: true).Y <= -1080 - DwmRadius);
        Assert.True(AcrylicBackdrop.Fit(panel, Notch, monitorTop: -2160, rounded: true).Y > -1080);
    }

    [Fact]
    public void Windows_10_gets_the_panel_rectangle_exactly()
    {
        // There the region does the clipping and it follows the outline precisely, so insetting
        // would only leave an unblurred ring for nothing.
        var panel = new Int32Rect(795, 0, 330, 44);

        Assert.Equal(panel, AcrylicBackdrop.Fit(panel, Notch, monitorTop: 0, rounded: false));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(4, 3)]
    [InlineData(2, 40)]
    public void A_surface_smaller_than_its_own_inset_does_not_turn_inside_out(int width, int height)
    {
        // Panels animate open from nothing. A width of two pixels must produce a small rectangle
        // or an empty one, never a negative one that a later cast turns into something enormous.
        var fitted = AcrylicBackdrop.Fit(new Int32Rect(100, 100, width, height), Card, 0, rounded: true);

        Assert.True(fitted.Width >= 0 && fitted.Height >= 0);
        Assert.True(fitted.Width <= width && fitted.Height <= height + DwmRadius);
    }

    // ==================================================================== contrast

    /// <summary>
    /// The worst surface each material actually produces, measured rather than computed.
    ///
    /// A blur destroys detail; it does not change luminance. A white wallpaper blurred is still
    /// white, so the colour a panel ends up being depends on what happens to be behind it, and
    /// no amount of arithmetic over the palette will say what that is - the acrylic blend is the
    /// compositor's, not ours. These are photographs: the real window over a field of forty-pixel
    /// bands of saturated colour including pure white and pure black, sampled across the content
    /// area at every row, worst value kept. The procedure is in docs/DESIGN.md.
    ///
    /// If the glass densities or the acrylic tint move, these move with them, and the way to find
    /// the new ones is to take the photograph again rather than to guess.
    /// </summary>
    public static TheoryData<bool, GlassMaterial, string> WorstSurfaces => new()
    {
        { false, GlassMaterial.Solid, "#FF46494F" },
        { false, GlassMaterial.Frosted, "#FF5C5D61" },
        { false, GlassMaterial.Liquid, "#FF616267" },
        { true, GlassMaterial.Solid, "#FFE6E7E7" },
        { true, GlassMaterial.Frosted, "#FFDEE2D9" },
        { true, GlassMaterial.Liquid, "#FFCED5C4" }
    };

    [Theory]
    [MemberData(nameof(WorstSurfaces))]
    public void Every_ink_clears_its_target_on_the_worst_surface_its_material_produces(
        bool isLight, GlassMaterial material, string surface)
    {
        // AA for anything that carries meaning, and the large-text threshold for the muted tone,
        // which labels rather than states. Nothing here is allowed to sit below that on any
        // wallpaper, in either theme, on any of the three materials.
        var palette = ThemePalette.Solids(isLight, AccentPalettes.Blue, material, blurred: material != GlassMaterial.Solid);

        Assert.True(Perceptual.Contrast(palette["TextPrimaryBrush"], surface) >= 4.5,
            "primary ink on " + material + (isLight ? " light" : " dark") + " is below 4.5:1.");

        Assert.True(Perceptual.Contrast(palette["TextSecondaryBrush"], surface) >= 4.5,
            "secondary ink on " + material + (isLight ? " light" : " dark") + " is below 4.5:1.");

        Assert.True(Perceptual.Contrast(palette["TextMutedBrush"], surface) >= 3.0,
            "muted ink on " + material + (isLight ? " light" : " dark") + " is below 3:1.");
    }

    [Theory]
    [MemberData(nameof(WorstSurfaces))]
    public void The_ink_ladder_still_reads_as_a_ladder(bool isLight, GlassMaterial material, string surface)
    {
        // Compressing the quiet end until it passes is easy and it is also how a hierarchy stops
        // being one. Each step has to stay a step: quieter than the one above it, on the surface
        // it is actually drawn on.
        var palette = ThemePalette.Solids(isLight, AccentPalettes.Blue, material, blurred: material != GlassMaterial.Solid);

        var primary = Perceptual.Contrast(palette["TextPrimaryBrush"], surface);
        var secondary = Perceptual.Contrast(palette["TextSecondaryBrush"], surface);
        var muted = Perceptual.Contrast(palette["TextMutedBrush"], surface);

        Assert.True(primary > secondary + 0.3, "secondary is not quieter than primary.");
        Assert.True(secondary > muted + 0.3, "muted is not quieter than secondary.");
    }
}
