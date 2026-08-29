using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Counter.App.Services;
using Counter.App.Theme;
using Counter.Core.Models;
using Xunit;

namespace Counter.Tests;

/// <summary>
/// The three glasses, and the one thing that separates them.
///
/// A material is not a brightness setting. It decides how much of the desktop reaches the eye,
/// and because this window cannot blur what is behind it, that is the difference between a
/// surface that reads as glass and a surface you can read a browser through. Every test here is
/// ultimately about that trade being made deliberately rather than drifting.
/// </summary>
public class GlassMaterialTests
{
    private static byte Alpha(string hex) =>
        byte.Parse(hex.Substring(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static readonly string[] GlassKeys =
    {
        "GlassBaseBrush", "GlassRaisedBrush", "GlassDeepBrush", "GlassHoverBrush"
    };

    // ==================================================================== the stored value

    [Fact]
    public void The_three_materials_are_the_ones_offered()
    {
        Assert.Equal(
            new[] { GlassMaterial.Solid, GlassMaterial.Frosted, GlassMaterial.Liquid },
            GlassMaterials.All);

        Assert.Equal(GlassMaterial.Solid, GlassMaterials.Default);
    }

    [Theory]
    [InlineData("Solid", GlassMaterial.Solid)]
    [InlineData("frosted", GlassMaterial.Frosted)]
    [InlineData("LIQUID", GlassMaterial.Liquid)]
    [InlineData("  Frosted  ", GlassMaterial.Frosted)]
    public void A_stored_material_round_trips(string stored, GlassMaterial expected) =>
        Assert.Equal(expected, GlassMaterials.Parse(stored));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("marble")]
    [InlineData("7")]
    public void An_unreadable_material_falls_back_rather_than_throwing(string? stored)
    {
        // A number would otherwise parse straight through Enum.TryParse into a member that does
        // not exist, which is the classic way an enum setting turns into an invisible window.
        Assert.Equal(GlassMaterial.Solid, GlassMaterials.Parse(stored));
    }

    // ==================================================================== the difference

    [Fact]
    public void Solid_glass_is_the_theme_untouched()
    {
        // The default material adds nothing, so a run that never opens settings is running the
        // palette exactly as it is written.
        Assert.Empty(ThemePalette.GlassOverrides(false, GlassMaterial.Solid));
        Assert.Empty(ThemePalette.GlassOverrides(true, GlassMaterial.Solid));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void Each_material_lets_more_through_than_the_one_before(bool isLight, bool blurred)
    {
        // The ordering is the whole promise of the control. If frosted were ever denser than
        // solid, the choice would be three names for the same thing. It has to hold in both
        // tables, because there are two: one for glass with a blur behind it and one without.
        foreach (var key in GlassKeys)
        {
            var solid = Alpha(ThemePalette.Solids(isLight, AccentPalettes.Blue, GlassMaterial.Solid, blurred)[key]);
            var frosted = Alpha(ThemePalette.Solids(isLight, AccentPalettes.Blue, GlassMaterial.Frosted, blurred)[key]);
            var liquid = Alpha(ThemePalette.Solids(isLight, AccentPalettes.Blue, GlassMaterial.Liquid, blurred)[key]);

            Assert.True(solid > frosted, key + " is not denser in solid than in frosted.");
            Assert.True(frosted > liquid, key + " is not denser in frosted than in liquid.");
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Without_a_blur_no_material_is_transparent_enough_to_read_through(bool isLight)
    {
        // The floor, and it only applies when there is nothing blurring behind the panel. Below
        // roughly two fifths, a line of text behind the glass stops being a tone and starts
        // being a sentence, and the interface is no longer the thing you are looking at.
        foreach (var material in GlassMaterials.All)
        {
            var map = ThemePalette.Solids(isLight, AccentPalettes.Blue, material);

            foreach (var key in GlassKeys)
            {
                Assert.True(Alpha(map[key]) >= 0x66,
                    material + " " + key + " is too transparent to carry text.");
            }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_blur_behind_buys_a_much_thinner_sheet(bool isLight)
    {
        // The entire reason the two tables exist. A blur has already destroyed the detail behind
        // the panel, so the tint no longer has to hide anything and the glass can be what the
        // reference designs actually are. Every surface gets thinner, and none of them by a
        // little: if the difference were small the second table would not be worth having.
        foreach (var material in new[] { GlassMaterial.Frosted, GlassMaterial.Liquid })
        {
            var bare = ThemePalette.Solids(isLight, AccentPalettes.Blue, material);
            var blurred = ThemePalette.Solids(isLight, AccentPalettes.Blue, material, blurred: true);

            foreach (var key in GlassKeys)
            {
                Assert.True(Alpha(blurred[key]) < Alpha(bare[key]) - 0x20,
                    material + " " + key + " is barely thinner with a blur behind it.");
            }
        }
    }

    [Theory]
    [InlineData(GlassMaterial.Frosted)]
    [InlineData(GlassMaterial.Liquid)]
    public void A_translucent_material_is_given_stronger_ink(GlassMaterial material)
    {
        // Quiet ink over a translucent panel is not quiet, it is gone: the contrast a muted tone
        // was measured against was a solid surface, and there is no solid surface any more.
        foreach (var isLight in new[] { true, false })
        {
            var solid = ThemePalette.Solids(isLight, AccentPalettes.Blue, GlassMaterial.Solid);
            var thin = ThemePalette.Solids(isLight, AccentPalettes.Blue, material);
            var ground = solid["GlassBaseBrush"];


            foreach (var key in new[] { "TextSecondaryBrush", "TextMutedBrush" })
            {
                Assert.True(
                    Contrast(thin[key], ground) > Contrast(solid[key], ground),
                    material + " " + key + " is no stronger than the solid one.");
            }
        }
    }

    [Fact]
    public void Both_themes_describe_every_material_the_same_way()
    {
        // The same failure the two theme sets are guarded against: a key present in one and
        // missing in the other is a control that goes wrong in exactly one combination, which
        // is the combination nobody looks at.
        foreach (var material in GlassMaterials.All)
        {
            Assert.Equal(
                ThemePalette.GlassOverrides(true, material).Keys.OrderBy(k => k),
                ThemePalette.GlassOverrides(false, material).Keys.OrderBy(k => k));
        }
    }

    [Fact]
    public void A_material_only_ever_restates_a_key_the_theme_already_has()
    {
        // An override with a typo in its key would silently add a resource nothing reads, and
        // silently leave the one it meant to change alone.
        foreach (var material in GlassMaterials.All)
        {
            foreach (var isLight in new[] { true, false })
            {
                var basis = isLight ? ThemePalette.Light : ThemePalette.Dark;

                foreach (var key in ThemePalette.GlassOverrides(isLight, material).Keys)
                {
                    Assert.True(basis.ContainsKey(key),
                        material + " overrides " + key + ", which the theme does not declare.");
                }
            }
        }
    }

    [Fact]
    public void The_material_never_touches_the_accent()
    {
        // Three independent inputs. Choosing a glass must not move a single accent-driven
        // colour, or the two controls become one control with a confusing name.
        var solid = ThemePalette.Solids(false, AccentPalettes.Pink, GlassMaterial.Solid);

        foreach (var material in GlassMaterials.All)
        {
            var map = ThemePalette.Solids(false, AccentPalettes.Pink, material);

            foreach (var key in map.Keys.Where(k => k.StartsWith("Accent")))
            {
                Assert.Equal(solid[key], map[key]);
            }
        }
    }

    // ==================================================================== the light layers

    [Fact]
    public void The_two_reflections_are_white_light_and_nothing_else()
    {
        // Light is not paint. A tinted reflection would put a colour into every surface in the
        // interface that no palette chose and no accent could change.
        foreach (var brush in new[] { ThemeService.BuildEdgeReflection(), ThemeService.BuildTopSheen() })
        {
            foreach (var stop in brush.GradientStops)
            {
                Assert.Equal(0xFF, stop.Color.R);
                Assert.Equal(0xFF, stop.Color.G);
                Assert.Equal(0xFF, stop.Color.B);
            }

            Assert.True(brush.IsFrozen);
        }
    }

    [Fact]
    public void The_edge_reflection_runs_from_the_corner_opposite_the_light()
    {
        // The specular sits at the upper left, so the far edge is the lower right. The two
        // together are what make a flat rectangle read as something with a thickness.
        var brush = ThemeService.BuildEdgeReflection();

        Assert.Equal(new System.Windows.Point(1, 1), brush.StartPoint);
        Assert.Equal(new System.Windows.Point(0, 0), brush.EndPoint);

        // And it is gone by the middle, rather than fading across the whole surface: an edge
        // catching light, not a second gradient laid over the panel.
        Assert.Equal(0.5, brush.GradientStops[^1].Offset);
        Assert.Equal(0, brush.GradientStops[^1].Color.A);
        Assert.True(brush.GradientStops[0].Color.A > 0);
    }

    [Fact]
    public void The_top_sheen_falls_straight_down()
    {
        var brush = ThemeService.BuildTopSheen();

        Assert.Equal(new System.Windows.Point(0, 0), brush.StartPoint);
        Assert.Equal(new System.Windows.Point(0, 1), brush.EndPoint);
        Assert.True(brush.GradientStops[0].Color.A > brush.GradientStops[^1].Color.A);
    }

    [Fact]
    public void Neither_reflection_is_strong_enough_to_read_as_a_surface()
    {
        // These are highlights. Past about a sixth they stop describing light falling on glass
        // and start being a white panel with a gradient on it.
        foreach (var brush in new[] { ThemeService.BuildEdgeReflection(), ThemeService.BuildTopSheen() })
        {
            Assert.All(brush.GradientStops, stop => Assert.True(stop.Color.A <= 0x40));
        }
    }

    // ==================================================================== the ripple

    [Fact]
    public void The_ripple_is_one_cached_texture()
    {
        Assert.Same(GlassNoise.Ripple(), GlassNoise.Ripple());
        Assert.True(GlassNoise.Ripple().IsFrozen);
    }

    [Fact]
    public void The_ripple_only_ever_adds_white_light()
    {
        // It stands in for a backdrop distortion this window cannot have, so it must not also
        // darken or tint: the material is uneven, not dirty.
        var source = (BitmapSource)GlassNoise.Ripple().ImageSource;
        var pixels = new byte[source.PixelWidth * source.PixelHeight * 4];
        source.CopyPixels(pixels, source.PixelWidth * 4, 0);

        for (var index = 0; index < pixels.Length; index += 4)
        {
            Assert.Equal(0xFF, pixels[index]);
            Assert.Equal(0xFF, pixels[index + 1]);
            Assert.Equal(0xFF, pixels[index + 2]);
            Assert.True(pixels[index + 3] <= GlassNoise.RippleStrength);
        }
    }

    [Fact]
    public void The_ripple_has_no_seam()
    {
        // A tiled texture whose edges do not meet draws a grid across every panel in the app.
        // The field is periodic by construction; this is the assertion that says so, by
        // comparing the step across the wrap with the steps everywhere else in the row.
        var source = (BitmapSource)GlassNoise.Ripple().ImageSource;
        var width = source.PixelWidth;
        var pixels = new byte[width * source.PixelHeight * 4];
        source.CopyPixels(pixels, width * 4, 0);

        byte AlphaAt(int x, int y) => pixels[(((y * width) + x) * 4) + 3];

        var interior = 0;

        for (var y = 0; y < source.PixelHeight; y++)
        {
            for (var x = 0; x + 1 < width; x++)
            {
                interior = Math.Max(interior, Math.Abs(AlphaAt(x, y) - AlphaAt(x + 1, y)));
            }
        }

        for (var y = 0; y < source.PixelHeight; y++)
        {
            Assert.True(Math.Abs(AlphaAt(width - 1, y) - AlphaAt(0, y)) <= interior + 1,
                "the ripple steps harder across its own wrap than anywhere inside it.");
        }
    }

    [Fact]
    public void The_ripple_is_slower_and_stronger_than_the_grain()
    {
        // They do opposite jobs. The grain is felt and not seen, at the scale of a pixel; the
        // ripple is seen across a whole panel. One tile size and one alpha say which is which.
        Assert.True(GlassNoise.RippleSize > GlassNoise.TileSize);
        Assert.True(GlassNoise.RippleStrength > GlassNoise.Strength);
    }

    // ==================================================================== the compositor

    [Fact]
    public void The_tint_is_packed_the_way_the_accent_policy_reads_it()
    {
        // Alpha, blue, green, red. Getting this backwards produces a tint that is the wrong
        // colour in a way that looks deliberate, which is the worst kind of wrong.
        Assert.Equal(unchecked((int)0x7A1C1714), AcrylicBackdrop.Tint(0x7A, 0x14, 0x17, 0x1C));
        Assert.Equal(unchecked((int)0xFFFFFFFF), AcrylicBackdrop.Tint(0xFF, 0xFF, 0xFF, 0xFF));
        Assert.Equal(0, AcrylicBackdrop.Tint(0, 0, 0, 0));
    }

    [Fact]
    public void Whether_windows_will_blur_at_all_is_asked_rather_than_assumed()
    {
        // "Transparency effects" is a global switch, and with it off DWM blurs nothing for
        // anybody: it substitutes a solid colour, and a panel mixed for a blur it is not getting
        // is a panel you can read your browser through. Asking costs a registry read.
        AcrylicBackdrop.Refresh();

        var disabled = AcrylicBackdrop.TransparencyDisabled;

        // The two must agree: transparency being off is never a state in which a blur is offered.
        Assert.False(disabled && AcrylicBackdrop.Available);

        // And the reason is reported rather than left as "it did not work".
        if (disabled)
        {
            Assert.Contains("transparency", AcrylicBackdrop.Method, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static double Contrast(string a, string b) => Counter.Core.Colour.Perceptual.Contrast(a, b);
}
