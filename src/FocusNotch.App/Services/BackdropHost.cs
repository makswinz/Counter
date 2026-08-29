using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using FocusNotch.Core.Models;

namespace FocusNotch.App.Services;

/// <summary>
/// Keeps one blurred window under each glass surface.
///
/// The panels live in a layered window that cannot blur its own backdrop, so each one is shadowed
/// by an ordinary window sitting directly beneath it carrying DWM's acrylic. This class is the
/// bookkeeping that makes that illusion hold: the same rectangle, the same rounded outline, the
/// same moment of appearing and disappearing.
///
/// Everything here is arithmetic on rectangles, and it is deliberately cheap. Geometry is
/// recomputed when the layout says it changed rather than on a timer, each backdrop is only told
/// to move when it has actually moved, and choosing Solid glass releases every window this class
/// owns rather than leaving them hidden.
/// </summary>
public sealed class BackdropHost : IDisposable
{
    private readonly record struct Surface(FrameworkElement Element, Func<CornerRadius> Radius);

    private readonly List<Surface> _surfaces = new();
    private readonly Dictionary<FrameworkElement, AcrylicBackdrop> _backdrops = new();

    private Window? _owner;
    private IntPtr _handle;
    private bool _enabled;
    private bool _dark = true;
    private int _tint;
    private bool _disposed;

    /// <summary>
    /// Whether a real blur is actually being drawn right now.
    ///
    /// The glass is mixed differently depending on the answer, and it has to be: the reference
    /// designs are thin because a blur has already destroyed the detail behind them, and without
    /// one the same alpha leaves a browser legible through the panel.
    /// </summary>
    public bool IsBlurred => _enabled && AcrylicBackdrop.Available;

    /// <summary>
    /// True when a translucent material is chosen but Windows will not blur for it, which is a
    /// thing the settings panel says out loud rather than leaving as a mystery.
    /// </summary>
    public bool BlurRefused { get; private set; }

    /// <summary>Raised when that answer changes, so the theme can be mixed again.</summary>
    public event Action? BlurChanged;

    /// <summary>Adds one surface to keep a backdrop under. Called once, at construction.</summary>
    public void Register(FrameworkElement element, Func<CornerRadius> radius) =>
        _surfaces.Add(new Surface(element, radius));

    /// <summary>Starts following a window. Safe to call before the window has a handle.</summary>
    public void Attach(Window owner)
    {
        _owner = owner;
        _handle = new WindowInteropHelper(owner).Handle;

        owner.LayoutUpdated += (_, _) => Update();
        owner.LocationChanged += (_, _) => Update();
        owner.IsVisibleChanged += (_, _) => Update();
    }

    /// <summary>
    /// Turns the blur on or off for the whole interface.
    ///
    /// Solid glass does not want one, and a window that is not wanted is destroyed rather than
    /// hidden: an invisible window is still a window the compositor has to think about.
    /// </summary>
    public void SetMaterial(GlassMaterial material, bool isDark)
    {
        // Asked again every time, because it is a switch the user can flip while the app is
        // running, and flipping it should change what is on screen rather than what is on screen
        // after a restart.
        AcrylicBackdrop.Refresh();

        var wanted = material != GlassMaterial.Solid && AcrylicBackdrop.Available;
        var was = IsBlurred;

        _dark = isDark;
        _tint = TintFor(material, isDark);

        if (!wanted && _enabled)
        {
            Release();
        }

        _enabled = wanted;
        BlurRefused = material != GlassMaterial.Solid && !AcrylicBackdrop.Available;

        if (_enabled)
        {
            Update();
        }

        if (IsBlurred != was)
        {
            BlurChanged?.Invoke();
        }
    }

    /// <summary>
    /// How heavily the acrylic tints what it blurs.
    ///
    /// Frosted glass carries more of it than liquid does, which is the same difference the two
    /// materials have everywhere else. Neither goes near the floor below which the compositor
    /// gives up and returns black instead of a blur.
    /// </summary>
    private static int TintFor(GlassMaterial material, bool isDark)
    {
        var alpha = (byte)(material == GlassMaterial.Frosted ? 0x7A : 0x4D);

        return isDark
            ? AcrylicBackdrop.Tint(alpha, 0x14, 0x17, 0x1C)
            : AcrylicBackdrop.Tint(alpha, 0xF5, 0xF7, 0xFA);
    }

    /// <summary>Places every backdrop under the surface it belongs to.</summary>
    public void Update()
    {
        if (_disposed || !_enabled || _owner is null)
        {
            return;
        }

        if (_handle == IntPtr.Zero)
        {
            _handle = new WindowInteropHelper(_owner).Handle;

            if (_handle == IntPtr.Zero)
            {
                return;
            }
        }

        var source = PresentationSource.FromVisual(_owner);

        if (source?.CompositionTarget is null)
        {
            return;
        }

        var scale = source.CompositionTarget.TransformToDevice;
        var was = IsBlurred;

        foreach (var surface in _surfaces)
        {
            var element = surface.Element;

            if (!element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0
                || !_owner.IsVisible)
            {
                if (_backdrops.TryGetValue(element, out var idle))
                {
                    idle.Hide();
                }

                continue;
            }

            // PointToScreen already answers in physical pixels; the size does not, so it is
            // scaled by the same transform the window is being composited through.
            var origin = element.PointToScreen(new Point(0, 0));

            var rect = new Int32Rect(
                (int)Math.Round(origin.X),
                (int)Math.Round(origin.Y),
                (int)Math.Round(element.ActualWidth * scale.M11),
                (int)Math.Round(element.ActualHeight * scale.M22));

            var radius = surface.Radius();

            var scaled = new CornerRadius(
                radius.TopLeft * scale.M11,
                radius.TopRight * scale.M11,
                radius.BottomRight * scale.M11,
                radius.BottomLeft * scale.M11);

            if (!_backdrops.TryGetValue(element, out var backdrop))
            {
                backdrop = new AcrylicBackdrop();
                _backdrops[element] = backdrop;
            }

            backdrop.Place(_handle, rect, scaled, _dark, _tint);
        }

        if (IsBlurred != was)
        {
            BlurChanged?.Invoke();
        }
    }

    private void Release()
    {
        foreach (var backdrop in _backdrops.Values)
        {
            backdrop.Dispose();
        }

        _backdrops.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Release();
    }
}
