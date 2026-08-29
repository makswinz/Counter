using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Counter.App.Controls;

/// <summary>
/// A button whose whole content is one icon.
///
/// The icon is a typed property rather than a geometry stuffed into Tag. That is not tidiness:
/// a Tag holds anything, so a view could put a brush, a string or the wrong geometry in it and
/// the button would render nothing with no error anywhere. <see cref="IconKind"/> is generated
/// from the bundled asset list, so an icon that is not in the family will not compile.
///
/// Sizes come from the style, not from here. Normal is a 28 x 28 hit target around a 16 x 16
/// icon; compact is 24 x 24. Every visual state - hover, pressed, selected, keyboard focus,
/// disabled - lives in one template in Theme/Controls.xaml, so no two icon buttons anywhere can
/// disagree about what pressed looks like.
/// </summary>
public class IconButton : Button
{
    static IconButton()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(IconButton), new FrameworkPropertyMetadata(typeof(IconButton)));
    }

    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(IconKind), typeof(IconButton),
        new FrameworkPropertyMetadata(IconKind.None));

    public static readonly DependencyProperty IconVariantProperty = DependencyProperty.Register(
        nameof(IconVariant), typeof(IconVariant), typeof(IconButton),
        new FrameworkPropertyMetadata(Controls.IconVariant.Regular));

    /// <summary>The rendered icon edge. The hit target is set by the style and is larger.</summary>
    public static readonly DependencyProperty IconSizeProperty = DependencyProperty.Register(
        nameof(IconSize), typeof(double), typeof(IconButton),
        new FrameworkPropertyMetadata(16d));

    /// <summary>
    /// Marks the button as the current destination, which tints its background and its icon with
    /// the accent. Statistics and Settings each own one of these, and selecting either clears
    /// the other, so the header always shows exactly where you are.
    /// </summary>
    public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.Register(
        nameof(IsSelected), typeof(bool), typeof(IconButton),
        new FrameworkPropertyMetadata(false));

    public IconKind Icon
    {
        get => (IconKind)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public IconVariant IconVariant
    {
        get => (IconVariant)GetValue(IconVariantProperty);
        set => SetValue(IconVariantProperty, value);
    }

    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    public bool IsSelected
    {
        get => (bool)GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }
}

/// <summary>
/// The toggle form of <see cref="IconButton"/>, for the pin. Carries a second icon for the
/// checked state so a toggle can change weight - outline to filled - rather than only colour,
/// which is what keeps the state readable without relying on the accent being visible.
/// </summary>
public class IconToggleButton : ToggleButton
{
    static IconToggleButton()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(IconToggleButton), new FrameworkPropertyMetadata(typeof(IconToggleButton)));
    }

    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(IconKind), typeof(IconToggleButton),
        new FrameworkPropertyMetadata(IconKind.None));

    public static readonly DependencyProperty CheckedIconProperty = DependencyProperty.Register(
        nameof(CheckedIcon), typeof(IconKind), typeof(IconToggleButton),
        new FrameworkPropertyMetadata(IconKind.None));

    public static readonly DependencyProperty IconSizeProperty = DependencyProperty.Register(
        nameof(IconSize), typeof(double), typeof(IconToggleButton),
        new FrameworkPropertyMetadata(16d));

    public IconKind Icon
    {
        get => (IconKind)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public IconKind CheckedIcon
    {
        get => (IconKind)GetValue(CheckedIconProperty);
        set => SetValue(CheckedIconProperty, value);
    }

    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }
}

/// <summary>
/// A circle with something centred in it: a calendar date, a count, a status dot.
///
/// It exists because every off-centre badge in the old interface was off-centre for the same
/// reason - the circle and its content were laid out as siblings with padding, so the moment
/// the content changed width the circle moved, and a two-digit date sat differently from a
/// one-digit one. Here the circle and the content occupy the same cell, both centred, and the
/// circle's size comes only from <see cref="Diameter"/>. Text width cannot reach it.
///
/// <see cref="BaselineOffset"/> is the one permitted correction. Digits in most UI faces sit a
/// touch low inside a circle because their optical centre is above the em box centre, and half
/// a pixel up fixes it. It moves the content, never the circle, and it is capped, because
/// anything larger is a layout mistake being papered over.
/// </summary>
public class CircularBadge : ContentControl
{
    /// <summary>The largest baseline correction the control will accept, in pixels.</summary>
    public const double MaximumBaselineOffset = 1.0;

    static CircularBadge()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(CircularBadge), new FrameworkPropertyMetadata(typeof(CircularBadge)));

        // Content is always centred. These are not defaults a view is expected to override.
        HorizontalContentAlignmentProperty.OverrideMetadata(
            typeof(CircularBadge), new FrameworkPropertyMetadata(HorizontalAlignment.Center));

        VerticalContentAlignmentProperty.OverrideMetadata(
            typeof(CircularBadge), new FrameworkPropertyMetadata(VerticalAlignment.Center));
    }

    public CircularBadge()
    {
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
    }

    public static readonly DependencyProperty DiameterProperty = DependencyProperty.Register(
        nameof(Diameter), typeof(double), typeof(CircularBadge),
        new FrameworkPropertyMetadata(
            22d,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(CircularBadge),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(CircularBadge),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(CircularBadge),
        new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BaselineOffsetProperty = DependencyProperty.Register(
        nameof(BaselineOffset), typeof(double), typeof(CircularBadge),
        new FrameworkPropertyMetadata(-0.5), IsWithinBaselineLimit);

    public double Diameter
    {
        get => (double)GetValue(DiameterProperty);
        set => SetValue(DiameterProperty, value);
    }

    public Brush? Fill
    {
        get => (Brush?)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public Brush? Stroke
    {
        get => (Brush?)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public double BaselineOffset
    {
        get => (double)GetValue(BaselineOffsetProperty);
        set => SetValue(BaselineOffsetProperty, value);
    }

    /// <summary>
    /// Always an exact square of <see cref="Diameter"/>. Width and height cannot drift apart,
    /// and no amount of content can stretch either of them.
    /// </summary>
    protected override Size MeasureOverride(Size constraint)
    {
        var edge = Math.Max(0, Diameter);
        base.MeasureOverride(new Size(edge, edge));
        return new Size(edge, edge);
    }

    /// <summary>
    /// The circle is drawn by the control itself rather than placed in its template.
    ///
    /// That is not an optimisation. A circle in a template is a circle a template can get wrong:
    /// give it a margin, an alignment, a width bound to the wrong thing, and the badge is off
    /// centre again. Drawn here it is defined as "a disc of Diameter, centred in the rendered
    /// box", which no amount of retemplating can move. The template positions the content and
    /// nothing else.
    ///
    /// Diameter, not the rendered box. A badge dropped into a cell larger than itself is
    /// arranged to the whole cell unless somebody remembers to centre it, and a badge that
    /// quietly grows to fill its cell is a badge whose size depends on where it was put - which
    /// is the exact class of bug the control exists to make impossible. The disc is the size it
    /// was asked to be, wherever it lands.
    /// </summary>
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var edge = Math.Min(Math.Max(0, Diameter), Math.Min(RenderSize.Width, RenderSize.Height));
        if (edge <= 0)
        {
            return;
        }

        var thickness = Stroke is null ? 0 : Math.Max(0, StrokeThickness);
        var radius = (edge - thickness) / 2;

        if (radius <= 0 || (Fill is null && thickness <= 0))
        {
            return;
        }

        Pen? pen = null;
        if (thickness > 0)
        {
            pen = new Pen(Stroke, thickness);
            pen.Freeze();
        }

        dc.DrawEllipse(
            Fill, pen, new Point(RenderSize.Width / 2, RenderSize.Height / 2), radius, radius);
    }

    private static bool IsWithinBaselineLimit(object value) =>
        value is double offset && !double.IsNaN(offset) && Math.Abs(offset) <= MaximumBaselineOffset;
}

/// <summary>
/// The completed-task control.
///
/// A dedicated control rather than a styled CheckBox, and rather than the plain Button with a
/// data trigger it replaces. The old one drew a ring, a separate focus ring and a tick as three
/// independent siblings, which is exactly how a checkmark ends up a pixel off centre and a blue
/// outline ends up doubled: three elements, three centring rules, three chances to disagree.
///
/// Here there is one 16 x 16 visual with everything inside it, sitting in a transparent 28 x 28
/// hit target. Checked, the fill and the contour are the same accent colour, so there is exactly
/// one contour and no second outline anywhere. The keyboard focus ring is a focus visual, which
/// means Windows shows it after a Tab and not after a mouse click, without the template having
/// to guess which happened.
/// </summary>
public class CompletionCheck : Button
{
    static CompletionCheck()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(CompletionCheck), new FrameworkPropertyMetadata(typeof(CompletionCheck)));
    }

    /// <summary>
    /// Whether the task is done. One-way from the view model on purpose: the click raises the
    /// command, the command decides, and the control follows. A control that ticked itself and
    /// then found the write had failed would be lying.
    /// </summary>
    public static readonly DependencyProperty IsCheckedProperty = DependencyProperty.Register(
        nameof(IsChecked), typeof(bool), typeof(CompletionCheck),
        new FrameworkPropertyMetadata(false));

    public bool IsChecked
    {
        get => (bool)GetValue(IsCheckedProperty);
        set => SetValue(IsCheckedProperty, value);
    }
}
