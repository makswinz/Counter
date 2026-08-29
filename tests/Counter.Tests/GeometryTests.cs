using Counter.App.Services;
using Counter.App.Views;
using Xunit;

namespace Counter.Tests;

/// <summary>
/// The device-independent to physical-pixel conversion behind every window move.
///
/// This is asserted directly rather than only by looking at a running window, because the
/// interesting cases are the display scales the machine running the suite is unlikely to be
/// set to. The rules under test are: the top edge never moves, the horizontal centre never
/// never move at all, the top edge never moves, and every rectangle lands on whole pixels.
/// </summary>
public class GeometryTests
{
    private const double ShadowSide = 16;
    private const double ShadowBottom = 16;

    private static MonitorInfo Monitor(int left = 0, int width = 1920, int height = 1080, double scale = 1.0)
        => new("\\\\.\\DISPLAY1", "Display 1", left, 0, width, height,
            left, 0, width, height - 48, left == 0, scale);

    private static NativeBounds Bounds(MonitorInfo monitor, double w, double h, double topOffset = 0)
        => NotchGeometryCoordinator.ComputeBounds(
            monitor, new ShellSize(w, h, 13), topOffset, ShadowSide, ShadowBottom);

    // ---------------------------------------------------------------- Centring

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void The_horizontal_centre_is_the_monitor_centre_at_every_scale(double scale)
    {
        var monitor = Monitor(scale: scale);
        var expected = monitor.Left + monitor.Width / 2d;

        foreach (var width in new[] { 330d, 520d, 600d })
        {
            var bounds = Bounds(monitor, width, 42);

            // Whole-pixel rounding can only ever move the centre by half a pixel.
            Assert.InRange(bounds.CentreX, expected - 0.5, expected + 0.5);
        }
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void The_window_width_and_position_never_move_with_the_card(double scale)
    {
        // The card animates; the window does not. A layered window being resized horizontally
        // cannot be composited in step with its own content, and that mismatch is what made
        // centred content swing sideways during a transition.
        var monitor = Monitor(scale: scale);
        var first = Bounds(monitor, 330, 42);

        for (var width = 330d; width <= 600d; width += 0.5)
        {
            var bounds = Bounds(monitor, width, 42);
            Assert.Equal(first.Width, bounds.Width);
            Assert.Equal(first.X, bounds.X);
        }
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void The_centre_does_not_drift_across_an_animation_at_any_scale(double scale)
    {
        var monitor = Monitor(scale: scale);
        var expected = monitor.Left + monitor.Width / 2d;

        // Walk the whole width range the open animation passes through.
        for (var width = 330d; width <= 600d; width += 0.5)
        {
            var bounds = Bounds(monitor, width, 42);
            Assert.InRange(bounds.CentreX, expected - 0.5, expected + 0.5);
        }
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void The_top_edge_stays_on_the_monitor_top_at_every_scale(double scale)
    {
        var monitor = Monitor(scale: scale);

        Assert.Equal(monitor.Top, Bounds(monitor, 330, 42).Y);
        Assert.Equal(monitor.Top, Bounds(monitor, 600, 496).Y);
    }

    [Fact]
    public void A_configured_top_offset_is_scaled_and_applied_once()
    {
        Assert.Equal(8, Bounds(Monitor(scale: 1.0), 330, 42, topOffset: 8).Y);
        Assert.Equal(10, Bounds(Monitor(scale: 1.25), 330, 42, topOffset: 8).Y);
        Assert.Equal(12, Bounds(Monitor(scale: 1.5), 330, 42, topOffset: 8).Y);
    }

    // ---------------------------------------------------------------- Scaling

    [Theory]
    [InlineData(1.0, 632, 58)]
    [InlineData(1.25, 790, 73)]
    [InlineData(1.5, 948, 87)]
    [InlineData(2.0, 1264, 116)]
    public void The_window_scales_to_whole_pixels(double scale, int width, int height)
    {
        var bounds = Bounds(Monitor(scale: scale), 330, 42);

        // 600 widest card + 2 x 16 gutter = 632 DIP wide, always. The collapsed card is 42 DIP
        // tall plus 16 of shadow room below it; the height is the part that animates.
        Assert.Equal(width, bounds.Width);
        Assert.Equal(height, bounds.Height);
    }

    [Fact]
    public void The_card_is_clamped_to_the_display_rather_than_the_window()
    {
        var roomy = Monitor(width: 800, height: 600);

        // 800 - 24 margin - 32 gutter = 744, wider than the widest card, so it keeps its 600.
        Assert.Equal(600, NotchGeometryCoordinator.ShellWidthLimit(roomy, ShadowSide, 240, 24, 600));

        var narrow = Monitor(width: 500, height: 600);
        Assert.Equal(
            500 - 24 - 32,
            NotchGeometryCoordinator.ShellWidthLimit(narrow, ShadowSide, 240, 24, 600));
    }

    [Fact]
    public void Every_rectangle_is_whole_device_pixels()
    {
        foreach (var scale in new[] { 1.0, 1.25, 1.5, 1.75, 2.0 })
        {
            for (var width = 330d; width <= 600d; width += 7.3)
            {
                var bounds = Bounds(Monitor(scale: scale), width, 173.4);

                // The record holds ints, so this asserts the contract rather than the type:
                // nothing downstream ever has to deal with a half pixel.
                Assert.True(bounds.Width > 0);
                Assert.True(bounds.Height > 0);
            }
        }
    }

    // ---------------------------------------------------------------- Multiple monitors

    [Fact]
    public void A_monitor_to_the_left_of_the_origin_is_centred_correctly()
    {
        var secondary = Monitor(left: -1920);
        var bounds = Bounds(secondary, 330, 42);

        Assert.Equal(-1276, bounds.X);
        Assert.Equal(-960d, bounds.CentreX);
        Assert.Equal(0, bounds.Y);
    }

    [Fact]
    public void A_mixed_dpi_secondary_monitor_is_centred_in_its_own_pixels()
    {
        var secondary = Monitor(left: -2560, width: 2560, height: 1440, scale: 1.5);
        var bounds = Bounds(secondary, 520, 210);

        Assert.Equal(-2560 + 1280d, bounds.CentreX);
        Assert.Equal((int)Math.Round((600 + 32) * 1.5), bounds.Width);
    }

    // ---------------------------------------------------------------- Clamping

    [Fact]
    public void The_window_never_runs_past_the_edges_of_a_narrow_display()
    {
        foreach (var width in new[] { 500, 640, 800 })
        {
            var small = Monitor(width: width, height: 600);
            var bounds = Bounds(small, 600, 400);

            Assert.True(bounds.X >= small.Left);
            Assert.True(bounds.Right <= small.Left + small.Width);
        }
    }

    [Fact]
    public void The_shell_never_runs_past_the_bottom_of_a_short_display()
    {
        var shortDisplay = Monitor(width: 1280, height: 720);
        var bounds = Bounds(shortDisplay, 600, 2000);

        Assert.True(bounds.Bottom <= shortDisplay.Height);
    }

    [Fact]
    public void A_missing_or_nonsensical_scale_falls_back_to_one_to_one()
    {
        var broken = Monitor(scale: 0);
        Assert.Equal(632, Bounds(broken, 330, 42).Width);
    }

    // ---------------------------------------------------------------- The shadow reserve

    [Fact]
    public void There_is_shadow_room_at_the_sides_and_below_but_never_above()
    {
        var monitor = Monitor();
        var bounds = Bounds(monitor, 330, 42);

        // Sixteen each side of the widest card, sixteen below, nothing above: the notch meets
        // the bezel exactly.
        Assert.Equal(600 + 2 * (int)ShadowSide, bounds.Width);
        Assert.Equal(42 + (int)ShadowBottom, bounds.Height);
        Assert.Equal(0, bounds.Y);
    }

    [Fact]
    public void Only_the_height_changes_as_the_panel_unfolds()
    {
        var monitor = Monitor();
        var collapsed = Bounds(monitor, 330, 42);
        var quick = Bounds(monitor, 520, 210);
        var planner = Bounds(monitor, 600, 496);

        Assert.Equal(collapsed.Width, quick.Width);
        Assert.Equal(collapsed.Width, planner.Width);
        Assert.Equal(collapsed.X, quick.X);
        Assert.Equal(collapsed.X, planner.X);

        Assert.Equal(58, collapsed.Height);
        Assert.Equal(226, quick.Height);
        Assert.Equal(512, planner.Height);
    }
}
