using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Counter.App.Controls;
using Counter.Core.Journey;
using Counter.Core.Streaks;
using Xunit;

namespace Counter.Tests;

/// <summary>
/// The heatmap, rendered for real and then inspected pixel by pixel.
///
/// "Crisp at every DPI" is a claim about pixels, so it is checked as one: the control is drawn
/// to a bitmap at 100, 125, 150 and 200 percent and the straight edges of the squares are
/// examined for the blending that appears when a square straddles the device pixel grid.
/// Rendering it here rather than on a display also means the assertion holds without anybody
/// having to change their system scaling.
///
/// The corners are deliberately excluded. A two-pixel radius is a curve, and a curve is
/// anti-aliased on purpose; the straight edges between the corners are what has to be exact.
/// </summary>
public class HeatmapRenderingTests
{
    private static readonly DateOnly Today = new(2026, 8, 29);

    private const int Cell = 9;
    private const int Gap = 3;
    private const int Pitch = Cell + Gap;

    [Theory]
    [InlineData(96)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(192)]
    public void The_vertical_edges_of_a_square_are_hard_at_every_scale(double dpi)
    {
        var image = Render(dpi);
        var scale = dpi / 96d;

        // A scanline through the middle of the first row of squares: well clear of the corner
        // curves, so every pixel on it belongs entirely to a square or entirely to a gap.
        var y = (int)Math.Round((Cell / 2d) * scale);
        var values = new HashSet<uint>();

        for (var x = 0; x < image.Width; x++)
        {
            values.Add(image.At(x, y));
        }

        Assert.True(
            values.Count == 2,
            "At " + dpi + " DPI a scanline across the squares held " + values.Count +
            " distinct values instead of two, so the vertical edges are blended.");
    }

    [Theory]
    [InlineData(96)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(192)]
    public void The_horizontal_edges_of_a_square_are_hard_at_every_scale(double dpi)
    {
        var image = Render(dpi);
        var scale = dpi / 96d;

        // The same check the other way: down the middle of the first filled column.
        var x = (int)Math.Round((Cell / 2d) * scale);
        var values = new HashSet<uint>();

        for (var y = 0; y < image.Height; y++)
        {
            values.Add(image.At(x, y));
        }

        Assert.True(
            values.Count == 2,
            "At " + dpi + " DPI a scanline down the squares held " + values.Count +
            " distinct values instead of two, so the horizontal edges are blended.");
    }

    [Theory]
    [InlineData(96)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(192)]
    public void Every_square_is_the_same_width_in_device_pixels(double dpi)
    {
        var image = Render(dpi);
        var scale = dpi / 96d;
        var y = (int)Math.Round((Cell / 2d) * scale);

        // Walk the scanline and measure each run of filled pixels. Squares that were snapped to
        // the grid are all the same width; squares that were not differ by a pixel here and
        // there, which is exactly what makes a grid look ragged.
        //
        // "Filled" is read from the middle of the first square rather than assumed, so the test
        // does not depend on which value the renderer happens to leave in the corners.
        var runs = new List<int>();
        var run = 0;
        var filled = image.At((int)Math.Round((Cell / 2d) * scale), y);

        for (var x = 0; x < image.Width; x++)
        {
            if (image.At(x, y) == filled)
            {
                run++;
                continue;
            }

            if (run > 0)
            {
                runs.Add(run);
                run = 0;
            }
        }

        if (run > 0)
        {
            runs.Add(run);
        }

        Assert.Equal(6, runs.Count);                        // twelve columns, every other filled
        Assert.Single(runs.Distinct());
        Assert.Equal((int)Math.Round(Cell * scale), runs[0]);
    }

    [Fact]
    public void The_control_reports_the_size_twelve_weeks_of_squares_actually_need()
    {
        var size = OnStaThread(() =>
        {
            var control = Build();
            control.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return control.DesiredSize;
        });

        // Twelve columns and seven rows at nine plus three, less the trailing gap.
        Assert.Equal(12 * Pitch - Gap, size.Width);
        Assert.Equal(7 * Pitch - Gap, size.Height);
    }

    // ---------------------------------------------------------------------------------

    private sealed record Image(uint[] Pixels, int Width, int Height)
    {
        public uint At(int x, int y) => Pixels[y * Width + x];
    }

    private static Image Render(double dpi) => OnStaThread(() =>
    {
        var control = Build();

        var size = new Size(12 * Pitch - Gap, 7 * Pitch - Gap);
        control.Measure(size);
        control.Arrange(new Rect(size));
        control.UpdateLayout();

        var scale = dpi / 96d;
        var width = (int)Math.Round(size.Width * scale);
        var height = (int)Math.Round(size.Height * scale);

        var target = new RenderTargetBitmap(width, height, dpi, dpi, PixelFormats.Pbgra32);
        target.Render(control);

        var pixels = new uint[width * height];
        target.CopyPixels(pixels, width * 4, 0);

        return new Image(pixels, width, height);
    });

    private static JourneyHeatmapControl Build()
    {
        // One colour against nothing. Empty squares are painted transparent so they are
        // indistinguishable from the gaps, which leaves a scanline holding exactly two values:
        // inside a filled square, or not. Any third value is a blended edge.
        var nothing = Brushes.Transparent;
        var full = new SolidColorBrush(Colors.White);
        full.Freeze();

        var activity = new Dictionary<DateOnly, DayActivity>();
        var monday = Today.AddDays(-StreakCalculator.MondayIndex(Today.DayOfWeek));

        // Fill alternate columns solid, so both the squares and the gaps are represented and
        // every edge in the grid is a hard transition between the two.
        for (var week = 0; week < 12; week += 2)
        {
            for (var row = 0; row < 7; row++)
            {
                var date = monday.AddDays(-7 * 11 + week * 7 + row);
                activity[date] = new DayActivity(date, 9, 0, 0, 0, 0);
            }
        }

        return new JourneyHeatmapControl
        {
            Cells = StreakCalculator.BuildHeatmap(activity, Today),
            CellSize = Cell,
            Gap = Gap,

            Level0Brush = nothing,
            Level1Brush = full,
            Level2Brush = full,
            Level3Brush = full,
            Level4Brush = full,

            // The outlines would add a third value of their own, and they are not what is
            // being measured here.
            TodayRingBrush = nothing,
            FocusRingBrush = nothing,
            LabelBrush = nothing
        };
    }

    /// <summary>WPF visuals need a single-threaded apartment; xUnit does not provide one.</summary>
    private static T OnStaThread<T>(Func<T> work)
    {
        T result = default!;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException("The render failed: " + failure);
        }

        return result;
    }
}
