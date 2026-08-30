using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;

namespace Counter.App.Services;

/// <summary>
/// A real blurred backdrop, in a window of its own.
///
/// The notch is a layered window. That is what gives it genuinely rounded corners, a flush top
/// edge and a frame that passes clicks through, and it is also why nothing drawn inside it can
/// ever blur what is behind it: a layered window is composited by <c>UpdateLayeredWindow</c> and
/// never goes through DWM, so there is no backdrop to sample. No amount of tuning a tint gets
/// there. Frosted glass without a blur is not frosted glass, it is a thinner sheet of the same
/// glass, which is exactly what it looked like.
///
/// So the blur is put where a blur can exist. Each glass surface gets a small, ordinary,
/// non-layered window that sits directly beneath it in the z-order carrying DWM's own acrylic
/// backdrop, rounded to sit inside the outline of the panel above it. The notch window itself
/// is not touched at all - not its transparency, not its hit testing, not its geometry - and the
/// panel keeps drawing its tint, its rim and its ripple on top. What changes is only what those
/// layers are drawn over.
///
/// The window is invisible to input and to the task switcher, it never activates, and it exists
/// only while a translucent material is chosen. Solid glass creates none of these at all.
/// </summary>
public sealed class AcrylicBackdrop : IDisposable
{
    private const string ClassName = "CounterBackdrop";

    // Window styles: a popup that cannot be clicked, cannot be focused, and does not appear in
    // the task switcher. It is scenery, and scenery must never take a click meant for the panel.
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private const int SW_SHOWNOACTIVATE = 4;
    private const int SW_HIDE = 0;

    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOOWNERZORDER = 0x0200;

    // DWM: the documented Windows 11 route. Attribute 38 asks for a system backdrop and 3 is the
    // transient-window acrylic, which is the strong blur a flyout gets. Attribute 20 tells DWM
    // which way to tint it, and 33 asks it to round the corners.
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMSBT_TRANSIENTWINDOW = 3;
    private const int DWMWCP_DONOTROUND = 1;
    private const int DWMWCP_ROUND = 2;

    /// <summary>
    /// The radius DWM rounds a window at, in physical pixels, and the first build that will.
    ///
    /// It is not configurable and it does not match the panel's own radius, which is why
    /// <see cref="Fit"/> exists: the backdrop is drawn a little smaller than the panel so that
    /// DWM's curve falls inside the panel's curve rather than crossing it.
    ///
    /// Taken as physical rather than as device-independent pixels on purpose. If DWM scales it
    /// with the display after all then the real curve is wider than assumed here, the computed
    /// inset is larger than it needed to be, and the backdrop is still inside the panel - which
    /// is the direction to be wrong in.
    /// </summary>
    private const int DwmCornerRadius = 8;

    private const int MinimumRoundedBuild = 22000;

    // The Windows 10 route, and the fallback when the documented one is not honoured.
    private const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;
    private const int WCA_ACCENT_POLICY = 19;

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left, Right, Top, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    private delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WndClassEx wndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        int exStyle, string className, string? windowName, int style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursorW(IntPtr instance, int cursor);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern int CombineRgn(IntPtr destination, IntPtr a, IntPtr b, int mode);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("gdi32.dll")]
    private static extern IntPtr GetStockObject(int index);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? name);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfoData
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref Rect rect, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfoData info);

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    private const int RGN_AND = 1;
    private const int RGN_OR = 2;
    private const int BLACK_BRUSH = 4;
    private const int IDC_ARROW = 32512;

    // The delegate is held in a static field on purpose: handing a collected delegate's thunk to
    // the window class is the classic way a native callback becomes a hard crash weeks later.
    private static readonly WndProc Procedure = DefWindowProcW;
    private static bool _registered;
    private static readonly object Gate = new();

    /// <summary>
    /// Whether DWM will round this window for us.
    ///
    /// It matters far more than it sounds. The acrylic accent state blurs the window's whole
    /// rectangle and takes no notice whatsoever of <c>SetWindowRgn</c> - verified on 26200 by
    /// clipping a live backdrop to half its width and watching nothing change - so on Windows 11
    /// the only thing that will clip a blur to a curve is DWM's own corner preference. On
    /// Windows 10, which has no corner preference, the region is the only tool there is and it
    /// does work. So the two paths are not a preference and a fallback, they are two different
    /// operating systems, and the window uses whichever one it is running on.
    /// </summary>
    private static readonly bool Rounds = Environment.OSVersion.Version.Build >= MinimumRoundedBuild;

    private IntPtr _hwnd;
    private Int32Rect _placed;
    private (int TopLeft, int TopRight, int BottomRight, int BottomLeft) _shaped = (-1, -1, -1, -1);
    private bool _shown;
    private int _tint;
    private bool _dark = true;
    private bool _disposed;

    /// <summary>
    /// Packs a tint the way the accent policy wants it, which is alpha, blue, green, red.
    /// </summary>
    public static int Tint(byte alpha, byte red, byte green, byte blue) =>
        unchecked((int)(((uint)alpha << 24) | ((uint)blue << 16) | ((uint)green << 8) | red));

    /// <summary>Whether the compositor will actually blur, rather than hand back a flat colour.</summary>
    public static bool Available { get; private set; } = true;

    /// <summary>How the blur was obtained, or why there is not one.</summary>
    public static string Method { get; private set; } = "unknown";

    /// <summary>
    /// True when the reason there is no blur is a Windows setting rather than anything here.
    ///
    /// Worth separating, because the two call for opposite responses: a machine that cannot do
    /// acrylic is a limitation to work around, and a machine that has been told not to is a
    /// preference to respect - and to say out loud, so that a panel which is merely obeying it
    /// does not read as a panel that is broken.
    /// </summary>
    public static bool TransparencyDisabled { get; private set; }

    private static bool _broken;

    /// <summary>
    /// Re-reads whether Windows is willing to composite transparency at all.
    ///
    /// "Transparency effects" in Personalisation is a global switch, and with it off DWM does not
    /// blur anything for anybody: it substitutes a solid colour and every acrylic surface on the
    /// machine, this one included, becomes opaque. It costs a registry read to know, and knowing
    /// is what lets the glass fall back to a density that is legible without a blur instead of
    /// pretending it has one.
    /// </summary>
    public static void Refresh()
    {
        bool enabled;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");

            // Absent means on: that is what a fresh Windows install behaves like.
            enabled = key?.GetValue("EnableTransparency") is not int value || value != 0;
        }
        catch (Exception)
        {
            enabled = true;
        }

        TransparencyDisabled = !enabled;

        if (_broken)
        {
            Available = false;
            return;
        }

        Available = enabled;

        if (!enabled)
        {
            Method = "none: Windows transparency effects are turned off";
        }
    }

    private static void EnsureClass()
    {
        lock (Gate)
        {
            if (_registered)
            {
                return;
            }

            var wndClass = new WndClassEx
            {
                cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Procedure),
                hInstance = GetModuleHandleW(null),
                hCursor = LoadCursorW(IntPtr.Zero, IDC_ARROW),

                // On Windows 10, black is glass: with the frame extended over the whole client
                // area, DWM treats black pixels as fully transparent, so a window that paints
                // nothing but black is a window that is nothing but backdrop. On Windows 11 the
                // frame is not extended, so there is no such rule and the window must paint
                // nothing whatsoever - a black brush there would be exactly what it says.
                hbrBackground = Rounds ? IntPtr.Zero : GetStockObject(BLACK_BRUSH),
                lpszClassName = ClassName
            };

            if (RegisterClassExW(ref wndClass) == 0)
            {
                var error = Marshal.GetLastWin32Error();

                // 1410 is "class already exists", which is success by another name.
                if (error != 1410)
                {
                    _broken = true;
                    Available = false;
                    Method = "unavailable: the backdrop window class could not be registered";
                    Log.Warn("The backdrop window class could not be registered (" + error + ").");
                    return;
                }
            }

            _registered = true;
        }
    }

    private bool Create()
    {
        EnsureClass();

        if (!Available)
        {
            return false;
        }

        _hwnd = CreateWindowExW(
            WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE,
            ClassName, null, WS_POPUP,
            0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            _broken = true;
            Available = false;
            Method = "unavailable: the backdrop window could not be created";
            return false;
        }

        if (!Rounds)
        {
            // The whole client area becomes frame, which is what lets the backdrop show through
            // it. Windows 10 only: on Windows 11 an extended frame is also a frame DWM draws a
            // shadow around, and a shadow under the backdrop is a second shadow under a panel
            // that already has one - a dark halo the design never asked for. There, the window
            // simply paints nothing at all and the acrylic behind it is what shows.
            var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            DwmExtendFrameIntoClientArea(_hwnd, ref margins);
        }

        // Rounding is not a nicety here, it is the only thing on Windows 11 that will clip an
        // acrylic blur to a curve. It arrives with a shadow attached, which is why the panels
        // above give theirs up while it is on - see LiquidGlassPanel.HasBackdrop.
        var corners = Rounds ? DWMWCP_ROUND : DWMWCP_DONOTROUND;
        DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corners, sizeof(int));

        ApplyBackdrop();
        ApplyTheme();

        return true;
    }

    /// <summary>
    /// Asks for acrylic the documented way first, and falls back to the composition attribute
    /// every build since Windows 10 has understood.
    /// </summary>
    private void ApplyBackdrop()
    {
        // The tint is not decoration and it cannot be left near zero.
        //
        // The acrylic accent state renders its blur *under* this colour, and below roughly six
        // percent alpha the compositor stops producing a blur at all and hands back flat black.
        // That is not a documented behaviour, it is a well-known quirk of the accent policy, and
        // it is exactly what a "transparent" tint of 0x10 produced here: panels that hid a wall
        // of coloured text perfectly, because they were opaque rather than because they were
        // frosted. So the tint carries real weight, and the panel's own glass is thinned to
        // compensate - see ThemePalette, which mixes a different table when a blur is behind.
        // The composition attribute rather than DWMWA_SYSTEMBACKDROP_TYPE, and deliberately.
        //
        // The composition attribute rather than DWMWA_SYSTEMBACKDROP_TYPE, because the documented
        // Windows 11 attribute produces a backdrop the window's own opacity does not reach: the
        // tint below would stop applying and every material would look the same. Neither route
        // clips itself to a window region - that was assumed here once and it was wrong, and the
        // square corners it left are why Fit and the corner preference exist.
        var policy = new AccentPolicy
        {
            AccentState = ACCENT_ENABLE_ACRYLICBLURBEHIND,
            AccentFlags = 2,
            GradientColor = _tint
        };

        var size = Marshal.SizeOf<AccentPolicy>();
        var buffer = Marshal.AllocHGlobal(size);

        try
        {
            Marshal.StructureToPtr(policy, buffer, false);

            var data = new WindowCompositionAttributeData
            {
                Attribute = WCA_ACCENT_POLICY,
                Data = buffer,
                SizeOfData = size
            };

            if (SetWindowCompositionAttribute(_hwnd, ref data) != 0)
            {
                Method = "composition attribute, acrylic blur behind, clipped to the window region";
                return;
            }

            _broken = true;
            Available = false;
            Method = "none: the compositor refused an acrylic blur";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private void ApplyTheme()
    {
        var dark = _dark ? 1 : 0;
        DwmSetWindowAttribute(_hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
    }

    /// <summary>
    /// Puts the backdrop under one panel: the same rectangle, the same rounded outline, and
    /// directly beneath it in the z-order.
    /// </summary>
    /// <param name="below">The window this must sit immediately underneath.</param>
    /// <param name="rect">Where the panel is, in physical pixels.</param>
    /// <param name="radius">The panel's four corner radii, in physical pixels.</param>
    /// <param name="dark">Which way the acrylic should be tinted.</param>
    /// <param name="tint">The acrylic's own tint, packed as the accent policy wants it.</param>
    public void Place(IntPtr below, Int32Rect rect, CornerRadius radius, bool dark, int tint)
    {
        if (_disposed || rect.Width <= 0 || rect.Height <= 0)
        {
            Hide();
            return;
        }

        var retint = tint != _tint;
        _tint = tint;

        if (_hwnd == IntPtr.Zero)
        {
            if (!Create())
            {
                return;
            }
        }
        else if (retint)
        {
            ApplyBackdrop();
        }

        if (dark != _dark)
        {
            _dark = dark;
            ApplyTheme();
        }

        var shape = (
            (int)Math.Round(radius.TopLeft),
            (int)Math.Round(radius.TopRight),
            (int)Math.Round(radius.BottomRight),
            (int)Math.Round(radius.BottomLeft));

        // Where the blur actually goes, which is not quite where the panel is.
        var placed = Fit(rect, shape, MonitorTop(rect), Rounds);

        if (placed.Width <= 0 || placed.Height <= 0)
        {
            Hide();
            return;
        }

        var resized = placed.Width != _placed.Width || placed.Height != _placed.Height;

        // On Windows 10 the region is what gives the blur its curve, and it is rebuilt only when
        // the shape actually changes. During a height animation the corner radii move as well, so
        // this is not as rare as it sounds, but it does keep a simple move from allocating a GDI
        // object every frame. On Windows 11 DWM has already rounded the window and a region would
        // only clip pixels the window does not paint.
        if (!Rounds && (resized || shape != _shaped))
        {
            _shaped = shape;
            Shape(placed.Width, placed.Height, shape);
        }

        if (placed.X != _placed.X || placed.Y != _placed.Y || resized || !_shown)
        {
            _placed = placed;
            SetWindowPos(
                _hwnd, below, placed.X, placed.Y, placed.Width, placed.Height,
                SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        }
        else
        {
            // Even when nothing moved, the panel above may have been re-ordered, so the backdrop
            // is put back directly underneath it rather than left where it was.
            SetWindowPos(_hwnd, below, 0, 0, 0, 0, SWP_NOACTIVATE | SWP_NOOWNERZORDER | 0x0003);
        }

        if (!_shown)
        {
            ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
            _shown = true;
        }
    }

    /// <summary>
    /// Where the backdrop goes so that its corners stay inside the panel's.
    ///
    /// DWM rounds a window at its own fixed radius, which is nothing like the panel's. Laid over
    /// each other at the same size, the two curves cross: a panel rounded at thirteen pixels and
    /// a backdrop rounded at eight leaves a crescent of blur outside the panel at every corner,
    /// which is a grey nub sitting in clear air and is exactly what a square corner looked like,
    /// only smaller. So the backdrop is drawn a little inside the panel instead.
    ///
    /// How far inside is arithmetic rather than taste. Two circles are nested when the distance
    /// between their centres is at most the difference of their radii; the centres here sit on
    /// the diagonal, so an inset of <c>(R - r) * (1 - 1/root 2)</c>, which is about
    /// twenty-nine percent of the gap, is the least that puts the smaller curve entirely inside
    /// the larger one. Rounded up, and never less than one pixel, because a backdrop flush with
    /// an antialiased edge shows a bright seam along it.
    ///
    /// The top edge is the exception. The notch meets the top of the screen square, and rounding
    /// it there would cut two small bites of blur out of the corners where the panel has none. So
    /// when the panel is already against the top of its monitor the backdrop is extended above
    /// it: DWM still rounds the window, but it rounds it off-screen where nobody can see it.
    /// </summary>
    /// <param name="rect">Where the panel is, in physical pixels.</param>
    /// <param name="radius">The panel's four corner radii, in physical pixels.</param>
    /// <param name="monitorTop">The top of the monitor the panel is on, in physical pixels.</param>
    /// <param name="rounded">Whether DWM is rounding the window, which is what forces the inset.</param>
    public static Int32Rect Fit(
        Int32Rect rect,
        (int TopLeft, int TopRight, int BottomRight, int BottomLeft) radius,
        int monitorTop,
        bool rounded)
    {
        if (!rounded)
        {
            // The region follows the panel exactly, so there is nothing to fit.
            return rect;
        }

        var widest = Math.Max(
            Math.Max(radius.TopLeft, radius.TopRight),
            Math.Max(radius.BottomRight, radius.BottomLeft));

        var inset = Math.Max(
            1,
            (int)Math.Ceiling((widest - DwmCornerRadius) * (1 - 1 / Math.Sqrt(2))));

        // Half the panel at most, so a surface smaller than the inset collapses to nothing and is
        // hidden rather than turning inside out.
        inset = Math.Min(inset, Math.Min(rect.Width, rect.Height) / 2);

        var left = rect.X + inset;
        var right = rect.X + rect.Width - inset;
        var bottom = rect.Y + rect.Height - inset;

        var top = rect.Y <= monitorTop
            ? rect.Y - DwmCornerRadius
            : rect.Y + inset;

        return new Int32Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    /// <summary>
    /// The top edge of the monitor this rectangle is on, in physical pixels.
    ///
    /// Asked of the rectangle rather than of the window, because the window has not moved there
    /// yet the first time this runs and a notch on a second display would spend its first frame
    /// measuring itself against the primary one.
    /// </summary>
    private static int MonitorTop(Int32Rect rect)
    {
        var bounds = new Rect
        {
            Left = rect.X,
            Top = rect.Y,
            Right = rect.X + Math.Max(1, rect.Width),
            Bottom = rect.Y + Math.Max(1, rect.Height)
        };

        var monitor = MonitorFromRect(ref bounds, MONITOR_DEFAULTTONEAREST);

        if (monitor == IntPtr.Zero)
        {
            return rect.Y;
        }

        var info = new MonitorInfoData { Size = Marshal.SizeOf<MonitorInfoData>() };

        return GetMonitorInfoW(monitor, ref info) ? info.Monitor.Top : rect.Y;
    }

    /// <summary>
    /// Clips the window to the panel's outline.
    ///
    /// A rounded region takes one radius, and the notch's top corners are not its bottom ones -
    /// it meets the screen edge square and curves only where it hangs free. So two rounded
    /// regions are built and each contributes the half it is right about, which is exact for any
    /// pair of radii that fit inside the height.
    /// </summary>
    private void Shape(int width, int height, (int TopLeft, int TopRight, int BottomRight, int BottomLeft) shape)
    {
        // One pixel in from every edge. The panel above draws its own antialiased outline over
        // this boundary, so a backdrop that stops a pixel short is invisible, while one that
        // overshoots by a pixel is a bright nub sitting outside the curve. Erring inward is free.
        const int Inset = 1;

        var left = Inset;
        var top = Inset;
        var right = Math.Max(left + 1, width - Inset);
        var bottom = Math.Max(top + 1, height - Inset);

        var upperRadius = Math.Max(shape.TopLeft, shape.TopRight);
        var lowerRadius = Math.Max(shape.BottomRight, shape.BottomLeft);
        var split = Math.Clamp(top + Math.Max(upperRadius, lowerRadius), top, bottom);

        var upper = Rounded(left, top, right, bottom, upperRadius);
        var lower = Rounded(left, top, right, bottom, lowerRadius);

        var upperHalf = CreateRectRgn(left, top, right, split);
        var lowerHalf = CreateRectRgn(left, split, right, bottom);

        CombineRgn(upper, upper, upperHalf, RGN_AND);
        CombineRgn(lower, lower, lowerHalf, RGN_AND);
        CombineRgn(upper, upper, lower, RGN_OR);

        // The window takes ownership of the region it is given; the rest are ours to release.
        DeleteObject(lower);
        DeleteObject(upperHalf);
        DeleteObject(lowerHalf);

        SetWindowRgn(_hwnd, upper, true);
    }

    /// <summary>A rounded rectangle, or a plain one when the corner has no radius at all.</summary>
    private static IntPtr Rounded(int left, int top, int right, int bottom, int radius) =>
        radius > 0
            ? CreateRoundRectRgn(left, top, right, bottom, radius * 2, radius * 2)
            : CreateRectRgn(left, top, right, bottom);

    public void Hide()
    {
        if (_shown && _hwnd != IntPtr.Zero)
        {
            ShowWindow(_hwnd, SW_HIDE);
            _shown = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }
}
