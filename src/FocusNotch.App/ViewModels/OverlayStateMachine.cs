namespace FocusNotch.App.ViewModels;

/// <summary>Why a transition was requested. Recorded in the trace, never shown to the user.</summary>
public enum TransitionReason
{
    Initial,
    HoverIntent,
    HoverExit,
    Click,
    Keyboard,
    Hotkey,
    Tray,
    Command,
    OverlayOpened,
    OverlayClosed,
    Deactivated,
    ContentChanged
}

/// <summary>
/// One accepted panel transition. The identifier is what makes an interrupted animation safe:
/// a coordinator only applies a final geometry while its own identifier is still the newest.
/// </summary>
public readonly record struct PanelTransition(long Id, PanelLevel From, PanelLevel To, TransitionReason Reason)
{
    public bool IsExpanding => To > From;
}

/// <summary>
/// The single owner of which panel is showing, which overlay is on top of it, and whether a
/// pointer that has left should collapse anything.
///
/// Everything here is pure: no WPF types, no timers, no dispatcher. Hover intent is expressed as
/// deadlines against an injected instant, so the whole hysteresis model can be tested exactly
/// rather than by sleeping. The window pumps <see cref="Tick"/> and turns the transitions this
/// class emits into geometry; it never decides a state for itself.
/// </summary>
public sealed class OverlayStateMachine
{
    public static readonly TimeSpan HoverOpenDelay = TimeSpan.FromMilliseconds(220);
    public static readonly TimeSpan HoverCloseDelay = TimeSpan.FromMilliseconds(450);

    private long _nextTransitionId = 1;
    private DateTime? _openDeadline;
    private DateTime? _closeDeadline;
    private int _popupDepth;
    private bool _suppressHoverOpen;
    private bool _isPinned;

    /// <summary>Raised for every accepted change of panel level, in order.</summary>
    public event Action<PanelTransition>? TransitionAccepted;

    /// <summary>Raised when the overlay on top of the panel changes.</summary>
    public event Action? OverlayChanged;

    /// <summary>Raised when the panel is pinned or unpinned, so the toggle can follow it.</summary>
    public event Action? PinChanged;

    public PanelLevel Level { get; private set; } = PanelLevel.Collapsed;

    public OverlayKind Overlay { get; private set; } = OverlayKind.None;

    /// <summary>Set by an explicit click into the panel. A pinned panel ignores pointer exit.</summary>
    public bool IsPinned
    {
        get => _isPinned;
        private set
        {
            if (_isPinned == value)
            {
                return;
            }

            _isPinned = value;
            PinChanged?.Invoke();
        }
    }

    /// <summary>True while the pointer is inside the window's own hit-testable region.</summary>
    public bool IsPointerInside { get; private set; }

    /// <summary>Automatic opening on hover, off when the user has turned it off in the tray.</summary>
    public bool OpenOnHover { get; set; } = true;

    /// <summary>
    /// The identifier of the newest accepted transition. A coordinator compares against this
    /// before writing a final geometry, so a superseded animation cannot land.
    /// </summary>
    public long CurrentTransitionId { get; private set; }

    /// <summary>
    /// Owned popups, editors and confirmations that must survive pointer exit and window
    /// deactivation. Counted rather than flagged, so nesting cannot leave a stuck boolean.
    /// </summary>
    public int PopupDepth => _popupDepth;

    /// <summary>Set by the shell while an inline form has unsaved input in it.</summary>
    public bool HasOpenEditor { get; set; }

    /// <summary>Set by the shell while a transient message is still on screen.</summary>
    public bool HasTransientMessage { get; set; }

    /// <summary>Everything that must stop a pointer exit or a deactivation from collapsing.</summary>
    public bool BlocksAutoCollapse =>
        Overlay != OverlayKind.None || _popupDepth > 0 || IsPinned || HasOpenEditor || HasTransientMessage;

    // =================================================================================
    // The one transition entry point
    // =================================================================================

    /// <summary>
    /// Requests a panel level. Idempotent: asking for the level that is already current does
    /// nothing at all, so a repeated hover, click or hotkey cannot restart an animation.
    /// </summary>
    public bool RequestLevel(PanelLevel target, TransitionReason reason)
    {
        if (Level == target)
        {
            return false;
        }

        var transition = new PanelTransition(_nextTransitionId++, Level, target, reason);
        Level = target;
        CurrentTransitionId = transition.Id;

        if (target == PanelLevel.Collapsed)
        {
            IsPinned = false;

            // A panel the user deliberately closed while the pointer was still over the notch
            // must not be re-opened by hover a fraction of a second later. Collapsing shrinks
            // the window out from under the pointer, and without this the two effects chase
            // each other: close, hover, open, close. The block lifts as soon as the pointer
            // genuinely leaves.
            if (IsPointerInside && reason != TransitionReason.HoverExit)
            {
                _suppressHoverOpen = true;
            }
        }

        TransitionAccepted?.Invoke(transition);
        return true;
    }

    /// <summary>Re-fits the current level to changed content without replaying a state change.</summary>
    public PanelTransition RequestRefit(TransitionReason reason)
    {
        var transition = new PanelTransition(_nextTransitionId++, Level, Level, reason);
        CurrentTransitionId = transition.Id;
        return transition;
    }

    /// <summary>True while <paramref name="id"/> is still the newest accepted transition.</summary>
    public bool IsCurrent(long id) => id == CurrentTransitionId;

    // =================================================================================
    // Overlays
    // =================================================================================

    /// <summary>
    /// Opens an overlay. The base panel level stays explicit underneath: an overlay is drawn on
    /// top of a panel, it never replaces it, so closing one always returns somewhere valid.
    /// </summary>
    public void OpenOverlay(OverlayKind kind)
    {
        if (kind == OverlayKind.None)
        {
            CloseOverlay();
            return;
        }

        CancelHoverIntents();
        IsPinned = true;

        if (Overlay == kind)
        {
            return;
        }

        Overlay = kind;
        OverlayChanged?.Invoke();
    }

    public bool CloseOverlay()
    {
        if (Overlay == OverlayKind.None)
        {
            return false;
        }

        Overlay = OverlayKind.None;
        OverlayChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// Registers an owned popup - a tooltip interaction, a context surface, anything hosted in
    /// its own window. While the depth is above zero the parent panel cannot auto-collapse, and
    /// the window losing activation to that popup is not treated as the user leaving.
    /// </summary>
    public void PushPopup()
    {
        _popupDepth++;
        CancelHoverIntents();
    }

    public void PopPopup()
    {
        if (_popupDepth > 0)
        {
            _popupDepth--;
        }
    }

    public void ResetPopups() => _popupDepth = 0;

    public void Pin() => IsPinned = true;

    public void Unpin() => IsPinned = false;

    /// <summary>The pin toggle in the planner header.</summary>
    public void TogglePin() => IsPinned = !IsPinned;

    // =================================================================================
    // Hover hysteresis
    // =================================================================================

    /// <summary>
    /// The pointer entered the window's hit-testable region. Only the root boundary calls this:
    /// moving between child controls never reaches the machine, so it can never collapse.
    /// </summary>
    public void PointerEntered(DateTime now)
    {
        IsPointerInside = true;
        _closeDeadline = null;
        _suppressHoverOpen = false;

        if (Level == PanelLevel.Collapsed && OpenOnHover && Overlay == OverlayKind.None)
        {
            _openDeadline = now + HoverOpenDelay;
        }
        else
        {
            _openDeadline = null;
        }
    }

    /// <summary>The pointer left the window's hit-testable region.</summary>
    public void PointerExited(DateTime now)
    {
        IsPointerInside = false;
        _openDeadline = null;
        _suppressHoverOpen = false;

        if (Level == PanelLevel.Collapsed || BlocksAutoCollapse)
        {
            _closeDeadline = null;
            return;
        }

        _closeDeadline = now + HoverCloseDelay;
    }

    /// <summary>
    /// Applies whichever hover deadline has come due. Called from a low-frequency timer; it
    /// touches nothing unless a deadline has actually passed, so an idle app does no work.
    /// </summary>
    public bool Tick(DateTime now)
    {
        if (_openDeadline is { } open && now >= open)
        {
            _openDeadline = null;

            if (IsPointerInside && OpenOnHover && !_suppressHoverOpen && Level == PanelLevel.Collapsed)
            {
                return RequestLevel(PanelLevel.Quick, TransitionReason.HoverIntent);
            }

            return false;
        }

        if (_closeDeadline is { } close && now >= close)
        {
            _closeDeadline = null;

            if (!IsPointerInside && !BlocksAutoCollapse && Level != PanelLevel.Collapsed)
            {
                return RequestLevel(PanelLevel.Collapsed, TransitionReason.HoverExit);
            }
        }

        return false;
    }

    /// <summary>True while hover opening is held off after a deliberate close.</summary>
    public bool IsHoverOpenSuppressed => _suppressHoverOpen;

    public bool HasPendingOpen => _openDeadline.HasValue;

    public bool HasPendingClose => _closeDeadline.HasValue;

    public void CancelHoverIntents()
    {
        _openDeadline = null;
        _closeDeadline = null;
    }

    // =================================================================================
    // Window activation
    // =================================================================================

    /// <summary>
    /// The window lost activation. An owned popup taking focus is not the user leaving, and a
    /// pinned or overlaid panel stays open, so only a genuinely idle panel collapses here.
    /// </summary>
    public bool Deactivated()
    {
        if (BlocksAutoCollapse || Level == PanelLevel.Collapsed || IsPointerInside)
        {
            return false;
        }

        CancelHoverIntents();
        return RequestLevel(PanelLevel.Collapsed, TransitionReason.Deactivated);
    }

    // =================================================================================
    // Keyboard
    // =================================================================================

    /// <summary>What one press of Escape actually closed.</summary>
    public enum EscapeResult
    {
        Nothing,
        ClosedOverlay,
        ClosedSettings,
        ClosedStatistics,
        ClosedPlanner,
        ClosedQuickView
    }

    /// <summary>Escape closes the innermost thing first: overlay, then planner, then quick view.</summary>
    public EscapeResult Escape()
    {
        if (Overlay != OverlayKind.None)
        {
            CloseOverlay();
            return EscapeResult.ClosedOverlay;
        }

        // Settings and Statistics are peers, and each one is a step out rather than a step
        // down: Escape leaves the destination and lands back on the panel underneath it. The
        // shell knows which panel that actually was and intercepts first; this is the fallback
        // for a machine driven on its own, so the ordering still holds without it.
        if (Level == PanelLevel.Settings)
        {
            RequestLevel(PanelLevel.Quick, TransitionReason.Keyboard);
            return EscapeResult.ClosedSettings;
        }

        if (Level == PanelLevel.Statistics)
        {
            RequestLevel(PanelLevel.Quick, TransitionReason.Keyboard);
            return EscapeResult.ClosedStatistics;
        }

        if (Level == PanelLevel.Planner)
        {
            RequestLevel(PanelLevel.Quick, TransitionReason.Keyboard);
            return EscapeResult.ClosedPlanner;
        }

        if (Level == PanelLevel.Quick)
        {
            IsPinned = false;
            RequestLevel(PanelLevel.Collapsed, TransitionReason.Keyboard);
            return EscapeResult.ClosedQuickView;
        }

        // Escape on an already-collapsed notch has nothing to close, but it is also the one
        // gesture that means "clear whatever is holding things open", so it releases the pin.
        IsPinned = false;
        return EscapeResult.Nothing;
    }
}
