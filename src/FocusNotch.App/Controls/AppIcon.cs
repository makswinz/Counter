using System.Windows;
using System.Windows.Automation;
using System.Windows.Documents;
using System.Windows.Media;

namespace FocusNotch.App.Controls;

/// <summary>
/// Draws one Fluent icon, and is the only thing in the application that draws an icon at all.
///
/// It is a drawn element rather than a templated control with a Viewbox and a Path. Three
/// reasons, in order of how much they matter:
///
/// 1. <b>One centring rule.</b> The element measures to an exact square of <see cref="IconSize"/>
///    whatever the artwork inside it looks like, and the geometry is scaled from its own source
///    viewBox and centred in that square. A 12 x 12 checkmark and a 20 x 20 chevron therefore sit
///    on the same optical centre with no per-view margin anywhere, which is what stops one icon
///    in a row from looking a pixel low or a pixel left of its neighbours.
///
/// 2. <b>Aspect ratio cannot be lost.</b> There is a single uniform scale factor. There is no
///    Stretch property to set to Fill by accident.
///
/// 3. <b>Cost.</b> One geometry and one drawing instruction per icon, against a Viewbox, a
///    Canvas, a Path and a full layout pass per icon. The notch draws around thirty of them.
///
/// Optical corrections live in <see cref="IconCatalog"/>, never in a view.
/// </summary>
public sealed class AppIcon : FrameworkElement
{
    static AppIcon()
    {
        // Icons are decorative: the button around them carries the name and the tooltip.
        IsHitTestVisibleProperty.OverrideMetadata(
            typeof(AppIcon), new FrameworkPropertyMetadata(false));
    }

    public AppIcon()
    {
        // The host lands on a whole device pixel, so a 16 px icon is drawn into a 16 px box
        // starting at an integer offset rather than straddling two.
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
    }

    // ==================================================================== properties

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(IconKind), typeof(AppIcon),
        new FrameworkPropertyMetadata(IconKind.None, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty VariantProperty = DependencyProperty.Register(
        nameof(Variant), typeof(IconVariant), typeof(AppIcon),
        new FrameworkPropertyMetadata(IconVariant.Regular, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The rendered edge of the square host, in device-independent pixels.</summary>
    public static readonly DependencyProperty IconSizeProperty = DependencyProperty.Register(
        nameof(IconSize), typeof(double), typeof(AppIcon),
        new FrameworkPropertyMetadata(
            16d,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Inherited from the control around it, so an icon never carries its own colour.</summary>
    public static readonly DependencyProperty ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner(
            typeof(AppIcon),
            new FrameworkPropertyMetadata(
                SystemColors.ControlTextBrush,
                FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>A per-use horizontal nudge, on top of the catalog's own. Clamped to one pixel.</summary>
    public static readonly DependencyProperty OpticalOffsetXProperty = DependencyProperty.Register(
        nameof(OpticalOffsetX), typeof(double), typeof(AppIcon),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender),
        IsWithinOpticalLimit);

    /// <summary>A per-use vertical nudge, on top of the catalog's own. Clamped to one pixel.</summary>
    public static readonly DependencyProperty OpticalOffsetYProperty = DependencyProperty.Register(
        nameof(OpticalOffsetY), typeof(double), typeof(AppIcon),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender),
        IsWithinOpticalLimit);

    /// <summary>
    /// Set only where the icon is the whole meaning of a control and nothing around it has a
    /// name. Everywhere else the button owns the accessible name and the icon stays silent.
    /// </summary>
    public static readonly DependencyProperty AutomationNameProperty = DependencyProperty.Register(
        nameof(AutomationName), typeof(string), typeof(AppIcon),
        new FrameworkPropertyMetadata(null, OnAutomationNameChanged));

    public IconKind Kind
    {
        get => (IconKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public IconVariant Variant
    {
        get => (IconVariant)GetValue(VariantProperty);
        set => SetValue(VariantProperty, value);
    }

    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    public Brush? Foreground
    {
        get => (Brush?)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public double OpticalOffsetX
    {
        get => (double)GetValue(OpticalOffsetXProperty);
        set => SetValue(OpticalOffsetXProperty, value);
    }

    public double OpticalOffsetY
    {
        get => (double)GetValue(OpticalOffsetYProperty);
        set => SetValue(OpticalOffsetYProperty, value);
    }

    public string? AutomationName
    {
        get => (string?)GetValue(AutomationNameProperty);
        set => SetValue(AutomationNameProperty, value);
    }

    // ==================================================================== layout and drawing

    /// <summary>
    /// Always an exact square, whatever the artwork's ink extents are and whatever space the
    /// parent offers. This is the fixed host every icon is centred inside.
    /// </summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        var edge = Math.Max(0, IconSize);
        return new Size(edge, edge);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var brush = Foreground;
        if (brush is null || IconSize <= 0)
        {
            return;
        }

        var glyph = IconCatalog.Resolve(Kind, Variant);
        if (glyph is not { } entry)
        {
            return;
        }

        var geometry = IconCatalog.Geometry(entry.ResourceKey);
        if (geometry is null)
        {
            return;
        }

        var scale = IconSize / entry.ViewboxSize;

        // Centred against the box actually rendered rather than against IconSize, so an icon
        // stays centred even if a parent hands it more room than it asked for.
        var catalogOffset = IconCatalog.OpticalOffset(Kind, Variant);
        var x = ((RenderSize.Width - IconSize) / 2) + catalogOffset.X + OpticalOffsetX;
        var y = ((RenderSize.Height - IconSize) / 2) + catalogOffset.Y + OpticalOffsetY;

        dc.PushTransform(new TranslateTransform(x, y));
        dc.PushTransform(new ScaleTransform(scale, scale));
        dc.DrawGeometry(brush, null, geometry);
        dc.Pop();
        dc.Pop();
    }

    // ==================================================================== plumbing

    private static bool IsWithinOpticalLimit(object value) =>
        value is double offset
        && !double.IsNaN(offset)
        && Math.Abs(offset) <= IconCatalog.MaximumOpticalOffset;

    private static void OnAutomationNameChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // A bare FrameworkElement produces no automation peer of its own, so an unnamed icon is
        // already invisible to a screen reader and a named one needs only this.
        AutomationProperties.SetName((AppIcon)d, e.NewValue as string ?? string.Empty);
    }
}
