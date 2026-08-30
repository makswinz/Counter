using Counter.App.Services;
using Counter.App.Views;
using Counter.Core.Models;
using Xunit;

namespace Counter.Tests;

/// <summary>
/// Moving the notch out from under a browser's tab strip.
///
/// It sits at the top centre of the screen, which is exactly where a browser keeps its tabs. The
/// window is always the same width and always lands on whole device pixels; moving it sideways
/// must not change either of those, and must never put any part of it off the screen.
/// </summary>
public class PlacementTests
{
    private const int WindowWidth = 632;

    private static MonitorInfo Monitor(int width = 1920, int left = 0, double scale = 1.0)
        => new("\\\\.\\DISPLAY1", "Display 1", left, 0, width, 1080,
            left, 0, width, 1032, left == 0, scale);

    private static int Origin(NotchPlacement placement, MonitorInfo monitor) =>
        NotchGeometryCoordinator.HorizontalOrigin(monitor, WindowWidth, 24, monitor.Scale, placement);

    [Theory]
    [InlineData(NotchPlacement.Left)]
    [InlineData(NotchPlacement.Centre)]
    [InlineData(NotchPlacement.Right)]
    public void The_window_stays_entirely_on_the_screen(NotchPlacement placement)
    {
        foreach (var width in new[] { 800, 1280, 1920, 2560, 3840 })
        {
            foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
            {
                var monitor = Monitor(width, 0, scale);
                var x = Origin(placement, monitor);

                Assert.True(x >= monitor.Left,
                    placement + " at " + width + " starts off the left edge.");

                Assert.True(x + WindowWidth <= monitor.Left + monitor.Width,
                    placement + " at " + width + " runs off the right edge.");
            }
        }
    }

    [Fact]
    public void The_three_placements_are_ordered_and_actually_move_it()
    {
        var monitor = Monitor();

        var left = Origin(NotchPlacement.Left, monitor);
        var centre = Origin(NotchPlacement.Centre, monitor);
        var right = Origin(NotchPlacement.Right, monitor);

        Assert.True(left < centre, "left is not left of centre");
        Assert.True(centre < right, "right is not right of centre");

        // The whole point is clearing the middle of the screen. A placement that shifts the
        // window by forty pixels has not got out of anybody's way.
        Assert.True(right - left > 600, "the side placements barely move the window");
    }

    [Fact]
    public void Centring_rounds_rather_than_truncating()
    {
        // Rounding the share rather than truncating an offset is what stops the shell sliding
        // half a pixel sideways as its width animates.
        var monitor = Monitor(1921);

        Assert.Equal(
            (int)Math.Round((1921 - WindowWidth) / 2d, MidpointRounding.AwayFromZero),
            Origin(NotchPlacement.Centre, monitor));
    }

    [Fact]
    public void A_monitor_left_of_the_origin_is_placed_relative_to_itself()
    {
        // A second display can have a negative left edge, and every placement is an offset from
        // that rather than from zero.
        var monitor = Monitor(1920, left: -1920);

        Assert.True(Origin(NotchPlacement.Left, monitor) >= -1920);
        Assert.True(Origin(NotchPlacement.Right, monitor) + WindowWidth <= 0);
    }

    [Fact]
    public void A_display_narrower_than_the_window_still_gets_a_usable_origin()
    {
        // Nothing fits, so nothing is offset: the window starts at the left edge rather than at
        // a negative coordinate nobody can reach.
        var monitor = Monitor(480);

        foreach (var placement in NotchPlacements.All)
        {
            Assert.Equal(monitor.Left, Origin(placement, monitor));
        }
    }

    [Theory]
    [InlineData("Left", NotchPlacement.Left)]
    [InlineData("centre", NotchPlacement.Centre)]
    [InlineData("  RIGHT  ", NotchPlacement.Right)]
    [InlineData("sideways", NotchPlacement.Centre)]
    [InlineData(null, NotchPlacement.Centre)]
    [InlineData("2", NotchPlacement.Centre)]
    public void A_stored_placement_round_trips_or_falls_back(string? stored, NotchPlacement expected) =>
        Assert.Equal(expected, NotchPlacements.Parse(stored));
}
