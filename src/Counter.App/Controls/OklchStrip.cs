using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Input;
using System.Windows.Media;
using Counter.App.Theme;
using Counter.Core.Colour;

namespace Counter.App.Controls;

/// <summary>Which of the three coordinates a strip lets you move.</summary>
public enum OklchAxis
{
    /// <summary>How light the colour is. The engine's own usable band, and no wider.</summary>
    Lightness,

    /// <summary>How much colour there is in it. Grey at one end, as vivid as sRGB allows at the other.</summary>
    Chroma,

    /// <summary>Which colour it is. The full circle, so the two ends meet.</summary>
    Hue
}

/// <summary>
/// One axis of a colour, as a strip you drag.
///
/// The strip paints itself by walking its own axis and asking the same colour space the accent
/// engine uses what each position actually looks like, so the gradient under the thumb is never
/// an approximation of the result - it is the result. That matters more than it sounds: an HSV
/// picker drawn in HSV promises a smooth sweep and delivers a band of neon and a band of mud,
/// because HSV's "brightness" is not brightness. In OKLCH a step along the strip is the same
/// perceptual step wherever you take it, which is the whole reason the engine works in it.
///
/// The two coordinates a strip is not responsible for are inputs to it, so the hue strip
/// restates itself at the lightness you have chosen and the lightness strip restates itself in
/// your hue. Nothing shows you a colour you cannot have.
///
/// Every reachable position is a legal accent. The lightness range is exactly the band the
/// engine will accept without clamping, and chroma beyond what sRGB can show is mapped back
/// down by the same bisection every derived stop goes through, so dragging to the end gives the
/// most colour the display actually has rather than a value that silently becomes something
/// else on its way to the screen.
/// </summary>
public sealed class OklchStrip : FrameworkElement
{
    /// <summary>How many samples the track gradient is built from. Smooth well past a pixel.</summary>
    private const int Samples = 32;

    private const double ThumbRadius = 7.0;

    public static readonly DependencyProperty AxisProperty =
        DependencyProperty.Register(
            nameof(Axis), typeof(OklchAxis), typeof(OklchStrip),
            new FrameworkPropertyMetadata(OklchAxis.Hue, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Which coordinate this strip moves. The other two are held.</summary>
    public OklchAxis Axis
    {
        get => (OklchAxis)GetValue(AxisProperty);
        set => SetValue(AxisProperty, value);
    }

    public static readonly DependencyProperty LightnessProperty =
        DependencyProperty.Register(
            nameof(Lightness), typeof(double), typeof(OklchStrip),
            new FrameworkPropertyMetadata(
                0.60,
                FrameworkPropertyMetadataOptions.AffectsRender
                | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public double Lightness
    {
        get => (double)GetValue(LightnessProperty);
        set => SetValue(LightnessProperty, value);
    }

    public static readonly DependencyProperty ChromaProperty =
        DependencyProperty.Register(
            nameof(Chroma), typeof(double), typeof(OklchStrip),
            new FrameworkPropertyMetadata(
                0.14,
                FrameworkPropertyMetadataOptions.AffectsRender
                | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public double Chroma
    {
        get => (double)GetValue(ChromaProperty);
        set => SetValue(ChromaProperty, value);
    }

    /// <summary>The hue in degrees, because nobody thinks in radians. Converted at the edge.</summary>
    public static readonly DependencyProperty HueProperty =
        DependencyProperty.Register(
            nameof(Hue), typeof(double), typeof(OklchStrip),
            new FrameworkPropertyMetadata(
                260.0,
                FrameworkPropertyMetadataOptions.AffectsRender
                | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public double Hue
    {
        get => (double)GetValue(HueProperty);
        set => SetValue(HueProperty, value);
    }

    public static readonly DependencyProperty ContourBrushProperty =
        DependencyProperty.Register(
            nameof(ContourBrush), typeof(Brush), typeof(OklchStrip),
            new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The strip's own edge, so it reads as a control on any surface.</summary>
    public Brush ContourBrush
    {
        get => (Brush)GetValue(ContourBrushProperty);
        set => SetValue(ContourBrushProperty, value);
    }

    public static readonly DependencyProperty CommitCommandProperty =
        DependencyProperty.Register(
            nameof(CommitCommand), typeof(ICommand), typeof(OklchStrip),
            new FrameworkPropertyMetadata(null));

    /// <summary>
    /// Raised when a drag ends, not while it runs.
    ///
    /// The colour is applied continuously so the whole interface moves under the thumb, but
    /// applying is not storing: writing the setting on every mouse-move would put a few hundred
    /// rows through the database to answer one question. This is the question being answered.
    /// </summary>
    public ICommand? CommitCommand
    {
        get => (ICommand?)GetValue(CommitCommandProperty);
        set => SetValue(CommitCommandProperty, value);
    }

    // ==================================================================== the axis

    /// <summary>The lowest value this axis offers. Never a value the engine would have to clamp.</summary>
    public double Minimum => Axis switch
    {
        OklchAxis.Lightness => AccentEngine.MinimumBaseLightness,
        OklchAxis.Chroma => 0.0,
        _ => 0.0
    };

    /// <summary>The highest. Chroma stops where sRGB does for the most colourful hue there is.</summary>
    public double Maximum => Axis switch
    {
        OklchAxis.Lightness => AccentEngine.MaximumBaseLightness,
        OklchAxis.Chroma => 0.22,
        _ => 360.0
    };

    /// <summary>What one arrow key moves, and what one page moves.</summary>
    private double Step => Axis == OklchAxis.Hue ? 2.0 : (Maximum - Minimum) / 50.0;

    public double Value => Axis switch
    {
        OklchAxis.Lightness => Lightness,
        OklchAxis.Chroma => Chroma,
        _ => Hue
    };

    public OklchStrip()
    {
        Focusable = true;
        Cursor = Cursors.Hand;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        MinHeight = 16;
    }

    private void Move(double raw)
    {
        if (Axis == OklchAxis.Hue)
        {
            // The circle joins, so the two ends of the strip are the same colour and dragging
            // off one end is not an error.
            Hue = ((raw % 360) + 360) % 360;
            return;
        }

        var clamped = Math.Clamp(raw, Minimum, Maximum);

        if (Axis == OklchAxis.Lightness)
        {
            Lightness = clamped;
        }
        else
        {
            Chroma = clamped;
        }
    }

    // ==================================================================== input

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        CaptureMouse();
        Track(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (IsMouseCaptured)
        {
            Track(e.GetPosition(this).X);
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
            Commit();
        }

        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        var move = e.Key switch
        {
            Key.Left or Key.Down => -Step,
            Key.Right or Key.Up => Step,
            Key.PageDown => -Step * 5,
            Key.PageUp => Step * 5,
            _ => 0.0
        };

        if (move != 0)
        {
            Move(Value + move);
            Commit();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Home)
        {
            Move(Minimum);
            Commit();
            e.Handled = true;
        }
        else if (e.Key == Key.End)
        {
            Move(Maximum);
            Commit();
            e.Handled = true;
        }
    }

    private void Commit()
    {
        if (CommitCommand is { } command && command.CanExecute(null))
        {
            command.Execute(null);
        }
    }

    private void Track(double x)
    {
        var span = RenderSize.Width - (2 * ThumbRadius);

        if (span <= 0)
        {
            return;
        }

        var fraction = Math.Clamp((x - ThumbRadius) / span, 0, 1);
        Move(Minimum + (fraction * (Maximum - Minimum)));
    }

    // ==================================================================== paint

    /// <summary>The colour at one position along this axis, with the other two held.</summary>
    private Color Sample(double value)
    {
        var (l, c, h) = Axis switch
        {
            OklchAxis.Lightness => (value, Chroma, Hue),
            OklchAxis.Chroma => (Lightness, value, Hue),
            _ => (Lightness, Chroma, value)
        };

        return ThemePalette.ToColor(Perceptual.ToHex(new Oklch(l, c, h * Math.PI / 180)));
    }

    protected override void OnRender(DrawingContext context)
    {
        var width = RenderSize.Width;
        var height = RenderSize.Height;

        if (width <= 0 || height <= 0)
        {
            return;
        }

        var radius = height / 2;
        var track = new Rect(0, 0, width, height);

        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };

        for (var index = 0; index < Samples; index++)
        {
            var offset = (double)index / (Samples - 1);
            brush.GradientStops.Add(
                new GradientStop(Sample(Minimum + (offset * (Maximum - Minimum))), offset));
        }

        brush.Freeze();
        context.DrawRoundedRectangle(brush, null, track, radius, radius);

        // The strip's own edge. Half a pixel in, so the stroke lands inside the shape rather
        // than straddling it and going soft.
        var pen = new Pen(ContourBrush, 1);
        pen.Freeze();
        context.DrawRoundedRectangle(
            null, pen, new Rect(0.5, 0.5, Math.Max(0, width - 1), Math.Max(0, height - 1)),
            Math.Max(0, radius - 0.5), Math.Max(0, radius - 0.5));

        // The thumb carries the colour it is standing on, so the control says what it selected
        // rather than only where it is. White outside and black inside the white, because one
        // ring alone disappears at one end of every strip there is.
        var span = width - (2 * ThumbRadius);

        if (span <= 0)
        {
            return;
        }

        var fraction = (Value - Minimum) / (Maximum - Minimum);
        var centre = new Point(ThumbRadius + (Math.Clamp(fraction, 0, 1) * span), height / 2);

        var fill = new SolidColorBrush(Sample(Value));
        fill.Freeze();

        var outer = new Pen(Brushes.White, 2);
        outer.Freeze();

        var inner = new Pen(new SolidColorBrush(Color.FromArgb(0x59, 0, 0, 0)), 1);
        inner.Freeze();

        context.DrawEllipse(fill, outer, centre, ThumbRadius, ThumbRadius);
        context.DrawEllipse(null, inner, centre, ThumbRadius + 1, ThumbRadius + 1);
    }

    // ==================================================================== accessibility

    /// <summary>
    /// A bare FrameworkElement is invisible to assistive technology however many properties are
    /// set on it, because nothing publishes a peer for it. This is a slider in every way that
    /// matters, so it says so, and reports its own range.
    /// </summary>
    protected override AutomationPeer OnCreateAutomationPeer() => new StripPeer(this);

    private sealed class StripPeer : FrameworkElementAutomationPeer, IRangeValueProvider
    {
        private readonly OklchStrip _strip;

        public StripPeer(OklchStrip strip) : base(strip) => _strip = strip;

        protected override AutomationControlType GetAutomationControlTypeCore() =>
            AutomationControlType.Slider;

        protected override string GetClassNameCore() => nameof(OklchStrip);

        public override object GetPattern(PatternInterface pattern) =>
            pattern == PatternInterface.RangeValue ? this : base.GetPattern(pattern);

        public bool IsReadOnly => false;

        public double Minimum => _strip.Minimum;

        public double Maximum => _strip.Maximum;

        public double SmallChange => _strip.Step;

        public double LargeChange => _strip.Step * 5;

        public double Value => _strip.Value;

        public void SetValue(double value)
        {
            _strip.Move(value);
            _strip.Commit();
        }
    }

    /// <summary>The value as the panel prints it: degrees for hue, two decimals for the rest.</summary>
    public string Readout => Axis == OklchAxis.Hue
        ? Math.Round(Hue).ToString(CultureInfo.InvariantCulture) + "°"
        : Value.ToString("0.00", CultureInfo.InvariantCulture);
}
