using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using FocusNotch.App.Controls;
using FocusNotch.App.Interop;
using FocusNotch.App.Services;
using FocusNotch.App.ViewModels;
using FocusNotch.Core.Models;
using Microsoft.Win32;

namespace FocusNotch.App.Views;

/// <summary>
/// The notch window: one borderless transparent window anchored to the horizontal centre of the
/// top edge of the chosen monitor.
///
/// This class renders and collects input. It decides nothing. Which panel is showing is
/// <see cref="OverlayStateMachine"/>'s to say, and where the window physically is is
/// <see cref="NotchGeometryCoordinator"/>'s: no handler here writes a size, a position or an
/// animation of its own, which is what stops the window from being pulled in two directions at
/// once. Layout events are used only to keep the clip in step; none of them can move the window.
/// </summary>
public partial class NotchWindow : Window
{
    // Room for the shadow. Never any at the top: the notch must touch the bezel.
    private const double ShadowSide = 16;
    private const double ShadowBottom = 16;

    private const double CollapsedWidth = 330;
    private const double CollapsedHeight = 42;
    private const double QuickWidth = 520;
    private const double PlannerWidth = 600;

    private const double CollapsedTopRadius = 2;
    private const double CollapsedBottomRadius = 13;
    private const double ExpandedBottomRadius = 14;

    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan ContentFadeDelay = TimeSpan.FromMilliseconds(40);

    /// <summary>
    /// How often hover deadlines are checked. It only runs while one is pending, so an idle
    /// notch costs nothing, and 40 ms is fine enough that a 220 ms delay lands where intended.
    /// </summary>
    private static readonly TimeSpan HoverPumpInterval = TimeSpan.FromMilliseconds(40);

    private readonly DispatcherTimer _hoverPump;
    private readonly TranslateTransform _quickSlide = new();
    private readonly TranslateTransform _plannerSlide = new();
    private readonly TranslateTransform _statisticsSlide = new();
    private readonly TranslateTransform _settingsSlide = new();

    /// <summary>
    /// The notch glow, detached while the panel is changing size.
    ///
    /// A gaussian blur over a surface whose height changes every frame is re-rendered every
    /// frame, and it is the one effect in this app expensive enough to cost a frame at 150
    /// percent scaling. It is put back the instant the transition settles, so the resting state
    /// keeps its glow and the moving state keeps its frame rate.
    /// </summary>
    private BlurEffect? _glowBlur;

    private NotchGeometryCoordinator? _geometry;
    private ShellViewModel? _vm;
    private OverlayStateMachine? _machine;

    private IntPtr _handle = IntPtr.Zero;
    private MonitorInfo _monitor;
    private bool _shuttingDown;
    private bool _refitQueued;
    private double _bottomRadius = CollapsedBottomRadius;

    public NotchWindow()
    {
        InitializeComponent();

        _monitor = MonitorService.Resolve(null);

        _hoverPump = new DispatcherTimer(DispatcherPriority.Input) { Interval = HoverPumpInterval };
        _hoverPump.Tick += OnHoverPump;

        QuickContent.RenderTransform = _quickSlide;
        PlannerGroup.RenderTransform = _plannerSlide;
        StatisticsGroup.RenderTransform = _statisticsSlide;
        SettingsGroup.RenderTransform = _settingsSlide;

        _glowBlur = NotchGlow.Effect as BlurEffect;

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closing += OnClosing;
        Deactivated += OnDeactivated;
        PreviewKeyDown += OnPreviewKeyDown;

        // Only the root boundary reports hover intent. Moving between child controls never
        // reaches the state machine, so it can never open or close the panel.
        RootHost.MouseEnter += OnRootMouseEnter;
        RootHost.MouseLeave += OnRootMouseLeave;

        // A press anywhere inside pins the panel open, so it survives the pointer wandering.
        RootHost.PreviewMouseLeftButtonDown += OnRootPressed;

        // The header background still toggles the panel. It is safe to do that now: a
        // deliberate close holds hover opening off until the pointer has actually left.
        HeaderGrid.MouseLeftButtonUp += OnHeaderClicked;

        // Owned popups - tooltips above all - must not be read as the user leaving.
        AddHandler(ToolTipService.ToolTipOpeningEvent, new RoutedEventHandler(OnToolTipOpening), true);
        AddHandler(ToolTipService.ToolTipClosingEvent, new RoutedEventHandler(OnToolTipClosing), true);

        RegisterBackdrops();

        NotchSurface.SizeChanged += (_, _) => UpdateSurfaceClip();
        DraftTitleBox.KeyDown += OnDraftKeyDown;
        DraftNoteBox.KeyDown += OnDraftKeyDown;

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    public bool KeepOnTop { get; set; } = true;

    public double TopOffset { get; set; }

    /// <summary>Set from the tray menu; hover opening is on by default.</summary>
    public bool OpenOnHover
    {
        get => _machine?.OpenOnHover ?? true;
        set
        {
            if (_machine is not null)
            {
                _machine.OpenOnHover = value;
            }

            _pendingOpenOnHover = value;
        }
    }

    private bool _pendingOpenOnHover = true;

    private static bool AnimationsEnabled => SystemParameters.ClientAreaAnimation;

    // =================================================================================
    // Lifecycle
    // =================================================================================

    public void Attach(ShellViewModel viewModel, MonitorInfo monitor, double topOffset, bool keepOnTop)
    {
        _vm = viewModel;
        _machine = viewModel.Overlay;
        _machine.OpenOnHover = _pendingOpenOnHover;
        DataContext = viewModel;

        TopOffset = topOffset;
        KeepOnTop = keepOnTop;
        Topmost = keepOnTop;
        _monitor = monitor;

        _geometry = new NotchGeometryCoordinator(
            () => _monitor,
            MeasureShell,
            new ShellSize(CollapsedWidth, CollapsedHeight, CollapsedBottomRadius))
        {
            ShadowSide = ShadowSide,
            ShadowBottom = ShadowBottom,
            TopOffset = topOffset
        };

        _geometry.Advanced += OnGeometryAdvanced;
        _geometry.AnimatingChanged += OnAnimatingChanged;

        if (_handle != IntPtr.Zero)
        {
            _geometry.AttachHandle(_handle);
        }

        _machine.TransitionAccepted += OnPanelTransition;
        _machine.OverlayChanged += OnOverlayChanged;

        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.ContentSizeChanged += QueueRefit;
        viewModel.RequestFocusNewTaskField += FocusNewTaskField;

        ApplyAccent(viewModel.Accent);
        ApplyPanelVisuals(animate: false);
    }

    public void SetMonitor(MonitorInfo monitor)
    {
        _monitor = monitor;
        _geometry?.Reposition();
    }

    public void SetTopmost(bool value)
    {
        KeepOnTop = value;
        Topmost = value;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _handle = new WindowInteropHelper(this).Handle;

        // Hide from Alt+Tab and the taskbar without making the window click-through.
        var exStyle = NativeMethods.GetWindowLong(_handle, NativeMethods.GWL_EXSTYLE);
        exStyle |= NativeMethods.WS_EX_TOOLWINDOW;
        exStyle &= ~NativeMethods.WS_EX_APPWINDOW;
        NativeMethods.SetWindowLong(_handle, NativeMethods.GWL_EXSTYLE, exStyle);

        HwndSource.FromHwnd(_handle)?.AddHook(WndProc);

        _geometry?.AttachHandle(_handle);

        // Asked for once, honestly, and fallen back from completely. See BackdropService: this
        // window is layered, which is what gives the notch its real rounded corners and its
        // click-through frame, and a layered window cannot have a compositor backdrop. The glass
        // is painted instead, and the layout is identical either way.
        BackdropService.Apply(this);

        // And the blur that the layered window cannot have is put where it can exist: one
        // ordinary window under each glass surface, carrying the compositor's own acrylic.
        Backdrop.Attach(this);

        ApplyHairline();
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        ApplyHairline();
        UpdateSurfaceClip();
        ApplyPanelVisuals(animate: false);
        _geometry?.Reposition();
    }

    /// <summary>
    /// Resolves the hairline thickness for whichever display the window is currently on.
    ///
    /// Every contour, divider, focus ring and checkbox stroke in the application resolves this
    /// dynamically, so one call moves all of them together and none can be left behind at the
    /// old scale after the window is dragged to a second monitor.
    /// </summary>
    private void ApplyHairline()
    {
        var resources = Application.Current?.Resources;

        if (resources is null)
        {
            return;
        }

        if (DpiService.Apply(resources, VisualTreeHelper.GetDpi(this).DpiScaleX))
        {
            UpdateSurfaceClip();
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case NativeMethods.WM_DPICHANGED:
            case NativeMethods.WM_DISPLAYCHANGE:
                // Re-resolving the monitor is the only thing allowed to reposition outside a
                // transition, and it is deferred so it can never run inside a layout pass. The
                // hairline is recomputed in the same pass, because a per-monitor DPI change is
                // exactly when a one-physical-pixel border stops being one physical pixel.
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    ApplyHairline();
                    RefreshMonitor();
                }), DispatcherPriority.Background);
                break;
        }

        return IntPtr.Zero;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
        => Dispatcher.BeginInvoke(new Action(RefreshMonitor), DispatcherPriority.Background);

    public void RefreshMonitor()
    {
        if (_shuttingDown)
        {
            return;
        }

        _monitor = MonitorService.Resolve(_monitor.DeviceName);
        _geometry?.Reposition();
    }

    // =================================================================================
    // Geometry, entirely through the coordinator
    // =================================================================================

    private void OnPanelTransition(PanelTransition transition)
    {
        _geometry?.Run(transition, animate: true);
        ApplyPanelVisuals(animate: true);
    }

    private void OnOverlayChanged()
    {
        // An overlay can change how tall the panel needs to be, but it never changes the level.
        QueueRefit();
    }

    /// <summary>
    /// Asks the coordinator to re-fit the current level to changed content. Requests are
    /// coalesced onto one dispatcher callback, so an edit that touches several rows produces a
    /// single re-fit rather than one per row.
    /// </summary>
    private void QueueRefit()
    {
        if (_refitQueued || _shuttingDown || _machine is null || _machine.Level == PanelLevel.Collapsed)
        {
            return;
        }

        _refitQueued = true;

        Dispatcher.BeginInvoke(
            new Action(() =>
            {
                _refitQueued = false;

                if (_shuttingDown || _machine is null || _machine.Level == PanelLevel.Collapsed)
                {
                    return;
                }

                _geometry?.Refit(
                    _machine.RequestRefit(TransitionReason.ContentChanged),
                    _machine.Level,
                    animate: true);
            }),
            DispatcherPriority.Background);
    }

    /// <summary>
    /// The size the shell needs for a level: an explicit width, and a height measured from the
    /// real content so nothing is ever clipped and nothing is ever padded out.
    /// </summary>
    private ShellSize MeasureShell(PanelLevel level)
    {
        var width = level switch
        {
            PanelLevel.Quick => QuickWidth,
            PanelLevel.Planner => PlannerWidth,
            PanelLevel.Statistics => PlannerWidth,
            PanelLevel.Settings => PlannerWidth,
            _ => CollapsedWidth
        };

        if (level == PanelLevel.Collapsed)
        {
            return new ShellSize(width, CollapsedHeight, CollapsedBottomRadius);
        }

        return new ShellSize(
            width,
            Math.Max(CollapsedHeight, MeasureContentHeight(level, width)),
            ExpandedBottomRadius);
    }

    /// <summary>
    /// Measures the content for a level at the width that level will have.
    ///
    /// This has to measure the live tree at a width the window is not currently at, which leaves
    /// the cached desired sizes describing a layout that is not on screen. If it is left that
    /// way, the very next arrange runs against stale measurements and the header re-centres for
    /// a single frame, which is seen as the title jumping a few pixels. So the tree is always
    /// measured back to the width it actually occupies before this returns.
    /// </summary>
    private double MeasureContentHeight(PanelLevel level, double width)
    {
        try
        {
            var quickWas = QuickContent.Visibility;
            var plannerWas = PlannerGroup.Visibility;
            var liveWidth = double.IsNaN(ContentHost.Width) ? width : ContentHost.Width;

            var statisticsWas = StatisticsGroup.Visibility;
            var settingsWas = SettingsGroup.Visibility;

            QuickContent.Visibility = Visibility.Visible;
            PlannerGroup.Visibility = level == PanelLevel.Planner ? Visibility.Visible : Visibility.Collapsed;
            StatisticsGroup.Visibility =
                level == PanelLevel.Statistics ? Visibility.Visible : Visibility.Collapsed;
            SettingsGroup.Visibility =
                level == PanelLevel.Settings ? Visibility.Visible : Visibility.Collapsed;

            ContentStack.Measure(new Size(width, double.PositiveInfinity));
            var height = ContentStack.DesiredSize.Height;

            QuickContent.Visibility = quickWas;
            PlannerGroup.Visibility = plannerWas;
            StatisticsGroup.Visibility = statisticsWas;
            SettingsGroup.Visibility = settingsWas;

            // Put the measurement back where the window really is, so nothing downstream can
            // arrange against the size we were only asking about.
            ContentStack.Measure(new Size(liveWidth, double.PositiveInfinity));

            return Math.Ceiling(height);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not measure the panel height; keeping the current size.", ex);
            return _geometry?.Current.Height ?? CollapsedHeight;
        }
    }

    /// <summary>
    /// Drops the glow's blur while the panel is resizing and restores it once it settles. This
    /// is the only effect in the app expensive enough to matter, and it is invisible during a
    /// two-hundred-millisecond move anyway.
    /// </summary>
    private void OnAnimatingChanged(bool animating)
    {
        if (_shuttingDown)
        {
            return;
        }

        NotchGlow.Effect = animating ? null : _glowBlur;
    }

    /// <summary>Called once per rendered frame while a transition advances.</summary>
    private void OnGeometryAdvanced(ShellSize shell)
    {
        // The card is sized here, not by the window. The window is a fixed-width transparent
        // frame; this is the only thing whose width actually moves.
        var width = Math.Min(shell.Width, _geometry?.CardWidthLimit ?? shell.Width);
        if (Math.Abs(ContentHost.Width - width) > 0.01)
        {
            ContentHost.Width = width;
        }

        if (Math.Abs(shell.BottomRadius - _bottomRadius) > 0.01)
        {
            _bottomRadius = shell.BottomRadius;

            var radius = new CornerRadius(
                CollapsedTopRadius, CollapsedTopRadius, shell.BottomRadius, shell.BottomRadius);

            // The contour ring and everything drawn at the outer edge take the outer radius; the
            // glass body sits one physical pixel inside it and rounds to match, so the two curves
            // stay concentric and the ring keeps one weight all the way round the corner.
            var inset = DpiService.Scale > 0 ? 1.0 / DpiService.Scale : 1.0;

            var inner = new CornerRadius(
                Math.Max(0, radius.TopLeft - inset),
                Math.Max(0, radius.TopRight - inset),
                Math.Max(0, radius.BottomRight - inset),
                Math.Max(0, radius.BottomLeft - inset));

            NotchContour.CornerRadius = radius;
            NotchEdgeAccent.CornerRadius = radius;
            NotchGlow.CornerRadius = radius;
            NotchEdge.CornerRadius = inner;
            UpdateSurfaceClip();
        }
    }

    /// <summary>
    /// Clips the notch interior to its own rounded outline, so the progress line and any content
    /// stop exactly at the curve instead of squaring off the bottom corners.
    /// </summary>
    private void UpdateSurfaceClip()
    {
        var width = NotchSurface.ActualWidth;
        var height = NotchSurface.ActualHeight;

        if (width <= 0 || height <= 0)
        {
            NotchSurface.Clip = null;
            return;
        }

        // One pixel inside the border, matching the border's own inner radius.
        var top = Math.Max(0, CollapsedTopRadius - 1);
        var bottom = Math.Max(0, _bottomRadius - 1);

        NotchSurface.Clip = BuildRoundedRect(width, height, top, top, bottom, bottom);
    }

    private static Geometry BuildRoundedRect(
        double width, double height, double topLeft, double topRight, double bottomRight, double bottomLeft)
    {
        var half = Math.Min(width, height) / 2;
        topLeft = Math.Clamp(topLeft, 0, half);
        topRight = Math.Clamp(topRight, 0, half);
        bottomRight = Math.Clamp(bottomRight, 0, half);
        bottomLeft = Math.Clamp(bottomLeft, 0, half);

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(topLeft, 0), isFilled: true, isClosed: true);
            ctx.LineTo(new Point(width - topRight, 0), true, false);
            ctx.ArcTo(new Point(width, topRight), new Size(topRight, topRight), 0, false,
                SweepDirection.Clockwise, true, false);
            ctx.LineTo(new Point(width, height - bottomRight), true, false);
            ctx.ArcTo(new Point(width - bottomRight, height), new Size(bottomRight, bottomRight), 0, false,
                SweepDirection.Clockwise, true, false);
            ctx.LineTo(new Point(bottomLeft, height), true, false);
            ctx.ArcTo(new Point(0, height - bottomLeft), new Size(bottomLeft, bottomLeft), 0, false,
                SweepDirection.Clockwise, true, false);
            ctx.LineTo(new Point(0, topLeft), true, false);
            ctx.ArcTo(new Point(topLeft, 0), new Size(topLeft, topLeft), 0, false,
                SweepDirection.Clockwise, true, false);
        }

        geometry.Freeze();
        return geometry;
    }

    // =================================================================================
    // Content cross-fade. Opacity only: none of this can change the window's size.
    // =================================================================================

    private void ApplyPanelVisuals(bool animate)
    {
        if (_vm is null)
        {
            return;
        }

        FadeSection(QuickContent, _quickSlide, _vm.IsQuickVisible, animate);
        FadeSection(PlannerGroup, _plannerSlide, _vm.IsPlannerVisible, animate);
        FadeSection(StatisticsGroup, _statisticsSlide, _vm.IsStatisticsVisible, animate);
        FadeSection(SettingsGroup, _settingsSlide, _vm.IsSettingsVisible, animate);

        // The chevron points down while there is still more to open, up at full expansion.
        ChevronButton.Icon = _vm.Panel is PanelLevel.Planner or PanelLevel.Statistics or PanelLevel.Settings
            ? IconKind.ChevronUp
            : IconKind.ChevronDown;

        ApplyAccent(_vm.Accent);
    }

    /// <summary>Cross-fades a section with a small downward slide, starting 40 ms after the shell.</summary>
    private void FadeSection(UIElement element, TranslateTransform slide, bool visible, bool animate)
    {
        element.BeginAnimation(OpacityProperty, null);
        slide.BeginAnimation(TranslateTransform.YProperty, null);

        if (!animate || !AnimationsEnabled)
        {
            element.Opacity = visible ? 1 : 0;
            slide.Y = 0;
            element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        if (visible)
        {
            element.Visibility = Visibility.Visible;

            element.BeginAnimation(OpacityProperty, new DoubleAnimation(1, FadeDuration)
            {
                BeginTime = ContentFadeDelay,
                FillBehavior = FillBehavior.HoldEnd
            });

            slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-6, 0,
                TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.HoldEnd
            });
            return;
        }

        var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(100))
        {
            FillBehavior = FillBehavior.HoldEnd
        };

        // Hidden content must stop receiving hit tests once it is gone.
        fadeOut.Completed += (_, _) =>
        {
            if (element.Opacity <= 0.02)
            {
                element.Visibility = Visibility.Collapsed;
            }
        };

        element.BeginAnimation(OpacityProperty, fadeOut);
    }

    // =================================================================================
    // Accent edge and glow
    // =================================================================================

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ShellViewModel.Accent):
                ApplyAccent(_vm!.Accent);
                break;

            case nameof(ShellViewModel.IsRunning):
                // Play becomes Pause only while a session is genuinely running. Both are the
                // Filled weight, and both are the same 14 px, so the button never changes size.
                PlayPauseButton.Icon = _vm!.IsRunning ? IconKind.Pause : IconKind.Play;
                break;

            case nameof(ShellViewModel.IsAddingTask):
            case nameof(ShellViewModel.HasOfflineNotice):
                QueueRefit();
                break;

            case nameof(ShellViewModel.IsDurationPickerOpen):
                if (_vm!.IsDurationPickerOpen)
                {
                    Dispatcher.BeginInvoke(new Action(PositionDurationPopover), DispatcherPriority.Loaded);
                }

                break;
        }
    }

    /// <summary>
    /// Paints the state of the whole tool: the contour around it, the light spilling past that
    /// contour, the reflection the glass picks up inside it, and the progress light along its
    /// bottom edge.
    ///
    /// Two decisions, kept apart on purpose. <b>Which family</b> is a question about meaning: the
    /// user's own accent for ongoing work, and the fixed amber, red and green for held, failed
    /// and finished. <b>How strongly</b> is a question about state: idle sits at four tenths,
    /// hovered or expanded at six, running at nearly full. Separating them is what lets a paused
    /// session look exactly as present as a running one while being unmistakably a different
    /// colour, and it is why nothing here has to know what colour anything is.
    ///
    /// The contour is never switched off. The tool always has an edge, on every wallpaper, in
    /// every panel state - that is the whole point of it - and the only thing that ever changes
    /// is how much of the accent shows through the neutral ring underneath.
    /// </summary>
    private void ApplyAccent(AccentState accent)
    {
        var hovered = IsMouseOver;
        var expanded = _machine is not null && _machine.Level != PanelLevel.Collapsed;

        var contourKey = accent switch
        {
            AccentState.Paused => "PausedGradientBrush",
            AccentState.FinalMinute => "DangerGradientBrush",
            AccentState.Completed => "SuccessGradientBrush",
            _ => "AccentContourBrush"
        };

        var glowKey = accent switch
        {
            AccentState.Paused => "PausedBrush",
            AccentState.FinalMinute => "DangerBrush",
            AccentState.Completed => "SuccessBrush",
            _ => "AccentGlowBrush"
        };

        var progressKey = accent switch
        {
            AccentState.Paused => "PausedGradientBrush",
            AccentState.FinalMinute => "DangerGradientBrush",
            AccentState.Completed => "SuccessGradientBrush",
            _ => "RunningGradientBrush"
        };

        // The glow never rises above a tenth. Anything stronger stops reading as light along an
        // edge and starts reading as a coloured haze around the window.
        var (contour, glow, reflection) = accent switch
        {
            AccentState.Running => (0.95, 0.10, 1.0),
            AccentState.Paused => (0.92, 0.09, 0.55),
            AccentState.FinalMinute => (1.00, 0.12, 1.0),
            AccentState.Completed => (0.95, 0.10, 0.75),
            _ => expanded
                ? (0.62, 0.05, 0.0)
                : hovered
                    ? (0.58, 0.04, 0.0)
                    : (0.40, 0.00, 0.0)
        };

        // Resource references rather than resolved brushes. Assigning the brush would pin
        // whatever colour was in the dictionary at that instant, and the next accent change
        // replaces the dictionary entry without ever touching this element again - which is
        // exactly how a green interface ends up still wearing a blue contour.
        NotchEdgeAccent.SetResourceReference(Border.BorderBrushProperty, contourKey);
        NotchGlow.SetResourceReference(Border.BackgroundProperty, glowKey);

        foreach (var card in Cards)
        {
            card.SetResourceReference(LiquidGlassPanel.AccentContourBrushProperty, contourKey);
        }

        ApplyProgressLight(progressKey);

        FadeTo(NotchEdgeAccent, contour);
        FadeTo(NotchGlow, glow);
        FadeTo(NotchReflection, reflection);

        // The planner is the panel closest to the running task, so it picks up the same warm
        // light through its glass - at half the strength, because it is a reflection spilling
        // onto a neighbouring surface rather than the lit surface itself. Nothing else in the
        // interface gets it: a coloured wash behind Statistics or behind the settings text would
        // be a tinted panel, not a reflection.
        PlannerCard.ShowAccent = reflection > 0;
        PlannerCard.AccentReflectionOpacity = reflection * 0.5;

        // Every top-level card of the tool wears the same ring at the same strength. A planner
        // outlined more faintly than the notch it is attached to would read as two objects.
        foreach (var card in Cards)
        {
            FadeContour(card, contour);
        }
    }

    /// <summary>
    /// The blurred windows that sit beneath the glass.
    ///
    /// Owned by the window because the window is what knows where every surface is, and disposed
    /// with it, so no scenery outlives the thing it was scenery for.
    /// </summary>
    public BackdropHost Backdrop { get; } = new();

    /// <summary>
    /// Tells the backdrop which glass is chosen. Called by the host after every repaint, so
    /// switching material or theme moves the blur at the same instant the tint moves.
    /// </summary>
    public void SyncBackdrop(GlassMaterial material, bool isLight) =>
        Backdrop.SetMaterial(material, !isLight);

    /// <summary>
    /// Registers every surface that is made of glass.
    ///
    /// The notch shell is registered by its contour rather than by the window, because the
    /// window includes the gutter the shadow is drawn into and a blur out there would be a
    /// rectangle hanging in mid-air around a rounded panel.
    /// </summary>
    private void RegisterBackdrops()
    {
        Backdrop.Register(NotchContour, () => NotchContour.CornerRadius);

        foreach (var card in Cards)
        {
            Backdrop.Register(card, () => card.CornerRadius);
        }

        foreach (var popover in Popovers)
        {
            Backdrop.Register(popover, () => popover.CornerRadius);
        }
    }

    /// <summary>Everything that floats over the panel rather than sitting in it.</summary>
    private IEnumerable<LiquidGlassPanel> Popovers
    {
        get
        {
            yield return DurationPopover;
            yield return SwitchConfirm;
            yield return DeleteConfirm;
            yield return CompletionCard;
            yield return ManualTimeCard;
            yield return UndoSnackbar;
        }
    }

    /// <summary>The top-level glass cards of the tool. All of them are outlined together.</summary>
    private IEnumerable<LiquidGlassPanel> Cards
    {
        get
        {
            yield return PlannerCard;
            yield return StatisticsCard;
            yield return SettingsCard;
        }
    }

    /// <summary>
    /// Repaints the parts of the window that read a colour out of the dictionary rather than
    /// binding to it.
    ///
    /// Almost everything follows a DynamicResource and re-resolves on its own when the theme
    /// service swaps an entry. The progress light does not: its leading point and its glow are
    /// stops read off a gradient rather than keys of their own, so they have to be taken again
    /// once the gradient behind them has changed. This is called after every repaint, which is
    /// when a theme or an accent has just been applied - never on a timer tick.
    /// </summary>
    public void RefreshAccentVisuals()
    {
        if (_shuttingDown || _vm is null)
        {
            return;
        }

        ApplyAccent(_vm.Accent);
    }

    /// <summary>
    /// Points the progress light at one ramp: the fill takes the gradient, the leading point
    /// takes its lit stop and the glow underneath takes the next one down.
    ///
    /// Reading the stops off the brush rather than naming three more resources is what keeps the
    /// leading point the right colour for a paused amber line and a failing red one as well as
    /// for the accent, without anything here knowing which of them it is looking at. It runs on a
    /// state change, never on a timer tick: the only thing that moves while a session runs is a
    /// width.
    /// </summary>
    private void ApplyProgressLight(string key)
    {
        if (TryFindResource(key) is not LinearGradientBrush ramp || ramp.GradientStops.Count < 2)
        {
            return;
        }

        ProgressFill.Fill = ramp;
        ProgressHead.Fill = Frozen(ramp.GradientStops[0].Color);
        ProgressGlow.Fill = Frozen(ramp.GradientStops[1].Color);
    }

    private static SolidColorBrush Frozen(Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;
    }

    /// <summary>Crossfades one glass panel's accent ring, on the same curve as the notch's.</summary>
    private static void FadeContour(LiquidGlassPanel panel, double opacity)
    {
        if (!AnimationsEnabled)
        {
            panel.BeginAnimation(LiquidGlassPanel.ContourOpacityProperty, null);
            panel.ContourOpacity = opacity;
            return;
        }

        panel.BeginAnimation(
            LiquidGlassPanel.ContourOpacityProperty,
            new DoubleAnimation(opacity, TimeSpan.FromMilliseconds(200)) { FillBehavior = FillBehavior.HoldEnd });
    }

    private static void FadeTo(UIElement element, double opacity)
    {
        if (!AnimationsEnabled)
        {
            element.BeginAnimation(OpacityProperty, null);
            element.Opacity = opacity;
            return;
        }

        element.BeginAnimation(OpacityProperty,
            new DoubleAnimation(opacity, TimeSpan.FromMilliseconds(180)) { FillBehavior = FillBehavior.HoldEnd });
    }

    /// <summary>One restrained green pulse when a session lands, then back to the resting accent.</summary>
    public void PlayCompletionPulse()
    {
        if (!AnimationsEnabled)
        {
            return;
        }

        if (TryFindResource("SuccessGradientBrush") is Brush completed)
        {
            NotchEdgeAccent.BorderBrush = completed;
        }

        if (TryFindResource("SuccessBrush") is Brush completedGlow)
        {
            NotchGlow.Background = completedGlow;
        }

        NotchEdgeAccent.BeginAnimation(OpacityProperty, null);
        NotchEdgeAccent.Opacity = 1;

        // Opacity only. The blur radius is fixed and is never animated: animating a blur
        // re-renders the whole surface on every frame.
        NotchGlow.BeginAnimation(OpacityProperty, new DoubleAnimationUsingKeyFrames
        {
            KeyFrames =
            {
                new EasingDoubleKeyFrame(0.10, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120))),
                new EasingDoubleKeyFrame(0.09, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(800)))
            },
            FillBehavior = FillBehavior.HoldEnd
        });
    }

    // =================================================================================
    // Duration popover anchoring
    // =================================================================================

    private void PositionDurationPopover()
    {
        if (_vm?.DurationTarget is not { } target)
        {
            return;
        }

        DurationPopover.UpdateLayout();

        var popWidth = DurationPopover.ActualWidth;
        var popHeight = DurationPopover.ActualHeight;
        var hostWidth = ContentHost.ActualWidth;
        var hostHeight = ContentHost.ActualHeight;

        double left = hostWidth - popWidth - 12;
        double top = 96;

        if (PlannerList.ItemContainerGenerator.ContainerFromItem(target) is FrameworkElement { IsVisible: true } row)
        {
            try
            {
                var origin = row.TransformToAncestor(ContentHost).Transform(new Point(0, 0));
                left = origin.X + row.ActualWidth - popWidth;

                // Prefer opening below the row. When there is not enough room, flip above it,
                // so the popover never covers the row it belongs to.
                var below = origin.Y + row.ActualHeight + 4;
                var above = origin.Y - popHeight - 4;

                top = below + popHeight + 8 <= hostHeight
                    ? below
                    : above >= 8 ? above : below;
            }
            catch (InvalidOperationException)
            {
                // The row is not connected to this visual tree yet; the defaults still land inside.
            }
        }

        // Keep the popover fully inside the shell, which is itself inside the monitor.
        var maxLeft = Math.Max(0, hostWidth - popWidth - 8);
        var maxTop = Math.Max(0, hostHeight - popHeight - 8);

        Canvas.SetLeft(DurationPopover, Math.Clamp(left, 8, maxLeft));
        Canvas.SetTop(DurationPopover, Math.Clamp(top, 8, maxTop));
    }

    // =================================================================================
    // Pointer and keyboard interaction
    // =================================================================================

    private void OnRootMouseEnter(object sender, MouseEventArgs e)
    {
        if (_machine is null)
        {
            return;
        }

        _machine.PointerEntered(DateTime.UtcNow);
        Diag.Write("hover", "enter", ("level", _machine.Level), ("pinned", _machine.IsPinned));

        if (_machine.Level == PanelLevel.Collapsed)
        {
            ApplyAccent(_vm!.Accent);
        }

        PumpHoverIfNeeded();
    }

    private void OnRootMouseLeave(object sender, MouseEventArgs e)
    {
        if (_machine is null)
        {
            return;
        }

        _machine.PointerExited(DateTime.UtcNow);
        Diag.Write("hover", "leave", ("level", _machine.Level), ("pinned", _machine.IsPinned),
            ("blocks", _machine.BlocksAutoCollapse));

        if (_machine.Level == PanelLevel.Collapsed)
        {
            ApplyAccent(_vm!.Accent);
        }

        PumpHoverIfNeeded();
    }

    private void PumpHoverIfNeeded()
    {
        if (_machine is null)
        {
            return;
        }

        if (_machine.HasPendingOpen || _machine.HasPendingClose)
        {
            _hoverPump.Start();
        }
        else
        {
            _hoverPump.Stop();
        }
    }

    private void OnHoverPump(object? sender, EventArgs e)
    {
        if (_machine is null || _shuttingDown)
        {
            _hoverPump.Stop();
            return;
        }

        _machine.Tick(DateTime.UtcNow);
        PumpHoverIfNeeded();
    }

    /// <summary>
    /// A press inside an open panel pins it, so it survives the pointer wandering away. It is a
    /// preview handler so the press is seen before a button consumes it, and it never marks the
    /// event handled, so the control that was actually pressed still gets its click.
    ///
    /// A press on the bare collapsed notch deliberately pins nothing. There is no panel to hold
    /// open, and pinning anyway leaves the flag set with no transition to clear it: the next
    /// hover then opens a panel that can never auto-close again.
    /// </summary>
    private void OnRootPressed(object sender, MouseButtonEventArgs e)
    {
        if (_machine is null)
        {
            return;
        }

        if (_machine.Level != PanelLevel.Collapsed)
        {
            _machine.Pin();
        }

        _machine.CancelHoverIntents();
        _hoverPump.Stop();
    }

    private void OnHeaderClicked(object sender, MouseButtonEventArgs e)
    {
        _vm?.ToggleQuickView();
        e.Handled = true;
    }

    private void OnToolTipOpening(object sender, RoutedEventArgs e) => _machine?.PushPopup();

    private void OnToolTipClosing(object sender, RoutedEventArgs e) => _machine?.PopPopup();

    /// <summary>
    /// The window lost activation. Focus moving to one of our own windows - a tooltip, a popup -
    /// is not the user leaving, so the panel stays exactly as it is.
    /// </summary>
    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (_machine is null || _shuttingDown)
        {
            return;
        }

        if (ForegroundWindowIsOurs())
        {
            Diag.Write("window", "deactivated-own-popup");
            return;
        }

        var collapsed = _machine.Deactivated();
        Diag.Write("window", "deactivated", ("level", _machine.Level),
            ("blocks", _machine.BlocksAutoCollapse), ("collapsed", collapsed));
    }

    private static bool ForegroundWindowIsOurs()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(foreground, out var pid);
        return pid == Environment.ProcessId;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        _vm?.Escape();
        e.Handled = true;
    }

    private void OnDraftKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        // Shift+Enter inserts a line break in the note; plain Enter saves.
        if (Keyboard.Modifiers == ModifierKeys.Shift && ReferenceEquals(sender, DraftNoteBox))
        {
            return;
        }

        _vm?.ConfirmDraft();
        e.Handled = true;
    }

    public void FocusNewTaskField()
    {
        Show();
        Activate();

        Dispatcher.BeginInvoke(new Action(() =>
        {
            DraftTitleBox.Focus();
            Keyboard.Focus(DraftTitleBox);
            DraftTitleBox.SelectAll();
        }), DispatcherPriority.Input);
    }

    /// <summary>Brings the notch to the front without changing the focus session.</summary>
    public void Reveal()
    {
        if (!IsVisible)
        {
            Show();
        }

        Topmost = false;
        Topmost = KeepOnTop;
        _geometry?.Reposition();
    }

    // =================================================================================
    // Shutdown
    // =================================================================================

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_shuttingDown)
        {
            return;
        }

        // Closing the panel collapses it; only an explicit Quit ends the process.
        e.Cancel = true;
        _vm?.Collapse();
    }

    public void ShutdownWindow()
    {
        _shuttingDown = true;

        // The blurred windows are scenery for this one and must not outlive it.
        Backdrop.Dispose();

        _hoverPump.Stop();
        _hoverPump.Tick -= OnHoverPump;

        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;

        if (_geometry is not null)
        {
            _geometry.Advanced -= OnGeometryAdvanced;
            _geometry.AnimatingChanged -= OnAnimatingChanged;
            _geometry.Dispose();
            _geometry = null;
        }

        if (_machine is not null)
        {
            _machine.TransitionAccepted -= OnPanelTransition;
            _machine.OverlayChanged -= OnOverlayChanged;
            _machine = null;
        }

        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            _vm.ContentSizeChanged -= QueueRefit;
            _vm.RequestFocusNewTaskField -= FocusNewTaskField;
        }

        if (_handle != IntPtr.Zero)
        {
            HwndSource.FromHwnd(_handle)?.RemoveHook(WndProc);
        }

        Close();
    }
}
