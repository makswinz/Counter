namespace FocusNotch.App.ViewModels;

/// <summary>The interface state machine described in the product spec.</summary>
public enum NotchState
{
    CollapsedIdle,
    CollapsedRunning,
    CollapsedPaused,
    QuickView,
    PlannerView,
    DurationPickerOpen,
    TaskEditorOpen,
    FocusCompleted,
    StatisticsView,
    SettingsView
}

/// <summary>How far the window is unfolded. Drives the animated window size.</summary>
public enum PanelLevel
{
    Collapsed,
    Quick,
    Planner,

    /// <summary>
    /// The statistics surface. A fourth level of the same card rather than a separate window,
    /// so it inherits the one geometry coordinator, the one hover model and the one theme, and
    /// stays visually attached to the notch it came from.
    /// </summary>
    Statistics,

    /// <summary>
    /// Settings. A peer of <see cref="Statistics"/>, not a strip inside it: theme, accent and
    /// behaviour are things a person goes to deliberately, and reading a chart should never be
    /// the route to changing a colour. Opening either one closes the other.
    /// </summary>
    Settings
}

/// <summary>A modal layer drawn on top of the current panel. Blocks auto-collapse while open.</summary>
public enum OverlayKind
{
    None,
    DurationPicker,
    TaskEditor,
    SwitchConfirmation,
    DeleteConfirmation,
    Completed,
    ManualTime
}

/// <summary>Which task list the planner is showing.</summary>
public enum PlannerFilter
{
    Day,
    Unscheduled
}

/// <summary>Drives the notch border colour and glow.</summary>
public enum AccentState
{
    Idle,
    Running,
    Paused,
    FinalMinute,
    Completed
}
