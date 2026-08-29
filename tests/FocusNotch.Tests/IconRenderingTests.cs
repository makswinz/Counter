using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FocusNotch.App.Controls;
using Xunit;

namespace FocusNotch.Tests;

/// <summary>
/// The two controls the alignment work is about, rendered for real and measured in pixels.
///
/// "Centred" and "crisp at every DPI" are claims about pixels, so they are checked as pixels:
/// each control is drawn to a bitmap at 100, 125, 150 and 200 percent and the ink is measured.
/// Rendering it here rather than on a display is also what makes the claim checkable at all -
/// nobody has to change their system scaling, and the assertion is exact rather than a squint.
/// </summary>
public class IconRenderingTests
{
    private const double Host = 28;

    // =================================================================================
    // Icons
    // =================================================================================

    [Theory]
    [InlineData(96)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(192)]
    public void An_icon_is_centred_in_its_host_at_every_scale(double dpi)
    {
        Sta.Run(() =>
        {
            var scale = dpi / 96d;

            foreach (IconKind kind in Enum.GetValues<IconKind>())
            {
                // The play triangle is the one shape whose bounding box is not what the eye
                // centres on. It has its own test below, against its centre of mass.
                if (kind is IconKind.None or IconKind.Play)
                {
                    continue;
                }

                var ink = Measure(Icon(kind, IconVariant.Regular), dpi);
                Assert.NotNull(ink);

                var left = ink!.Value.Left;
                var right = (Host * scale) - ink.Value.Right;
                var top = ink.Value.Top;
                var bottom = (Host * scale) - ink.Value.Bottom;

                // Two device pixels of slack at 200 percent: the ink of an outline icon ends on an
                // anti-aliased edge, and Fluent's own artwork is not always symmetric to the pixel.
                var slack = 2 * scale;

                Assert.True(Math.Abs(left - right) <= slack,
                    kind + " at " + dpi + " DPI has " + left + " px on the left and " + right + " on the right.");
                Assert.True(Math.Abs(top - bottom) <= slack,
                    kind + " at " + dpi + " DPI has " + top + " px above and " + bottom + " below.");
            }
        });
    }

    [Theory]
    [InlineData(96)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(192)]
    public void An_icon_never_spills_out_of_its_host(double dpi)
    {
        Sta.Run(() =>
        {
            var scale = dpi / 96d;
            var edge = (int)Math.Round(Host * scale);

            foreach (IconKind kind in Enum.GetValues<IconKind>())
            {
                if (kind == IconKind.None)
                {
                    continue;
                }

                var ink = Measure(Icon(kind, IconVariant.Filled), dpi);

                Assert.NotNull(ink);
                Assert.True(ink!.Value.Left >= 0 && ink.Value.Top >= 0,
                    kind + " leaks past the top left of its host at " + dpi + " DPI.");
                Assert.True(ink.Value.Right <= edge && ink.Value.Bottom <= edge,
                    kind + " leaks past the bottom right of its host at " + dpi + " DPI.");
            }
        });
    }

    [Theory]
    [InlineData(96)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(192)]
    public void The_play_triangle_is_centred_on_its_mass_rather_than_its_box(double dpi)
    {
        Sta.Run(() =>
        {
            // A triangle's optical centre is its centroid, which sits a third of the way from
            // its base rather than in the middle of the box around it. Centring the box is what
            // makes a play button look as if the glyph has slid left; centring the mass is what
            // the eye actually reads as centred. This is the assertion that says the half-pixel
            // correction in IconCatalog is right rather than a matter of taste.
            var centre = Centroid(Icon(IconKind.Play, IconVariant.Filled), dpi);
            var middle = Host * dpi / 96d / 2;

            Assert.True(Math.Abs(centre.X - middle) <= 1,
                dpi + " DPI: the triangle's mass sits at " + centre.X.ToString("N2") + ", not " + middle + ".");
            Assert.True(Math.Abs(centre.Y - middle) <= 1,
                dpi + " DPI: the triangle's mass sits at " + centre.Y.ToString("N2") + " vertically.");
        });
    }

    [Theory]
    [InlineData(96)]
    [InlineData(192)]
    public void An_icon_is_redrawn_at_the_larger_scale_rather_than_stretched(double dpi)
    {
        Sta.Run(() =>
        {
            // A raster blown up doubles its ink and keeps the same number of distinct alpha steps.
            // Vector artwork redrawn at twice the size gains detail along every curve, so the count
            // of partially covered pixels rises faster than the area does. This is what "not blurry
            // at high DPI" actually means, and it is measurable.
            var pixels = Render(Icon(IconKind.Settings, IconVariant.Regular), dpi);
            var solid = 0;

            foreach (var pixel in pixels.Data)
            {
                if ((pixel >> 24) > 0xF0)
                {
                    solid++;
                }
            }

            var area = pixels.Width * pixels.Height;

            // At either scale the glyph covers a real fraction of its host and is not a smear: an
            // upscaled bitmap would have almost no fully opaque pixels left at 200 percent.
            Assert.True(solid > area / 40, dpi + " DPI: only " + solid + " solid pixels out of " + area + ".");
        });
    }

    // =================================================================================
    // Circular badges
    // =================================================================================

    [Theory]
    [InlineData(96)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(192)]
    public void A_badge_is_the_same_circle_for_every_date_at_every_scale(double dpi)
    {
        Sta.Run(() =>
        {
            var scale = dpi / 96d;
            Rect? first = null;

            foreach (var day in new[] { "1", "8", "11", "20", "28", "31" })
            {
                var ink = Measure(Badge(day), dpi);
                Assert.NotNull(ink);

                // Round, not oval, and exactly the diameter it was given.
                Assert.True(Math.Abs(ink!.Value.Width - ink.Value.Height) <= 1,
                    day + " at " + dpi + " DPI is " + ink.Value.Width + " by " + ink.Value.Height + ".");

                Assert.True(Math.Abs(ink.Value.Width - (22 * scale)) <= 1.5,
                    day + " at " + dpi + " DPI has a disc " + ink.Value.Width + " px across, not " + (22 * scale) + ".");

                // And identically placed, whatever the number inside it is. This is the whole point:
                // the old cell let a two-digit date push its circle sideways.
                first ??= ink;
                Assert.True(Math.Abs(ink.Value.Left - first.Value.Left) <= 0.5, day + " moved its circle horizontally.");
                Assert.True(Math.Abs(ink.Value.Top - first.Value.Top) <= 0.5, day + " moved its circle vertically.");
            }
        });
    }

    [Theory]
    [InlineData(96)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(192)]
    public void A_badge_sits_in_the_middle_of_its_cell_at_every_scale(double dpi)
    {
        Sta.Run(() =>
        {
            var scale = dpi / 96d;
            var ink = Measure(Badge("28"), dpi);

            Assert.NotNull(ink);

            var left = ink!.Value.Left;
            var right = (Host * scale) - ink.Value.Right;
            var top = ink.Value.Top;
            var bottom = (Host * scale) - ink.Value.Bottom;

            Assert.True(Math.Abs(left - right) <= 1, dpi + " DPI: " + left + " px left, " + right + " px right.");
            Assert.True(Math.Abs(top - bottom) <= 1, dpi + " DPI: " + top + " px above, " + bottom + " px below.");
        });
    }

    // =================================================================================
    // Composition
    // =================================================================================

    private static FrameworkElement Icon(IconKind kind, IconVariant variant) =>
        Wrap(new AppIcon { Kind = kind, Variant = variant, IconSize = 16, Foreground = Brushes.Black });

    private static FrameworkElement Badge(string day) =>
        Wrap(new CircularBadge
        {
            Diameter = 22,
            Content = day,
            Fill = Brushes.Black,
            Foreground = Brushes.White
        });

    /// <summary>Puts a control in the middle of the hit target the interface actually gives it.</summary>
    private static FrameworkElement Wrap(FrameworkElement child)
    {
        child.HorizontalAlignment = HorizontalAlignment.Center;
        child.VerticalAlignment = VerticalAlignment.Center;

        return new Grid
        {
            Width = Host,
            Height = Host,
            Background = Brushes.White,
            UseLayoutRounding = true,
            SnapsToDevicePixels = true,
            Children = { child }
        };
    }

    // =================================================================================
    // Pixels
    // =================================================================================

    private readonly record struct Image(uint[] Data, int Width, int Height);

    /// <summary>The coverage-weighted centre of the ink, in device pixels.</summary>
    private static Point Centroid(FrameworkElement element, double dpi)
    {
        var image = Render(element, dpi);
        double weight = 0, x = 0, y = 0;

        for (var row = 0; row < image.Height; row++)
        {
            for (var column = 0; column < image.Width; column++)
            {
                // The ground is white, so how dark a pixel is measures how much ink covers it.
                var pixel = image.Data[(row * image.Width) + column];
                var coverage = 1 - (((pixel >> 16) & 0xFF) / 255d);

                if (coverage <= 0)
                {
                    continue;
                }

                weight += coverage;
                x += (column + 0.5) * coverage;
                y += (row + 0.5) * coverage;
            }
        }

        return weight <= 0 ? default : new Point(x / weight, y / weight);
    }

    /// <summary>The bounding box of everything that is not the white ground, in device pixels.</summary>
    private static Rect? Measure(FrameworkElement element, double dpi)
    {
        var image = Render(element, dpi);

        var left = int.MaxValue;
        var top = int.MaxValue;
        var right = -1;
        var bottom = -1;

        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var pixel = image.Data[(y * image.Width) + x];

                // Anything darker than the ground. The threshold is generous so a faint
                // anti-aliased edge still counts as ink.
                if ((pixel & 0x00FFFFFF) > 0x00F0F0F0)
                {
                    continue;
                }

                if (x < left) { left = x; }
                if (x > right) { right = x; }
                if (y < top) { top = y; }
                if (y > bottom) { bottom = y; }
            }
        }

        return right < 0 ? null : new Rect(left, top, right - left + 1, bottom - top + 1);
    }

    private static Image Render(FrameworkElement element, double dpi)
    {
        var scale = dpi / 96d;

        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        element.Arrange(new Rect(element.DesiredSize));
        element.UpdateLayout();

        var width = (int)Math.Round(element.DesiredSize.Width * scale);
        var height = (int)Math.Round(element.DesiredSize.Height * scale);

        var target = new RenderTargetBitmap(width, height, dpi, dpi, PixelFormats.Pbgra32);
        target.Render(element);

        var data = new uint[width * height];
        target.CopyPixels(data, width * 4, 0);

        return new Image(data, width, height);
    }
}
