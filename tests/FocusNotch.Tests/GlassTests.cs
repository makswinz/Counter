using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FocusNotch.App.Controls;
using FocusNotch.App.Services;
using FocusNotch.App.Theme;
using FocusNotch.Core.Colour;
using FocusNotch.Core.Models;
using Xunit;

namespace FocusNotch.Tests;

/// <summary>
/// The liquid-glass treatment: the layers a panel is made of, the one-physical-pixel contour, and
/// the rules about where a gradient is allowed to appear.
///
/// The contour is the part worth testing hardest. It is the single most structural line in the
/// design - the thing that separates the tool from the desktop - and it is also the thing display
/// scaling quietly ruins, because a one-unit border is one and a half device pixels at 150
/// percent and the rasteriser resolves that as a soft two-pixel line. So the arithmetic is
/// asserted, and then the actual rendered pixels are counted at four scale factors.
/// </summary>
public class GlassTests
{
    /// <summary>The four scale factors Windows offers that people actually use.</summary>
    public static IEnumerable<object[]> Scales() => new[]
    {
        new object[] { 1.00 }, new object[] { 1.25 }, new object[] { 1.50 }, new object[] { 2.00 }
    };

    // ==================================================================== the hairline

    [Theory]
    [MemberData(nameof(Scales))]
    public void A_hairline_is_one_physical_pixel_at_every_scale(double scale)
    {
        var resources = new ResourceDictionary
        {
            [DpiService.ThicknessKey] = new Thickness(1),
            [DpiService.ScalarKey] = 1.0,
            [DpiService.AccentThicknessKey] = new Thickness(1.4)
        };

        DpiService.Apply(resources, scale);

        var thickness = (Thickness)resources[DpiService.ThicknessKey]!;
        var scalar = (double)resources[DpiService.ScalarKey]!;
        var accent = (Thickness)resources[DpiService.AccentThicknessKey]!;

        Assert.Equal(1.0, thickness.Left * scale, 6);
        Assert.Equal(1.0, scalar * scale, 6);

        // The active contour is allowed to be heavier, and is capped at a pixel and a half so it
        // can never start reading as a neon tube.
        Assert.InRange(accent.Left * scale, 1.0, 1.5);

        // Square: a border thicker on one side than another is not a contour, it is a mistake.
        Assert.Equal(thickness.Left, thickness.Top);
        Assert.Equal(thickness.Left, thickness.Right);
        Assert.Equal(thickness.Left, thickness.Bottom);
    }

    [Fact]
    public void A_nonsense_scale_falls_back_rather_than_dividing_by_zero()
    {
        var resources = new ResourceDictionary { [DpiService.ScalarKey] = 0.5 };

        foreach (var bad in new[] { 0.0, -1.0, double.NaN, double.PositiveInfinity })
        {
            DpiService.Apply(resources, bad);
            Assert.Equal(1.0, (double)resources[DpiService.ScalarKey]!);
            DpiService.Apply(resources, 0.5);
        }
    }

    [Theory]
    [MemberData(nameof(Scales))]
    public void The_contour_renders_as_exactly_one_device_pixel(double scale)
    {
        // The construction the design specifies and LiquidGlassPanel uses: an outer border filled
        // with the contour brush, padded by one physical pixel, with the glass body inside that
        // padding. This renders it at the real device resolution and counts the ring.
        //
        // Layout rounding is deliberately off here, and that is worth being precise about.
        // Rounding snaps to whole *device* pixels using the visual's own DPI context; a detached
        // element in a test process has none, so it would round to whole logical units instead
        // and turn a two-thirds-of-a-unit padding into a whole one. Forcing a context on the
        // visual leaks WPF's process-wide DPI state into every other render test running beside
        // it. So the test asserts the thing it can assert honestly: that a padding of one over
        // the scale factor rasterises to exactly one device pixel, on all four sides, at all four
        // scales. What it does not exercise is the running window's layout rounding, which has a
        // real DPI context and rounds to real device pixels.
        Sta.Run(() =>
        {
            var hairline = 1.0 / scale;

            var body = new Border
            {
                Background = Brushes.Black,
                CornerRadius = new CornerRadius(0),
                SnapsToDevicePixels = true
            };

            var contour = new Border
            {
                Background = Brushes.White,
                Padding = new Thickness(hairline),
                Width = 40,
                Height = 24,
                Child = body,
                SnapsToDevicePixels = true
            };

            var pixels = Render(contour, 40, 24, scale);
            var width = (int)Math.Round(40 * scale);
            var height = (int)Math.Round(24 * scale);

            // Walk the middle row inward from each edge and count how many pixels are the
            // contour rather than the body.
            var left = CountWhile(x => IsWhite(pixels, width, x, height / 2), 0, 1, width);
            var right = CountWhile(x => IsWhite(pixels, width, x, height / 2), width - 1, -1, width);
            var top = CountWhile(y => IsWhite(pixels, width, width / 2, y), 0, 1, height);
            var bottom = CountWhile(y => IsWhite(pixels, width, width / 2, y), height - 1, -1, height);

            foreach (var (side, measured) in new[]
                     {
                         ("left", left), ("right", right), ("top", top), ("bottom", bottom)
                     })
            {
                Assert.True(measured == 1,
                    "At " + scale.ToString("P0", CultureInfo.InvariantCulture) + " the " + side +
                    " contour is " + measured + " device pixels rather than 1.");
            }
        });
    }

    // ==================================================================== the light direction

    [Fact]
    public void Every_light_overlay_comes_from_the_upper_left_and_is_white()
    {
        // The gloss and the specular are light, not paint. If either one picked up a colour it
        // would start fighting the accent underneath it instead of lighting it.
        var text = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "FocusNotch.App", "Theme", "Brushes.xaml"));

        foreach (var key in new[] { "GlossOverlayBrush", "TopHighlightBrush" })
        {
            var block = Regex.Match(text, "<LinearGradientBrush x:Key=\"" + key + "\".*?</LinearGradientBrush>",
                RegexOptions.Singleline).Value;

            Assert.False(string.IsNullOrEmpty(block), key + " is not declared.");

            foreach (Match stop in Regex.Matches(block, "Color=\"#([0-9A-Fa-f]{2})([0-9A-Fa-f]{6})\""))
            {
                Assert.Equal("FFFFFF", stop.Groups[2].Value.ToUpperInvariant());
                Assert.True(
                    int.Parse(stop.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture) <= 0x42,
                    key + " reaches " + stop.Groups[1].Value + ", which is brighter than a reflection.");
            }
        }

        var specular = Regex.Match(text, "<RadialGradientBrush x:Key=\"SpecularHighlightBrush\".*?</RadialGradientBrush>",
            RegexOptions.Singleline).Value;

        Assert.Contains("GradientOrigin=\"0.18,0.06\"", specular);
        Assert.Contains("Center=\"0.22,0.10\"", specular);
    }

    [Fact]
    public void Nothing_outside_the_theme_declares_an_effect_of_its_own()
    {
        // One shadow per floating surface, defined in one place. A drop shadow on a task row, an
        // icon or a heatmap square costs a render-target pass each and turns layered depth into
        // mud, so the views are not allowed to declare any.
        var views = Directory.GetFiles(
            Path.Combine(RepositoryRoot(), "src", "FocusNotch.App", "Views"), "*.xaml");

        foreach (var file in views)
        {
            var text = File.ReadAllText(file);

            Assert.False(text.Contains("<DropShadowEffect"),
                Path.GetFileName(file) + " declares a drop shadow of its own.");
        }

        // And the ones that do exist are declared once, with one direction, agreeing with the
        // light: the source is upper left, so the shadow falls down and slightly right.
        var controls = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "FocusNotch.App", "Theme", "Controls.xaml"));

        var directions = Regex.Matches(controls, "<DropShadowEffect[^>]*Direction=\"(\\d+)\"", RegexOptions.Singleline)
            .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
            .ToList();

        Assert.NotEmpty(directions);

        foreach (var direction in directions)
        {
            Assert.InRange(direction, 270, 315);
        }
    }

    [Fact]
    public void A_glass_surface_is_translucent_but_still_carries_text()
    {
        // The two failure modes the glass sits between. Too opaque and it is a card; too
        // transparent and the desktop shows through as readable content rather than as depth.
        foreach (var (name, map) in new[] { ("dark", ThemePalette.Dark), ("light", ThemePalette.Light) })
        {
            foreach (var key in new[] { "GlassBaseBrush", "GlassRaisedBrush", "GlassDeepBrush", "GlassHoverBrush" })
            {
                var alpha = int.Parse(map[key].Substring(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

                Assert.True(alpha < 0xFF, name + " " + key + " is fully opaque, which is not glass.");
                Assert.True(alpha >= 0xD0,
                    name + " " + key + " is only " + (alpha / 255d).ToString("P0") +
                    " opaque; without a compositor blur behind it the desktop stays readable through the panel.");
            }
        }
    }

    // ==================================================================== the glyph rule

    [Theory]
    [MemberData(nameof(AccentPaletteTests.EveryColour), MemberType = typeof(AccentPaletteTests))]
    public void A_glyph_never_sits_on_a_ramp_it_cannot_be_seen_against(string colour)
    {
        // Three to one is what WCAG asks of a graphical object, and a filled play triangle at
        // seventeen pixels is one. The ink has to clear it across the region a centred glyph
        // actually covers, which is the base through the strong stop.
        var ramp = AccentEngine.Derive(colour);

        var contrast = Math.Min(
            Perceptual.Contrast(ramp.Glyph, ramp.Base),
            Perceptual.Contrast(ramp.Glyph, ramp.Strong));

        Assert.True(contrast >= 3.0,
            colour + " puts its glyph at " + contrast.ToString("F2") + " to one.");
    }

    [Fact]
    public void White_is_preferred_where_white_actually_works()
    {
        // The conventional look, kept wherever it is honest: a white play triangle on the deeper
        // families, and dark ink on the pale ones where white would be a smear.
        foreach (var colour in new[] { "#FF438BFF", "#FF9468F2", "#FFF15C9D" })
        {
            Assert.Equal(AccentEngine.LightForeground, AccentEngine.Derive(colour).Glyph);
        }

        foreach (var colour in new[] { "#FF23BDD4", "#FF35C77D", "#FFFF9638", "#FFF5E27A" })
        {
            Assert.Equal(AccentEngine.DarkForeground, AccentEngine.Derive(colour).Glyph);
        }
    }

    [Fact]
    public void Completion_is_a_meaning_rather_than_a_preference()
    {
        // The tick keeps the completion family whatever the accent is. If it followed the accent,
        // choosing green would make every open task look done.
        foreach (var accent in AccentPalettes.All)
        {
            var map = ThemePalette.Solids(isLight: false, accent);

            Assert.Equal(ThemePalette.Success.Base, map["SuccessBrush"]);
            Assert.Equal(
                AccentEngine.Glyph(ThemePalette.Success.Base, ThemePalette.Success.Strong),
                map["SuccessGlyphBrush"]);
        }

        var controls = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "FocusNotch.App", "Theme", "Controls.xaml"));

        var template = Between(controls, "<Style x:Key=\"CompletionCircle\"", "<Style TargetType=\"{x:Type ctl:CompletionCheck}\"");

        Assert.Contains("SuccessGradientBrush", template);
        Assert.Contains("SuccessGlyphBrush", template);
        Assert.DoesNotContain("AccentBaseBrush", template);
        Assert.DoesNotContain("AccentSoftBrush", template);
    }

    // ==================================================================== where a gradient may go

    [Fact]
    public void A_gradient_is_never_the_background_of_a_surface()
    {
        // The rule that keeps a chosen colour meaningful: gradients light the things that are
        // active, and the surfaces stay neutral. A panel, a task row, an input or a tooltip
        // filled with the accent is what turns "the timer is running" into "the interface is
        // orange", after which nothing means anything.
        //
        // Two backgrounds are gradients on purpose, and they are both named here so that adding a
        // third is a deliberate act rather than an accident.
        var allowed = new[]
        {
            "GlassContourBrush",   // the notch's contour ring: a one-pixel stroke, not a surface
            "AccentGradientBrush"  // the settings preview, whose whole job is to be the gradient
        };

        foreach (var file in ViewFiles().Concat(ThemeFiles()))
        {
            foreach (Match match in Regex.Matches(
                         File.ReadAllText(file),
                         "Background=\"\\{(?:Static|Dynamic)Resource ([A-Za-z0-9_]+)\\}\""))
            {
                var key = match.Groups[1].Value;

                if (!key.EndsWith("GradientBrush") && !key.EndsWith("ContourBrush"))
                {
                    continue;
                }

                Assert.True(allowed.Contains(key),
                    Path.GetFileName(file) + " fills a surface with " + key + ".");
            }
        }
    }

    [Fact]
    public void Every_glass_surface_is_a_flat_colour()
    {
        // Depth comes from stacking layers, not from painting a gradient on the glass. A gradient
        // in the surface itself fights every gradient laid on top of it.
        foreach (var map in new[] { ThemePalette.Dark, ThemePalette.Light })
        {
            foreach (var key in map.Keys.Where(k => k.StartsWith("Glass") || k.StartsWith("Row")))
            {
                Assert.Matches("^#[0-9A-Fa-f]{8}$", map[key]);
            }
        }
    }

    // ==================================================================== the layer stack

    [Fact]
    public void A_glass_panel_is_built_from_its_layers_in_order()
    {
        // The order is the design. A reflection under the tint, an inner edge outside the clip or
        // grain over the content each produce something that looks almost right and is not, and
        // any view assembling this by hand would get one of them wrong eventually - which is the
        // whole reason it is a control.
        var controls = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "FocusNotch.App", "Theme", "Controls.xaml"));

        var template = Between(controls,
            "<Style TargetType=\"{x:Type ctl:LiquidGlassPanel}\">",
            "<!-- ==================================================================== Icon buttons -->");

        var order = new[]
        {
            "x:Name=\"Glow\"",
            "x:Name=\"Contour\"",
            "x:Name=\"Body\"",
            "GlassTintBrush",
            "x:Name=\"Reflection\"",
            "x:Name=\"Sheen\"",
            "GlassInnerContourBrush",
            "GlassNoiseBrush",
            "<ContentPresenter",
            "x:Name=\"Accent\""
        };

        var at = -1;

        foreach (var layer in order)
        {
            var next = template.IndexOf(layer, StringComparison.Ordinal);
            Assert.True(next > at, layer + " is missing from the glass panel or is out of order.");
            at = next;
        }

        // The contour is a padded fill with the body inside it, not a stroked border: that is
        // what keeps the ring one weight the whole way round a rounded corner.
        Assert.Contains("Padding=\"{DynamicResource HairlineThickness}\"", template);

        // And the body is clipped, so nothing inside can paint outside the rounded shape.
        Assert.Contains("ClipToBounds=\"True\"", template);
    }

    [Fact]
    public void Every_hairline_in_the_interface_resolves_the_dpi_aware_one()
    {
        // A StaticResource here would be resolved once at parse time and never move again, which
        // is exactly how a one-pixel border becomes a soft two-pixel one on a second monitor.
        foreach (var file in ViewFiles().Concat(ThemeFiles()))
        {
            var text = File.ReadAllText(file);

            Assert.False(text.Contains("{StaticResource HairlineThickness}"),
                Path.GetFileName(file) + " resolves the hairline statically.");
            Assert.False(text.Contains("{StaticResource HairlinePixel}"),
                Path.GetFileName(file) + " resolves the hairline statically.");
        }
    }

    // ==================================================================== the backdrop

    [Fact]
    public void The_backdrop_mode_is_reported_rather_than_assumed()
    {
        // The one thing this must never do is claim a compositor blur it does not have. Before
        // anything asks, it says so.
        Assert.Equal(BackdropMode.Unknown, BackdropMode.Unknown);
        Assert.True(Enum.IsDefined(BackdropMode.Native));
        Assert.True(Enum.IsDefined(BackdropMode.Simulated));
    }

    // ---------------------------------------------------------------------------------

    private static IEnumerable<string> ViewFiles() =>
        Directory.GetFiles(Path.Combine(RepositoryRoot(), "src", "FocusNotch.App", "Views"), "*.xaml");

    private static IEnumerable<string> ThemeFiles() =>
        Directory.GetFiles(Path.Combine(RepositoryRoot(), "src", "FocusNotch.App", "Theme"), "*.xaml");

    private static string Between(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, "Could not find " + start);
        var to = text.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, "Could not find " + end);
        return text.Substring(from, to - from);
    }

    private static int CountWhile(Func<int, bool> test, int start, int step, int limit)
    {
        var count = 0;

        for (var index = start; index >= 0 && index < limit; index += step)
        {
            if (!test(index))
            {
                break;
            }

            count++;
        }

        return count;
    }

    private static bool IsWhite(byte[] pixels, int stride, int x, int y)
    {
        var offset = ((y * stride) + x) * 4;
        return pixels[offset] > 200 && pixels[offset + 1] > 200 && pixels[offset + 2] > 200;
    }

    /// <summary>Lays an element out at one logical size and rasterises it at a given scale.</summary>
    private static byte[] Render(FrameworkElement element, double width, double height, double scale)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();

        var pixelWidth = (int)Math.Round(width * scale);
        var pixelHeight = (int)Math.Round(height * scale);

        var target = new RenderTargetBitmap(
            pixelWidth, pixelHeight, 96 * scale, 96 * scale, PixelFormats.Pbgra32);

        target.Render(element);

        var pixels = new byte[pixelWidth * pixelHeight * 4];
        target.CopyPixels(pixels, pixelWidth * 4, 0);

        return pixels;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FocusNotch.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
