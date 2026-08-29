using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FocusNotch.Core.Focus;
using FocusNotch.Core.Statistics;

namespace FocusNotch.App.Controls;

/// <summary>
/// The daily activity chart: solid bars, light grid lines, compact axis labels.
///
/// Like the heatmap it draws itself and snaps every edge to the device pixel grid, so the bars
/// and the baseline stay crisp at every scaling factor instead of going soft. Its height is
/// fixed by the layout, never by the data, so loading statistics cannot resize the panel.
/// Timer time and hand-entered time are drawn as two tones of the same hue rather than as a
/// gradient, so they can be told apart without any colour being invented.
/// </summary>
public sealed class ActivityChartControl : FrameworkElement
{
    public static readonly DependencyProperty BucketsProperty = DependencyProperty.Register(
        nameof(Buckets), typeof(IReadOnlyList<StatBucket>), typeof(ActivityChartControl),
        new FrameworkPropertyMetadata(
            null,
            FrameworkPropertyMetadataOptions.AffectsRender,
            (d, _) => ((ActivityChartControl)d).Describe(-1)));

    /// <summary>
    /// The colours the chart paints with. Declared rather than looked up, for the same reason
    /// the heatmap declares its own: a drawn control keeps its drawing until something
    /// invalidates it, and a resource changing underneath it would never reach the screen.
    /// </summary>
    public static readonly DependencyProperty BarBrushProperty = BrushProperty(nameof(BarBrush));

    public static readonly DependencyProperty ManualBarBrushProperty = BrushProperty(nameof(ManualBarBrush));

    public static readonly DependencyProperty HoverBarBrushProperty = BrushProperty(nameof(HoverBarBrush));

    public static readonly DependencyProperty GridBrushProperty = BrushProperty(nameof(GridBrush));

    public static readonly DependencyProperty EmptyBrushProperty = BrushProperty(nameof(EmptyBrush));

    public static readonly DependencyProperty LabelBrushProperty = BrushProperty(nameof(LabelBrush));

    private static DependencyProperty BrushProperty(string name) => DependencyProperty.Register(
        name, typeof(Brush), typeof(ActivityChartControl),
        new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Room under the bars for the axis labels.</summary>
    private const double AxisHeight = 14;

    private const double MinBarWidth = 2;
    private const double BarGap = 2;

    private int _hoverIndex = -1;

    public ActivityChartControl()
    {
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        ToolTipService.SetInitialShowDelay(this, 200);
        AutomationProperties.SetName(this, "Daily focus time");
    }

    public IReadOnlyList<StatBucket>? Buckets
    {
        get => (IReadOnlyList<StatBucket>?)GetValue(BucketsProperty);
        set => SetValue(BucketsProperty, value);
    }
    public Brush BarBrush
    {
        get => (Brush)GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    public Brush ManualBarBrush
    {
        get => (Brush)GetValue(ManualBarBrushProperty);
        set => SetValue(ManualBarBrushProperty, value);
    }

    public Brush HoverBarBrush
    {
        get => (Brush)GetValue(HoverBarBrushProperty);
        set => SetValue(HoverBarBrushProperty, value);
    }

    public Brush GridBrush
    {
        get => (Brush)GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    public Brush EmptyBrush
    {
        get => (Brush)GetValue(EmptyBrushProperty);
        set => SetValue(EmptyBrushProperty, value);
    }

    public Brush LabelBrush
    {
        get => (Brush)GetValue(LabelBrushProperty);
        set => SetValue(LabelBrushProperty, value);
    }


    protected override void OnRender(DrawingContext dc)
    {
        var buckets = Buckets;
        var width = ActualWidth;
        var height = ActualHeight;

        if (width <= 0 || height <= AxisHeight)
        {
            return;
        }

        var plotHeight = height - AxisHeight;
        var grid = new Pen(GridBrush, 1);
        grid.Freeze();

        var dpi = VisualTreeHelper.GetDpi(this);
        var guidelines = new GuidelineSet();
        guidelines.GuidelinesY.Add(Snap(plotHeight, dpi.DpiScaleY));
        guidelines.Freeze();
        dc.PushGuidelineSet(guidelines);

        // Three horizontal rules plus the baseline. Light, behind everything, never in front.
        for (var i = 1; i <= 3; i++)
        {
            var y = Snap(plotHeight - plotHeight * i / 4d, dpi.DpiScaleY) + 0.5;
            dc.DrawLine(grid, new Point(0, y), new Point(width, y));
        }

        var baseline = Snap(plotHeight, dpi.DpiScaleY) + 0.5;
        dc.DrawLine(grid, new Point(0, baseline), new Point(width, baseline));

        if (buckets is null || buckets.Count == 0)
        {
            dc.Pop();
            DrawEmpty(dc, width, plotHeight);
            return;
        }

        long peak = 0;
        foreach (var bucket in buckets)
        {
            if (bucket.TotalSeconds > peak)
            {
                peak = bucket.TotalSeconds;
            }
        }

        if (peak <= 0)
        {
            // Buckets with nothing in them are still buckets. Saying so is better than drawing
            // an axis of flat stubs and leaving somebody to work out whether it failed to load.
            dc.Pop();
            DrawEmpty(dc, width, plotHeight);
            return;
        }

        var slot = width / buckets.Count;
        var barWidth = Math.Max(MinBarWidth, slot - BarGap);

        var focusBrush = BarBrush;
        var manualBrush = ManualBarBrush;
        var emptyBrush = EmptyBrush;
        var hoverBrush = HoverBarBrush;

        var typeface = new Typeface(
            TryFindResource("UiFont") as FontFamily ?? new FontFamily("Segoe UI"),
            FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var labelBrush = LabelBrush;

        for (var i = 0; i < buckets.Count; i++)
        {
            var bucket = buckets[i];
            var left = Snap(i * slot + (slot - barWidth) / 2, dpi.DpiScaleX);

            if (peak <= 0 || bucket.TotalSeconds <= 0)
            {
                // An empty period still gets a hairline stub, so the axis reads as a series of
                // days rather than as a gap where the chart failed to load.
                var stub = new Rect(left, plotHeight - 1, barWidth, 1);
                dc.DrawRectangle(emptyBrush, null, stub);
            }
            else
            {
                var total = Snap(plotHeight * bucket.TotalSeconds / peak, dpi.DpiScaleY);
                var manual = Snap(plotHeight * bucket.ManualSeconds / (double)peak, dpi.DpiScaleY);
                var focus = Math.Max(0, total - manual);

                if (focus > 0)
                {
                    dc.DrawRectangle(
                        i == _hoverIndex ? hoverBrush : focusBrush, null,
                        new Rect(left, plotHeight - total, barWidth, focus));
                }

                if (manual > 0)
                {
                    dc.DrawRectangle(
                        manualBrush, null,
                        new Rect(left, plotHeight - manual, barWidth, manual));
                }
            }

            if (bucket.Label.Length > 0)
            {
                var text = new FormattedText(
                    bucket.Label,
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    9,
                    labelBrush,
                    dpi.PixelsPerDip);

                var x = i * slot + (slot - text.Width) / 2;
                if (x >= 0 && x + text.Width <= width)
                {
                    dc.DrawText(text, new Point(Snap(x, dpi.DpiScaleX), plotHeight + 2));
                }
            }
        }

        dc.Pop();
    }

    private void DrawEmpty(DrawingContext dc, double width, double plotHeight)
    {
        var typeface = new Typeface(
            TryFindResource("UiFont") as FontFamily ?? new FontFamily("Segoe UI"),
            FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        var text = new FormattedText(
            "No focus time recorded yet",
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface,
            10.5,
            LabelBrush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        dc.DrawText(text, new Point((width - text.Width) / 2, plotHeight / 2 - text.Height / 2));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        var buckets = Buckets;
        if (buckets is null || buckets.Count == 0 || ActualWidth <= 0)
        {
            return;
        }

        var index = (int)Math.Floor(e.GetPosition(this).X / (ActualWidth / buckets.Count));
        Describe(index >= 0 && index < buckets.Count ? index : -1);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        Describe(-1);
    }

    private void Describe(int index)
    {
        if (index == _hoverIndex)
        {
            return;
        }

        _hoverIndex = index;

        var buckets = Buckets;
        if (buckets is null || index < 0 || index >= buckets.Count)
        {
            ToolTip = null;
            InvalidateVisual();
            return;
        }

        var bucket = buckets[index];
        var text = bucket.AccessibleLabel + "\n" + TimeFormat.Spent(bucket.FocusSeconds) + " focused";

        if (bucket.ManualSeconds > 0)
        {
            text += "\n" + TimeFormat.Spent(bucket.ManualSeconds) + " manually added";
        }

        ToolTip = text;
        InvalidateVisual();
    }

    private static double Snap(double value, double scale) => Math.Round(value * scale) / scale;
}
