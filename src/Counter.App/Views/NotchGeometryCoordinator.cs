using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using Counter.App.Interop;
using Counter.App.Services;
using Counter.App.ViewModels;
using Counter.Core.Models;

namespace Counter.App.Views;

/// <summary>The visible shell size the coordinator is moving towards, in device-independent units.</summary>
public readonly record struct ShellSize(double Width, double Height, double BottomRadius);

/// <summary>A window rectangle in physical monitor pixels, ready for SetWindowPos.</summary>
public readonly record struct NativeBounds(int X, int Y, int Width, int Height)
{
    /// <summary>The horizontal centre of the window, in monitor pixels.</summary>
    public double CentreX => X + Width / 2d;

    public int Right => X + Width;

    public int Bottom => Y + Height;
}

/// <summary>
/// The only component allowed to move or resize the notch window.
///
/// Width, height, the derived left edge and the bottom corner radius all advance on one
/// monotonic <see cref="Stopwatch"/> and are written to the window exactly once per rendered
/// frame, so the shell always reads as a single object changing shape rather than four
/// properties racing each other. Nothing else in the app calls SetWindowPos.
///
/// A new transition supersedes the one in flight and starts from the geometry actually on
/// screen, so reversing a half-finished animation continues smoothly instead of snapping back
/// to where the previous one began. Every transition carries the identifier minted by
/// <see cref="OverlayStateMachine"/>; a superseded transition can no longer write anything.
/// </summary>
public sealed class NotchGeometryCoordinator : IDisposable
{
    private static readonly TimeSpan OpenDuration = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan CloseDuration = TimeSpan.FromMilliseconds(160);

    private readonly Func<MonitorInfo> _monitor;
    private readonly Func<PanelLevel, ShellSize> _measure;

    private readonly Stopwatch _clock = new();

    /// <summary>DEBUG-only frame timing. Inert in Release unless COUNTER_DIAG is set.</summary>
    private readonly FrameMonitor _frames = new();

    private IntPtr _handle = IntPtr.Zero;
    private bool _running;
    private bool _applying;
    private bool _disposed;

    private long _transitionId;
    private ShellSize _from;
    private ShellSize _to;
    private TimeSpan _duration;
    private bool _easeOut;

    // The geometry actually on screen right now, in device-independent units.
    private ShellSize _current;

    // The last native rectangle written, so an unchanged frame costs nothing.
    private int _lastX = int.MinValue, _lastY = int.MinValue, _lastW = -1, _lastH = -1;

    public NotchGeometryCoordinator(
        Func<MonitorInfo> monitor,
        Func<PanelLevel, ShellSize> measure,
        ShellSize initial)
    {
        _monitor = monitor;
        _measure = measure;
        _current = initial;
        _to = initial;
        _from = initial;
    }

    /// <summary>Room reserved for the downward shadow. There is never any above the shell.</summary>
    public double ShadowSide { get; init; } = 16;

    public double ShadowBottom { get; init; } = 16;

    /// <summary>Distance from the top of the monitor to the top of the shell. Stays fixed.</summary>
    public double TopOffset { get; set; }

    /// <summary>Where the notch sits across the screen. Centre unless the user moved it.</summary>
    public NotchPlacement Placement { get; set; } = NotchPlacement.Centre;

    /// <summary>Minimum shell width, so a narrow display still gets a usable notch.</summary>
    public double MinimumWidth { get; init; } = 240;

    /// <summary>
    /// The widest the shell ever gets. The window is always this wide plus its side gutters and
    /// never changes width, because a layered window that is being resized horizontally cannot
    /// be composited in perfect step with the content inside it: for a frame or two the content
    /// is drawn for one width while the window is already at another, and everything centred
    /// visibly swings sideways. Only the card inside animates its width, as an ordinary WPF
    /// layout property, so it is laid out and composited in the same frame as everything else.
    /// The surplus is transparent and passes clicks straight through.
    /// </summary>
    public double MaxShellWidth { get; init; } = 600;

    /// <summary>Keeps the shell clear of the monitor edges.</summary>
    public double MonitorSideMargin { get; init; } = 24;

    /// <summary>The geometry on screen right now.</summary>
    public ShellSize Current => _current;

    /// <summary>The widest the card may be drawn on the current display.</summary>
    public double CardWidthLimit => ShellWidthLimit(
        _monitor(), ShadowSide, MinimumWidth, MonitorSideMargin, MaxShellWidth);

    /// <summary>True while a transition is advancing.</summary>
    public bool IsAnimating => _running;

    /// <summary>Frame timings for the last transition. Only populated when diagnostics are on.</summary>
    public FrameMonitor Frames => _frames;

    /// <summary>
    /// Raised as a transition starts and again as it settles, so the view can drop anything
    /// expensive - a blur above all - for the duration. Blurring a surface that is changing size
    /// every frame is one of the few things in this app that can genuinely miss a frame.
    /// </summary>
    public event Action<bool>? AnimatingChanged;

    /// <summary>Raised each frame with the current bottom corner radius, for the clip and edges.</summary>
    public event Action<ShellSize>? Advanced;


    /// <summary>Honours the Windows "show animations" accessibility setting.</summary>
    private static bool AnimationsEnabled => SystemParameters.ClientAreaAnimation;

    /// <summary>The window handle is only available once the native window exists.</summary>
    public void AttachHandle(IntPtr handle)
    {
        _handle = handle;
        ApplyToWindow(_current);
    }

    // =================================================================================
    // Transitions
    // =================================================================================

    /// <summary>
    /// Moves to the geometry for <paramref name="transition"/>. A request for the geometry
    /// already being targeted is dropped, so a repeated hover or click cannot restart anything.
    /// </summary>
    public void Run(PanelTransition transition, bool animate)
    {
        if (_disposed)
        {
            return;
        }

        var target = _measure(transition.To);

        // Idempotent: already there, and not moving. Nothing to do.
        if (!_running && Close(target, _current))
        {
            _transitionId = transition.Id;
            return;
        }

        // Already heading exactly there. Let the transition in flight finish rather than
        // restarting it from a fraction of the way along, which is what makes rapid repeated
        // requests stutter.
        if (_running && Close(target, _to))
        {
            _transitionId = transition.Id;
            return;
        }

        _transitionId = transition.Id;

        if (!animate || !AnimationsEnabled)
        {
            Stop();
            _current = target;
            _to = target;
            Commit(_current);
            Diag.Write("geom", "immediate", ("id", transition.Id), ("to", transition.To),
                ("w", target.Width), ("h", target.Height));
            return;
        }

        // An interrupted transition restarts from what is on screen, never from where the
        // previous one began.
        _from = _current;
        _to = target;
        _easeOut = transition.IsExpanding || transition.To != PanelLevel.Collapsed;
        _duration = transition.To == PanelLevel.Collapsed ? CloseDuration : OpenDuration;

        Diag.Write("geom", "start", ("id", transition.Id), ("reason", transition.Reason),
            ("from", transition.From), ("to", transition.To),
            ("fromW", Round(_from.Width)), ("fromH", Round(_from.Height)),
            ("toW", Round(_to.Width)), ("toH", Round(_to.Height)));

        _clock.Restart();
        _frames.Begin(transition.Id, transition.Reason.ToString());

        if (!_running)
        {
            _running = true;
            CompositionTarget.Rendering += OnRendering;
            AnimatingChanged?.Invoke(true);
        }
    }

    /// <summary>
    /// Re-fits the current level to changed content, at the same speed as an open, without
    /// replaying a state change. Collapsed never re-fits: its size is fixed by definition.
    /// </summary>
    public void Refit(PanelTransition transition, PanelLevel level, bool animate)
    {
        if (level == PanelLevel.Collapsed)
        {
            return;
        }

        Run(transition with { From = level, To = level }, animate);
    }

    /// <summary>Re-applies the current geometry after a monitor, DPI or offset change.</summary>
    public void Reposition()
    {
        Commit(_current);
    }

    /// <summary>Stops the clock and leaves the window exactly where it is.</summary>
    public void Stop()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        _clock.Stop();
        CompositionTarget.Rendering -= OnRendering;
        AnimatingChanged?.Invoke(false);
    }

    // =================================================================================
    // The frame loop
    // =================================================================================

    private void OnRendering(object? sender, EventArgs e)
    {
        // CompositionTarget.Rendering can be raised more than once for a single frame; the
        // reentrancy guard makes sure the window is moved at most once per pass either way.
        if (_applying || _disposed)
        {
            return;
        }

        var elapsed = _clock.Elapsed;
        var progress = _duration <= TimeSpan.Zero ? 1d : elapsed.TotalMilliseconds / _duration.TotalMilliseconds;

        _frames.Frame();

        if (progress >= 1d)
        {
            // The final geometry is written explicitly from the target rather than from the last
            // interpolated frame, so a transition always finishes exactly where it was asked to.
            _current = _to;
            _frames.Settle();
            Stop();
            Commit(_current);
            Diag.Write("geom", "settle", ("id", _transitionId),
                ("w", Round(_current.Width)), ("h", Round(_current.Height)));
            return;
        }

        var t = _easeOut ? EaseOutCubic(progress) : EaseInOutCubic(progress);

        _current = new ShellSize(
            Lerp(_from.Width, _to.Width, t),
            Lerp(_from.Height, _to.Height, t),
            Lerp(_from.BottomRadius, _to.BottomRadius, t));

        Commit(_current);
    }

    /// <summary>
    /// Applies one frame: the card gets its new size, then the window is moved to match. Only
    /// the height ever reaches the window, so the horizontal position of anything inside is
    /// decided purely by WPF layout inside a window whose width never moves.
    /// </summary>
    private void Commit(ShellSize shell)
    {
        Advanced?.Invoke(shell);
        ApplyToWindow(shell);
    }

    private static double Lerp(double from, double to, double t) => from + (to - from) * t;

    private static double EaseOutCubic(double t)
    {
        var inverted = 1 - t;
        return 1 - inverted * inverted * inverted;
    }

    private static double EaseInOutCubic(double t) =>
        t < 0.5
            ? 4 * t * t * t
            : 1 - Math.Pow(-2 * t + 2, 3) / 2;

    // =================================================================================
    // Native geometry
    // =================================================================================

    /// <summary>
    /// Converts a shell size into a native rectangle and writes it. The horizontal centre and
    /// the top edge are recomputed from the monitor every time, so they cannot drift, and the
    /// rectangle is rounded to whole device pixels so nothing lands on a half pixel.
    /// </summary>
    private void ApplyToWindow(ShellSize shell)
    {
        if (_handle == IntPtr.Zero || _disposed || _applying)
        {
            return;
        }

        var bounds = ComputeBounds(
            _monitor(), shell, TopOffset, ShadowSide, ShadowBottom,
            MinimumWidth, MonitorSideMargin, MaxShellWidth, Placement);

        if (bounds.X == _lastX && bounds.Y == _lastY
            && bounds.Width == _lastW && bounds.Height == _lastH)
        {
            return;
        }

        _lastX = bounds.X;
        _lastY = bounds.Y;
        _lastW = bounds.Width;
        _lastH = bounds.Height;

        _applying = true;
        try
        {
            NativeMethods.SetWindowPos(
                _handle,
                IntPtr.Zero,
                bounds.X,
                bounds.Y,
                bounds.Width,
                bounds.Height,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER);

        }
        finally
        {
            _applying = false;
        }
    }


    /// <summary>
    /// Turns a shell size in device-independent units into a native rectangle in monitor pixels.
    ///
    /// Kept static and free of window state so the conversion can be asserted directly at 100,
    /// 125 and 150 percent rather than only by looking at a running window. The horizontal
    /// centre and the top edge are recomputed from the monitor every time, and the whole
    /// rectangle lands on whole device pixels: rounding the centre rather than truncating an
    /// offset is what stops the shell sliding half a pixel sideways as the width animates.
    /// </summary>
    public static NativeBounds ComputeBounds(
        MonitorInfo monitor,
        ShellSize shell,
        double topOffset,
        double shadowSide,
        double shadowBottom,
        double minimumWidth = 240,
        double monitorSideMargin = 24,
        double maxShellWidth = 600,
        NotchPlacement placement = NotchPlacement.Centre)
    {
        var scale = monitor.Scale <= 0 ? 1d : monitor.Scale;

        // The window width is constant: the widest the shell can get, plus its gutters, clamped
        // to the display. It does not follow the animated shell width at all.
        var available = Math.Max(minimumWidth, monitor.Width / scale - monitorSideMargin - 2 * shadowSide);
        var windowShellWidth = Math.Min(maxShellWidth, available);

        var maxShellHeight = Math.Max(
            24, monitor.Height / scale - topOffset - shadowBottom - 16);
        var height = Math.Min(shell.Height, maxShellHeight);

        var physicalWidth = (int)Math.Round((windowShellWidth + 2 * shadowSide) * scale, MidpointRounding.AwayFromZero);
        var physicalHeight = (int)Math.Round((height + shadowBottom) * scale, MidpointRounding.AwayFromZero);

        var x = HorizontalOrigin(monitor, physicalWidth, monitorSideMargin, scale, placement);
        var y = monitor.Top + (int)Math.Round(topOffset * scale, MidpointRounding.AwayFromZero);

        return new NativeBounds(x, y, physicalWidth, physicalHeight);
    }

    /// <summary>
    /// Where the window's left edge lands, in monitor pixels.
    ///
    /// Centre rounds rather than truncating, which is what stops the shell sliding half a pixel
    /// sideways as its width animates. The two side placements are clamped into the monitor
    /// rather than assumed to fit: a narrow display can be smaller than the margin plus the
    /// window, and a window placed off the edge of the screen is worse than a badly placed one.
    /// </summary>
    public static int HorizontalOrigin(
        MonitorInfo monitor,
        int physicalWidth,
        double monitorSideMargin,
        double scale,
        NotchPlacement placement)
    {
        var margin = (int)Math.Round(monitorSideMargin / 2d * scale, MidpointRounding.AwayFromZero);
        var slack = monitor.Width - physicalWidth;

        if (slack <= 0)
        {
            return monitor.Left;
        }

        var offset = placement switch
        {
            NotchPlacement.Left => Math.Min(margin, slack),
            NotchPlacement.Right => Math.Max(0, slack - margin),
            _ => (int)Math.Round(slack / 2d, MidpointRounding.AwayFromZero)
        };

        return monitor.Left + Math.Clamp(offset, 0, slack);
    }

    /// <summary>The widest shell this monitor can show, after clamping.</summary>
    public static double ShellWidthLimit(
        MonitorInfo monitor, double shadowSide, double minimumWidth, double monitorSideMargin, double maxShellWidth)
    {
        var scale = monitor.Scale <= 0 ? 1d : monitor.Scale;
        var available = Math.Max(minimumWidth, monitor.Width / scale - monitorSideMargin - 2 * shadowSide);
        return Math.Min(maxShellWidth, available);
    }

    private static bool Close(ShellSize a, ShellSize b) =>
        Math.Abs(a.Width - b.Width) < 0.5 && Math.Abs(a.Height - b.Height) < 0.5;

    private static double Round(double value) => Math.Round(value, 1);

    public void Dispose()
    {
        _disposed = true;
        Stop();
    }
}
