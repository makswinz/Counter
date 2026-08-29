using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FocusNotch.Core.Streaks;

namespace FocusNotch.App.Controls;

/// <summary>
/// The journey heatmap: twelve weeks by seven rows, Monday at the top, one column per week and
/// the current week last.
///
/// It draws itself rather than hosting eighty-four elements in a stretched grid. That is what
/// makes it crisp: every square is snapped to whole device pixels through a
/// <see cref="GuidelineSet"/>, so at 125 and 150 percent the edges land on the pixel grid
/// instead of straddling it and going soft. It also means a refresh is one render pass rather
/// than eighty-four layout passes, and the control's own size never changes with the data, so
/// nothing it does can resize the panel around it.
///
/// It re-renders when the activity, the theme, the date, the DPI or its own size changes, and
/// at no other time. A running timer never touches it.
/// </summary>
public sealed class JourneyHeatmapControl : FrameworkElement
{
    public static readonly DependencyProperty CellsProperty = DependencyProperty.Register(
        nameof(Cells), typeof(IReadOnlyList<HeatmapCell>), typeof(JourneyHeatmapControl),
        new FrameworkPropertyMetadata(
            null,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
            OnCellsChanged));

    public static readonly DependencyProperty CellSizeProperty = DependencyProperty.Register(
        nameof(CellSize), typeof(double), typeof(JourneyHeatmapControl),
        new FrameworkPropertyMetadata(
            9d,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(double), typeof(JourneyHeatmapControl),
        new FrameworkPropertyMetadata(
            3d,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// Today, so the outline can be drawn on the right square. Passed in rather than read from
    /// the machine clock, so the control shows the same day the rest of the app decided on.
    /// </summary>
    public static readonly DependencyProperty TodayProperty = DependencyProperty.Register(
        nameof(Today), typeof(DateTime), typeof(JourneyHeatmapControl),
        new FrameworkPropertyMetadata(default(DateTime), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowMonthLabelsProperty = DependencyProperty.Register(
        nameof(ShowMonthLabels), typeof(bool), typeof(JourneyHeatmapControl),
        new FrameworkPropertyMetadata(
            false,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// The colours the control paints with, declared rather than looked up.
    ///
    /// A drawn control keeps its drawing until something invalidates it, and a resource changing
    /// underneath it is not something it would notice. Declaring them as dependency properties
    /// and binding them with DynamicResource in XAML means WPF invalidates the render itself the
    /// moment the theme swaps a brush.
    /// </summary>
    public static readonly DependencyProperty Level0BrushProperty = BrushProperty(nameof(Level0Brush));

    public static readonly DependencyProperty Level1BrushProperty = BrushProperty(nameof(Level1Brush));

    public static readonly DependencyProperty Level2BrushProperty = BrushProperty(nameof(Level2Brush));

    public static readonly DependencyProperty Level3BrushProperty = BrushProperty(nameof(Level3Brush));

    public static readonly DependencyProperty Level4BrushProperty = BrushProperty(nameof(Level4Brush));

    public static readonly DependencyProperty TodayRingBrushProperty = BrushProperty(nameof(TodayRingBrush));

    public static readonly DependencyProperty FocusRingBrushProperty = BrushProperty(nameof(FocusRingBrush));

    public static readonly DependencyProperty LabelBrushProperty = BrushProperty(nameof(LabelBrush));

    private static DependencyProperty BrushProperty(string name) => DependencyProperty.Register(
        name, typeof(Brush), typeof(JourneyHeatmapControl),
        new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Corner radius of one square. Two pixels reads as a square with softened corners.</summary>
    private const double Radius = 2;

    private const int Rows = 7;
    private const double MonthLabelHeight = 13;

    private int _hoverIndex = -1;
    private int _focusIndex = -1;

    public JourneyHeatmapControl()
    {
        Focusable = true;
        FocusVisualStyle = null;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;

        ToolTipService.SetInitialShowDelay(this, 250);
        ToolTipService.SetShowDuration(this, 20000);
        AutomationProperties.SetName(this, "Journey heatmap");
    }

    public IReadOnlyList<HeatmapCell>? Cells
    {
        get => (IReadOnlyList<HeatmapCell>?)GetValue(CellsProperty);
        set => SetValue(CellsProperty, value);
    }

    public double CellSize
    {
        get => (double)GetValue(CellSizeProperty);
        set => SetValue(CellSizeProperty, value);
    }

    public double Gap
    {
        get => (double)GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    public Brush Level0Brush
    {
        get => (Brush)GetValue(Level0BrushProperty);
        set => SetValue(Level0BrushProperty, value);
    }

    public Brush Level1Brush
    {
        get => (Brush)GetValue(Level1BrushProperty);
        set => SetValue(Level1BrushProperty, value);
    }

    public Brush Level2Brush
    {
        get => (Brush)GetValue(Level2BrushProperty);
        set => SetValue(Level2BrushProperty, value);
    }

    public Brush Level3Brush
    {
        get => (Brush)GetValue(Level3BrushProperty);
        set => SetValue(Level3BrushProperty, value);
    }

    public Brush Level4Brush
    {
        get => (Brush)GetValue(Level4BrushProperty);
        set => SetValue(Level4BrushProperty, value);
    }

    public Brush TodayRingBrush
    {
        get => (Brush)GetValue(TodayRingBrushProperty);
        set => SetValue(TodayRingBrushProperty, value);
    }

    public Brush FocusRingBrush
    {
        get => (Brush)GetValue(FocusRingBrushProperty);
        set => SetValue(FocusRingBrushProperty, value);
    }

    public Brush LabelBrush
    {
        get => (Brush)GetValue(LabelBrushProperty);
        set => SetValue(LabelBrushProperty, value);
    }

    public DateTime Today
    {
        get => (DateTime)GetValue(TodayProperty);
        set => SetValue(TodayProperty, value);
    }

    /// <summary>Month initials above the grid. Only worth the room in the larger view.</summary>
    public bool ShowMonthLabels
    {
        get => (bool)GetValue(ShowMonthLabelsProperty);
        set => SetValue(ShowMonthLabelsProperty, value);
    }

    /// <summary>How many week columns the current data holds. Twelve unless told otherwise.</summary>
    private int Weeks
    {
        get
        {
            var cells = Cells;
            if (cells is null || cells.Count == 0)
            {
                return StreakCalculator.DefaultWeeks;
            }

            var max = 0;
            foreach (var cell in cells)
            {
                if (cell.Week > max)
                {
                    max = cell.Week;
                }
            }

            return max + 1;
        }
    }

    private double Pitch => CellSize + Gap;

    private double GridTop => ShowMonthLabels ? MonthLabelHeight : 0;

    protected override Size MeasureOverride(Size availableSize)
    {
        var weeks = Weeks;
        var width = weeks * Pitch - Gap;
        var height = Rows * Pitch - Gap + GridTop;

        return new Size(Math.Max(0, width), Math.Max(0, height));
    }

    protected override void OnRender(DrawingContext dc)
    {
        var cells = Cells;
        if (cells is null || cells.Count == 0)
        {
            return;
        }

        var size = CellSize;
        var pitch = Pitch;
        var top = GridTop;

        var levels = new[] { Level0Brush, Level1Brush, Level2Brush, Level3Brush, Level4Brush };

        var todayPen = new Pen(TodayRingBrush, 1);
        var focusPen = new Pen(FocusRingBrush, 1);
        todayPen.Freeze();
        focusPen.Freeze();

        // One guideline set for the whole grid. Snapping every edge to the device pixel grid is
        // the difference between crisp squares and blurred ones at 125 and 150 percent.
        var dpi = VisualTreeHelper.GetDpi(this);
        var guidelines = new GuidelineSet();

        for (var week = 0; week <= Weeks; week++)
        {
            guidelines.GuidelinesX.Add(Snap(week * pitch, dpi.DpiScaleX));
            guidelines.GuidelinesX.Add(Snap(week * pitch + size, dpi.DpiScaleX));
        }

        for (var row = 0; row <= Rows; row++)
        {
            guidelines.GuidelinesY.Add(Snap(top + row * pitch, dpi.DpiScaleY));
            guidelines.GuidelinesY.Add(Snap(top + row * pitch + size, dpi.DpiScaleY));
        }

        guidelines.Freeze();
        dc.PushGuidelineSet(guidelines);

        if (ShowMonthLabels)
        {
            DrawMonthLabels(dc, cells, pitch);
        }

        for (var i = 0; i < cells.Count; i++)
        {
            var cell = cells[i];
            var rect = RectFor(cell.Week, cell.Row);

            var fill = levels[Math.Clamp(cell.Intensity, 0, 4)];

            if (cell.IsFuture)
            {
                // A day that has not happened is drawn as the empty step and left flat: it is
                // unavailable, not merely quiet, and must never look like a contribution.
                dc.DrawRoundedRectangle(levels[0], null, rect, Radius, Radius);
                continue;
            }

            dc.DrawRoundedRectangle(fill, null, rect, Radius, Radius);

            if (i == _hoverIndex)
            {
                dc.DrawRoundedRectangle(null, todayPen, Inset(rect), Radius, Radius);
            }
        }

        // Today's outline is drawn last so nothing can paint over it.
        for (var i = 0; i < cells.Count; i++)
        {
            if (!IsToday(cells[i]))
            {
                continue;
            }

            dc.DrawRoundedRectangle(null, todayPen, Inset(RectFor(cells[i].Week, cells[i].Row)), Radius, Radius);
        }

        if (IsKeyboardFocused && _focusIndex >= 0 && _focusIndex < cells.Count)
        {
            var cell = cells[_focusIndex];
            var rect = RectFor(cell.Week, cell.Row);
            rect.Inflate(1.5, 1.5);
            dc.DrawRoundedRectangle(null, focusPen, rect, Radius + 1, Radius + 1);
        }

        dc.Pop();
    }

    private void DrawMonthLabels(DrawingContext dc, IReadOnlyList<HeatmapCell> cells, double pitch)
    {
        var brush = LabelBrush;
        var typeface = new Typeface(
            TryFindResource("UiFont") as FontFamily ?? new FontFamily("Segoe UI"),
            FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var lastMonth = -1;

        // One label per column at most, and only where the month actually turns over, so the
        // labels never collide however narrow the squares are.
        foreach (var cell in cells.Where(c => c.Row == 0).OrderBy(c => c.Week))
        {
            if (cell.Date.Month == lastMonth)
            {
                continue;
            }

            lastMonth = cell.Date.Month;

            var text = new FormattedText(
                cell.Date.ToString("MMM", CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                typeface,
                9,
                brush,
                dpi);

            var x = cell.Week * pitch;
            if (x + text.Width > ActualWidth)
            {
                continue;
            }

            dc.DrawText(text, new Point(x, 0));
        }
    }

    private Rect RectFor(int week, int row) =>
        new(week * Pitch, GridTop + row * Pitch, CellSize, CellSize);

    private static Rect Inset(Rect rect)
    {
        // Half a pixel in, so a one-pixel stroke sits inside the square instead of straddling
        // its edge and painting half of itself onto the gap.
        var inset = rect;
        inset.Inflate(-0.5, -0.5);
        return inset;
    }

    private static double Snap(double value, double scale) => Math.Round(value * scale) / scale;

    private bool IsToday(HeatmapCell cell) =>
        Today != default && cell.Date == DateOnly.FromDateTime(Today);

    // =================================================================================
    // Pointer and keyboard
    // =================================================================================

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        SetHover(IndexAt(e.GetPosition(this)));
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        SetHover(-1);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        var index = IndexAt(e.GetPosition(this));
        if (index >= 0)
        {
            _focusIndex = index;
            Focus();
            InvalidateVisual();
        }
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);

        if (_focusIndex < 0)
        {
            // Land on today, which is the square anybody arriving by keyboard means.
            var cells = Cells;
            _focusIndex = cells is null ? -1 : cells.Count - 1 - CountTrailingFuture(cells);
        }

        Describe(_focusIndex);
        InvalidateVisual();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        var cells = Cells;
        if (cells is null || cells.Count == 0 || _focusIndex < 0)
        {
            return;
        }

        var current = cells[_focusIndex];
        var week = current.Week;
        var row = current.Row;

        switch (e.Key)
        {
            case Key.Left: week--; break;
            case Key.Right: week++; break;
            case Key.Up: row--; break;
            case Key.Down: row++; break;
            default: return;
        }

        var target = cells.ToList().FindIndex(c => c.Week == week && c.Row == row);
        if (target >= 0)
        {
            _focusIndex = target;
            Describe(target);
            InvalidateVisual();
        }

        e.Handled = true;
    }

    private static int CountTrailingFuture(IReadOnlyList<HeatmapCell> cells)
    {
        var count = 0;
        for (var i = cells.Count - 1; i >= 0 && cells[i].IsFuture; i--)
        {
            count++;
        }

        return count;
    }

    private void SetHover(int index)
    {
        if (index == _hoverIndex)
        {
            return;
        }

        _hoverIndex = index;
        Describe(index);
        InvalidateVisual();
    }

    /// <summary>Puts one square's story on the tooltip and on the automation name.</summary>
    private void Describe(int index)
    {
        var cells = Cells;

        if (cells is null || index < 0 || index >= cells.Count)
        {
            ToolTip = null;
            AutomationProperties.SetName(this, "Journey heatmap");
            return;
        }

        ToolTip = cells[index].Tooltip;
        AutomationProperties.SetName(this, cells[index].AccessibleDescription);
    }

    private int IndexAt(Point point)
    {
        var cells = Cells;
        if (cells is null || cells.Count == 0)
        {
            return -1;
        }

        var pitch = Pitch;
        var week = (int)Math.Floor(point.X / pitch);
        var row = (int)Math.Floor((point.Y - GridTop) / pitch);

        if (week < 0 || row is < 0 or >= Rows)
        {
            return -1;
        }

        // Inside the gap between squares counts as nothing, so the tooltip does not flicker
        // between neighbours as the pointer crosses the seam.
        if (point.X - week * pitch > CellSize || point.Y - GridTop - row * pitch > CellSize)
        {
            return -1;
        }

        return cells.ToList().FindIndex(c => c.Week == week && c.Row == row);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new JourneyHeatmapAutomationPeer(this);

    private static void OnCellsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is JourneyHeatmapControl control)
        {
            control._hoverIndex = -1;
            control._focusIndex = -1;
            control.ToolTip = null;
        }
    }

    private sealed class JourneyHeatmapAutomationPeer : FrameworkElementAutomationPeer
    {
        public JourneyHeatmapAutomationPeer(JourneyHeatmapControl owner) : base(owner)
        {
        }

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;

        protected override string GetClassNameCore() => nameof(JourneyHeatmapControl);
    }
}
