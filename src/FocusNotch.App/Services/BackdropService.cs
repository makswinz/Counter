using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace FocusNotch.App.Services;

/// <summary>What the window is actually rendering its glass with.</summary>
public enum BackdropMode
{
    /// <summary>Not decided yet.</summary>
    Unknown,

    /// <summary>The compositor is blurring what is behind the window.</summary>
    Native,

    /// <summary>Layered glass painted by the application. Identical layout, no compositor blur.</summary>
    Simulated
}

/// <summary>
/// Decides how the glass gets its depth, and says so honestly.
///
/// Windows 11 can blur what is behind a window for free, in the compositor, through
/// DWMWA_SYSTEMBACKDROP_TYPE. It is much better than anything an application can paint. It also
/// has a hard prerequisite: the window must not be layered. Focus Notch is layered - it is a
/// transparent frame with a small rounded card floating in it, and that is what lets the notch
/// have real rounded corners, sit flush against the top bezel and pass clicks straight through
/// everywhere else. Trading that away to get a blur would change the window's transparency, its
/// hit testing and its geometry, which is the one thing the design explicitly must not do.
///
/// The alternative APIs are worse rather than better. SetWindowCompositionAttribute with acrylic
/// does apply to a layered window, but it blurs the whole window rectangle: on a window that is
/// mostly transparent frame, that paints a large blurred rectangle behind nothing, with square
/// corners, which is precisely the artifact the design rules out. Capturing and blurring the
/// desktop by hand would mean re-reading the screen on a timer, which is worse still.
///
/// So this class probes rather than forces. If the window is ever not layered on a build new
/// enough to have the attribute, it asks the compositor for the backdrop and reports Native. On
/// this window, today, it reports Simulated and the layered glass in LiquidGlassPanel does the
/// work - the same layers, the same layout, the same contour, just without a compositor blur
/// behind them. Nothing about the interface moves between the two modes.
/// </summary>
public static class BackdropService
{
    // Documented in the Dwm API. 38 is DWMWA_SYSTEMBACKDROP_TYPE, added in Windows 11 22H2.
    private const int SystemBackdropTypeAttribute = 38;

    /// <summary>DWMSBT_TRANSIENTWINDOW: a moderate blur with a slight lift, no strong tint.</summary>
    private const int TransientWindowBackdrop = 3;

    /// <summary>The first build that carries the attribute. Earlier ones ignore or reject it.</summary>
    private const int MinimumBuild = 22621;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr window, int attribute, ref int value, int size);

    /// <summary>What the last <see cref="Apply"/> settled on.</summary>
    public static BackdropMode Mode { get; private set; } = BackdropMode.Unknown;

    /// <summary>Why, in one line, for the diagnostics log and the readme.</summary>
    public static string Reason { get; private set; } = "not applied";

    /// <summary>
    /// Asks the compositor for a backdrop if this window can have one, and falls back silently
    /// and completely if it cannot. Never throws, and never changes the window.
    /// </summary>
    public static BackdropMode Apply(Window window)
    {
        try
        {
            if (window.AllowsTransparency)
            {
                // A layered window. The attribute is accepted and then ignored, which is worse
                // than not asking: it would leave the interface claiming a blur it does not have.
                return Settle(BackdropMode.Simulated, "the window is layered, which the compositor backdrop excludes");
            }

            if (Environment.OSVersion.Version.Build < MinimumBuild)
            {
                return Settle(BackdropMode.Simulated, "Windows build " + Environment.OSVersion.Version.Build + " has no system backdrop");
            }

            var handle = new WindowInteropHelper(window).Handle;

            if (handle == IntPtr.Zero)
            {
                return Settle(BackdropMode.Simulated, "the window has no handle yet");
            }

            var backdrop = TransientWindowBackdrop;
            var result = DwmSetWindowAttribute(handle, SystemBackdropTypeAttribute, ref backdrop, sizeof(int));

            return result == 0
                ? Settle(BackdropMode.Native, "compositor backdrop applied")
                : Settle(BackdropMode.Simulated, "the compositor refused the backdrop (0x" + result.ToString("X8") + ")");
        }
        catch (Exception ex)
        {
            Log.Warn("Could not query the system backdrop; using layered glass.", ex);
            return Settle(BackdropMode.Simulated, "the backdrop could not be queried");
        }
    }

    private static BackdropMode Settle(BackdropMode mode, string reason)
    {
        Mode = mode;
        Reason = reason;
        Diag.Write("backdrop", "mode", ("mode", mode), ("reason", reason));
        return mode;
    }
}
