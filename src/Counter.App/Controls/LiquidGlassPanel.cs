using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Counter.Core.Models;

namespace Counter.App.Controls;

/// <summary>How much a surface is lifted off the desktop. Three steps, and only three.</summary>
public enum GlassElevation
{
    /// <summary>Flat. No shadow at all: an inner surface, not a floating one.</summary>
    Flush,

    /// <summary>The main tool and its panels.</summary>
    Panel,

    /// <summary>A popover, which floats above a panel and has to read as being in front of it.</summary>
    Popup
}

/// <summary>
/// One piece of liquid glass: shadow, glow, contour, body, tint, reflection, highlight, inner
/// edge, grain, content. In that order, every time.
///
/// The reason this is a control rather than a pattern is that glass is not one property. A
/// convincing pane needs about eight layers stacked in a specific order with specific opacities,
/// and any view that assembles them by hand will get one of them slightly wrong - a reflection
/// under the tint instead of over it, an inner edge outside the clip, a second shadow on a
/// surface that already had one. Written once, the whole interface is guaranteed to be made of
/// the same material.
///
/// What it deliberately does not do: it does not blur anything. See <c>BackdropService</c> for
/// why. The depth here comes from layering and from a real one-pixel contour, which is what
/// keeps the panel legible on a busy wallpaper - a blur alone would not.
/// </summary>
public sealed class LiquidGlassPanel : ContentControl
{
    // ==================================================================== shape

    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.Register(
            nameof(CornerRadius), typeof(CornerRadius), typeof(LiquidGlassPanel),
            new FrameworkPropertyMetadata(new CornerRadius(14), FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The outer radius. The body inside is inset by the contour, and rounds to match.</summary>
    public CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    // ==================================================================== contour

    public static readonly DependencyProperty ContourBrushProperty =
        DependencyProperty.Register(
            nameof(ContourBrush), typeof(Brush), typeof(LiquidGlassPanel),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// The structural edge. Neutral by default; an active panel overlays the accent contour on
    /// top of it rather than replacing it, so the tool never loses its outline mid-crossfade.
    /// </summary>
    public Brush? ContourBrush
    {
        get => (Brush?)GetValue(ContourBrushProperty);
        set => SetValue(ContourBrushProperty, value);
    }

    public static readonly DependencyProperty AccentContourBrushProperty =
        DependencyProperty.Register(
            nameof(AccentContourBrush), typeof(Brush), typeof(LiquidGlassPanel),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The coloured ring laid over the structural one. Its strength is the state.</summary>
    public Brush? AccentContourBrush
    {
        get => (Brush?)GetValue(AccentContourBrushProperty);
        set => SetValue(AccentContourBrushProperty, value);
    }

    public static readonly DependencyProperty ContourOpacityProperty =
        DependencyProperty.Register(
            nameof(ContourOpacity), typeof(double), typeof(LiquidGlassPanel),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// How strongly the accent ring shows. Idle sits near four tenths, hovered near six, running
    /// near one: enough of a difference to read as a state change, never enough to look like a
    /// different control.
    /// </summary>
    public double ContourOpacity
    {
        get => (double)GetValue(ContourOpacityProperty);
        set => SetValue(ContourOpacityProperty, value);
    }

    // ==================================================================== glow

    public static readonly DependencyProperty GlowBrushProperty =
        DependencyProperty.Register(
            nameof(GlowBrush), typeof(Brush), typeof(LiquidGlassPanel),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush? GlowBrush
    {
        get => (Brush?)GetValue(GlowBrushProperty);
        set => SetValue(GlowBrushProperty, value);
    }

    public static readonly DependencyProperty GlowOpacityProperty =
        DependencyProperty.Register(
            nameof(GlowOpacity), typeof(double), typeof(LiquidGlassPanel),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Capped at twelve percent by the design, and never animated as a blur radius.</summary>
    public double GlowOpacity
    {
        get => (double)GetValue(GlowOpacityProperty);
        set => SetValue(GlowOpacityProperty, value);
    }

    // ==================================================================== body

    public static readonly DependencyProperty GlassBrushProperty =
        DependencyProperty.Register(
            nameof(GlassBrush), typeof(Brush), typeof(LiquidGlassPanel),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Which of the glass surfaces this panel is made of. Base, raised, deep or hover.</summary>
    public Brush? GlassBrush
    {
        get => (Brush?)GetValue(GlassBrushProperty);
        set => SetValue(GlassBrushProperty, value);
    }

    public static readonly DependencyProperty GlassOpacityProperty =
        DependencyProperty.Register(
            nameof(GlassOpacity), typeof(double), typeof(LiquidGlassPanel),
            new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Thins the whole body without touching the contour, for a panel mid-transition.</summary>
    public double GlassOpacity
    {
        get => (double)GetValue(GlassOpacityProperty);
        set => SetValue(GlassOpacityProperty, value);
    }

    // ==================================================================== light

    public static readonly DependencyProperty ShowAccentProperty =
        DependencyProperty.Register(
            nameof(ShowAccent), typeof(bool), typeof(LiquidGlassPanel),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// Whether the glass picks up the warm reflection of something active nearby. Off by default,
    /// because a coloured haze behind every panel is exactly the mistake the design forbids.
    /// </summary>
    public bool ShowAccent
    {
        get => (bool)GetValue(ShowAccentProperty);
        set => SetValue(ShowAccentProperty, value);
    }

    public static readonly DependencyProperty AccentReflectionOpacityProperty =
        DependencyProperty.Register(
            nameof(AccentReflectionOpacity), typeof(double), typeof(LiquidGlassPanel),
            new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Fades the reflection in and out with the state, without rebuilding a brush.</summary>
    public double AccentReflectionOpacity
    {
        get => (double)GetValue(AccentReflectionOpacityProperty);
        set => SetValue(AccentReflectionOpacityProperty, value);
    }

    public static readonly DependencyProperty ShowTopHighlightProperty =
        DependencyProperty.Register(
            nameof(ShowTopHighlight), typeof(bool), typeof(LiquidGlassPanel),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The specular sheen across the upper left. On for a panel, off for an inner card.</summary>
    public bool ShowTopHighlight
    {
        get => (bool)GetValue(ShowTopHighlightProperty);
        set => SetValue(ShowTopHighlightProperty, value);
    }

    // ==================================================================== elevation

    public static readonly DependencyProperty ElevationProperty =
        DependencyProperty.Register(
            nameof(Elevation), typeof(GlassElevation), typeof(LiquidGlassPanel),
            new FrameworkPropertyMetadata(GlassElevation.Panel, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// How far off the desktop the surface sits. There is exactly one shadow per glass panel and
    /// this chooses its weight; nothing inside a panel gets a shadow of its own.
    /// </summary>
    public GlassElevation Elevation
    {
        get => (GlassElevation)GetValue(ElevationProperty);
        set => SetValue(ElevationProperty, value);
    }

    // ==================================================================== material

    public static readonly DependencyProperty MaterialProperty =
        DependencyProperty.Register(
            nameof(Material), typeof(GlassMaterial), typeof(LiquidGlassPanel),
            new FrameworkPropertyMetadata(GlassMaterial.Solid, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// Which of the three glasses this panel is made of.
    ///
    /// Every panel takes it from one dictionary entry the theme writes, so the choice is made
    /// once for the whole interface rather than surface by surface. It is on the control rather
    /// than read from the dictionary directly so that a template trigger can switch the layers,
    /// and so that a single panel can be pinned to one material when a test needs it to be.
    /// </summary>
    public GlassMaterial Material
    {
        get => (GlassMaterial)GetValue(MaterialProperty);
        set => SetValue(MaterialProperty, value);
    }

    // ==================================================================== backdrop

    public static readonly DependencyProperty HasBackdropProperty =
        DependencyProperty.Register(
            nameof(HasBackdrop), typeof(bool), typeof(LiquidGlassPanel),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// Whether a compositor backdrop is currently blurring underneath this panel.
    ///
    /// It exists to settle an argument about shadows. Asking DWM to round the backdrop window is
    /// the only thing on Windows 11 that will clip an acrylic blur to a curve, and DWM will not
    /// round a window without also casting a shadow around it. So when the backdrop is there the
    /// panel already has a shadow, drawn by the compositor, underneath the one it draws itself -
    /// and two shadows around one edge is the dark halo that makes a translucent panel look like
    /// it is leaking rather than floating. The panel gives up its own while that is true.
    /// </summary>
    public bool HasBackdrop
    {
        get => (bool)GetValue(HasBackdropProperty);
        set => SetValue(HasBackdropProperty, value);
    }

    static LiquidGlassPanel()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(LiquidGlassPanel), new FrameworkPropertyMetadata(typeof(LiquidGlassPanel)));
    }
}
