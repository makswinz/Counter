using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Counter.App.Services;
using Counter.Core.Abstractions;
using Counter.Core.Drafts;
using Counter.Core.Focus;
using Counter.Core.Journey;
using Counter.Core.Models;
using Counter.Core.Statistics;
using Counter.Core.Streaks;
using Counter.Core.Threading;
using Counter.Core.Time;
using Counter.Core.Validation;

namespace Counter.App.ViewModels;

/// <summary>
/// The single view model behind the notch, the quick panel, the planner and the statistics.
///
/// It owns no state machine of its own. Panel level and overlay live in
/// <see cref="OverlayStateMachine"/>, the focus session lives in <see cref="FocusSessionService"/>,
/// the streak lives in <see cref="JourneyActivityService"/> and the statistics live in
/// <see cref="StatisticsService"/>. This class is where those four meet the interface, and
/// nothing else is allowed to drive them.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    private const int QuickTaskLimit = 3;
    private const int UndoWindowSeconds = 5;

    /// <summary>
    /// How long the editor stays quiet before the draft is written. Saving on every keystroke
    /// would put hundreds of writes behind a long note for no benefit; saving never would lose
    /// it to a crash. Just under a second is the pause after a thought, not after a letter.
    /// </summary>
    private static readonly TimeSpan DraftDebounce = TimeSpan.FromMilliseconds(800);

    private readonly ITaskRepository _tasks;
    private readonly IManualTimeRepository _manualTime;
    private readonly ISettingsStore _settings;
    private readonly FocusSessionService _focus;
    private readonly JourneyActivityService _journey;
    private readonly StatisticsService _statistics;
    private readonly ITaskTimeReader _timeReader;
    private readonly IBackgroundScheduler _scheduler;
    private readonly DraftStore _drafts;
    private readonly IClock _clock;

    private readonly List<TaskItem> _allTasks = new();
    private readonly Dictionary<Guid, TaskTimeTotals> _taskTime = new();

    private TaskItem? _pendingUndoDelete;
    private DateTime _undoExpiresAtUtc;
    private TaskRowViewModel? _switchTarget;
    private TaskRowViewModel? _deleteTarget;
    private TaskRowViewModel? _durationTarget;
    private TaskItem? _completedSessionTask;
    private TaskRowViewModel? _editingRow;

    private DateOnly _heatmapAnchor = DateOnly.MinValue;
    private DateTime? _draftDirtyAtUtc;
    private bool _restoringDraft;

    /// <summary>
    /// Held while a task edit and a focus transition would otherwise interleave. Both already
    /// run on the dispatcher, so they cannot truly run at once, but a completion that stops a
    /// timer re-enters through the focus service's own events, and this is what stops that
    /// re-entry from starting a second pass over the same change.
    /// </summary>
    private bool _mutating;

    public ShellViewModel(
        ITaskRepository tasks,
        IManualTimeRepository manualTime,
        ISettingsStore settings,
        FocusSessionService focus,
        JourneyActivityService journey,
        StatisticsService statistics,
        ITaskTimeReader timeReader,
        IClock clock,
        IBackgroundScheduler? scheduler = null)
    {
        _tasks = tasks;
        _manualTime = manualTime;
        _settings = settings;
        _focus = focus;
        _journey = journey;
        _statistics = statistics;
        _timeReader = timeReader;
        _scheduler = scheduler ?? InlineScheduler.Instance;
        _drafts = new DraftStore(settings);
        _clock = clock;

        DefaultDurationSeconds = _settings.GetInt(SettingKeys.DefaultDurationSeconds, (int)FocusDefaults.DefaultSeconds);
        _stopTimerWhenTaskCompleted = _settings.GetBool(SettingKeys.StopTimerWhenTaskCompleted, true);

        _selectedDate = LoadSelectedDate();
        _filter = _settings.Get(SettingKeys.LastPlannerFilter) == nameof(PlannerFilter.Unscheduled)
            ? PlannerFilter.Unscheduled
            : PlannerFilter.Day;
        _calendarMonth = new DateOnly(_selectedDate.Year, _selectedDate.Month, 1);

        Statistics = new StatisticsViewModel
        {
            Range = LoadStatisticsRange(),
            RangeRequested = SelectStatisticsRange
        };

        Overlay = new OverlayStateMachine();
        Overlay.TransitionAccepted += OnPanelTransition;
        Overlay.OverlayChanged += OnOverlayChanged;
        Overlay.PinChanged += OnPinChanged;

        _focus.StateChanged += OnFocusStateChanged;
        _focus.SessionCompleted += OnFocusSessionCompleted;
        _focus.PersistenceFailed += OnFocusPersistenceFailed;
        _focus.Committed += OnFocusCommitted;

        _journey.Changed += OnJourneyChanged;
        _statistics.Changed += OnStatisticsChanged;

        _accentId = AccentPalettes.Parse(_settings.Get(SettingKeys.AccentPalette)).Id;
        BuildAccents();
        AttachDefaultDuration();
    }

    // ---------------------------------------------------------------------------------
    // Cross-cutting notifications the host wires up (chime, toast, window nudges).
    // ---------------------------------------------------------------------------------

    public event Action<FocusSession>? FocusCompleted;

    public event Action? RequestFocusNewTaskField;

    /// <summary>Raised when the panel content changed enough that the window should re-fit.</summary>
    public event Action? ContentSizeChanged;

    /// <summary>The one owner of panel level, overlay, pinning and hover intent.</summary>
    public OverlayStateMachine Overlay { get; }

    public StatisticsViewModel Statistics { get; }

    public DateOnly TodayDate => _clock.Today();

    public int DefaultDurationSeconds { get; private set; }

    /// <summary>True while a focus transition is being written. Duplicate presses are refused.</summary>
    public bool IsCommittingFocus => _focus.IsCommitting || _mutating;

    // =================================================================================
    // Interface state, projected from the machine
    // =================================================================================

    public PanelLevel Panel => Overlay.Level;

    public OverlayKind OverlayKindNow => Overlay.Overlay;

    public NotchState State => Overlay.Overlay switch
    {
        ViewModels.OverlayKind.DurationPicker => NotchState.DurationPickerOpen,
        ViewModels.OverlayKind.TaskEditor => NotchState.TaskEditorOpen,
        ViewModels.OverlayKind.Completed => NotchState.FocusCompleted,
        _ => Overlay.Level switch
        {
            PanelLevel.Settings => NotchState.SettingsView,
            PanelLevel.Statistics => NotchState.StatisticsView,
            PanelLevel.Planner => NotchState.PlannerView,
            PanelLevel.Quick => NotchState.QuickView,
            _ => _focus.Current?.Status switch
            {
                FocusSessionStatus.Running => NotchState.CollapsedRunning,
                FocusSessionStatus.Paused => NotchState.CollapsedPaused,
                _ => NotchState.CollapsedIdle
            }
        }
    };

    public bool IsCollapsed => Overlay.Level == PanelLevel.Collapsed;

    public bool IsQuickVisible =>
        Overlay.Level is PanelLevel.Quick or PanelLevel.Planner or PanelLevel.Statistics or PanelLevel.Settings;

    public bool IsPlannerVisible => Overlay.Level == PanelLevel.Planner;

    public bool IsStatisticsVisible => Overlay.Level == PanelLevel.Statistics;

    public bool IsOverlayOpen => Overlay.Overlay != ViewModels.OverlayKind.None;

    public bool IsDurationPickerOpen => Overlay.Overlay == ViewModels.OverlayKind.DurationPicker;

    public bool IsSwitchConfirmationOpen => Overlay.Overlay == ViewModels.OverlayKind.SwitchConfirmation;

    public bool IsDeleteConfirmationOpen => Overlay.Overlay == ViewModels.OverlayKind.DeleteConfirmation;

    public bool IsManualTimeOpen => Overlay.Overlay == ViewModels.OverlayKind.ManualTime;

    public bool IsCompletionVisible => Overlay.Overlay == ViewModels.OverlayKind.Completed;

    public bool IsPinned => Overlay.IsPinned;

    public string ExpandTooltip => Overlay.Level switch
    {
        PanelLevel.Collapsed => "Open the quick view (Ctrl+Shift+F)",
        PanelLevel.Quick => "Open the full task view",
        _ => "Collapse Counter"
    };

    private void OnPanelTransition(PanelTransition transition)
    {
        Diag.Write("panel", "transition", ("id", transition.Id), ("from", transition.From),
            ("to", transition.To), ("reason", transition.Reason));

        OnPropertyChanged(nameof(Panel));
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(IsCollapsed));
        OnPropertyChanged(nameof(IsQuickVisible));
        OnPropertyChanged(nameof(IsPlannerVisible));
        OnPropertyChanged(nameof(IsStatisticsVisible));
        OnPropertyChanged(nameof(IsSettingsVisible));
        OnPropertyChanged(nameof(ExpandTooltip));

        if (transition.To == PanelLevel.Planner)
        {
            RebuildCalendar();
            RebuildPlannerTasks();
        }

        if (transition.To == PanelLevel.Statistics)
        {
            RefreshStatistics("panel-opened");
        }

        if (transition.To == PanelLevel.Settings)
        {
            // Leaving Settings must not leave a stale message from the last backup behind it.
            DataMessage = string.Empty;
        }
    }

    private void OnOverlayChanged()
    {
        Diag.Write("panel", "overlay", ("kind", Overlay.Overlay), ("depth", Overlay.PopupDepth));

        OnPropertyChanged(nameof(OverlayKindNow));
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(IsOverlayOpen));
        OnPropertyChanged(nameof(IsDurationPickerOpen));
        OnPropertyChanged(nameof(IsSwitchConfirmationOpen));
        OnPropertyChanged(nameof(IsDeleteConfirmationOpen));
        OnPropertyChanged(nameof(IsManualTimeOpen));
        OnPropertyChanged(nameof(IsCompletionVisible));
    }

    // =================================================================================
    // Timer surface
    // =================================================================================

    [ObservableProperty]
    private string _timerText = "30:00";

    [ObservableProperty]
    private string _activeTaskTitle = "Choose a task";

    [ObservableProperty]
    private double _remainingFraction;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(State))]
    [NotifyPropertyChangedFor(nameof(PlayPauseTooltip))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(State))]
    private bool _isPaused;

    [ObservableProperty]
    private bool _hasSession;

    [ObservableProperty]
    private AccentState _accent = AccentState.Idle;

    [ObservableProperty]
    private string _errorBanner = string.Empty;

    [ObservableProperty]
    private bool _hasError;

    /// <summary>Shown once when a timer finished while the app was not running.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOfflineNotice))]
    private string _offlineNotice = string.Empty;

    public bool HasOfflineNotice => !string.IsNullOrEmpty(OfflineNotice);

    [RelayCommand]
    private void DismissOfflineNotice()
    {
        OfflineNotice = string.Empty;
        ContentSizeChanged?.Invoke();
    }

    public string PlayPauseTooltip => IsRunning ? "Pause focus (Ctrl+Shift+Space)" : "Start focus (Ctrl+Shift+Space)";

    // =================================================================================
    // Collections
    // =================================================================================

    public ObservableCollection<TaskRowViewModel> QuickTasks { get; } = new();

    public ObservableCollection<TaskRowViewModel> PlannerTasks { get; } = new();

    public ObservableCollection<CalendarDayViewModel> CalendarDays { get; } = new();

    public DurationPickerViewModel DurationPicker { get; } = new();

    public ManualTimeViewModel ManualTime { get; } = new();

    /// <summary>The twelve-week grid the heatmap control draws. One list, both panels.</summary>
    [ObservableProperty]
    private IReadOnlyList<HeatmapCell> _heatmapCells = Array.Empty<HeatmapCell>();

    [ObservableProperty]
    private DateTime _heatmapToday;

    [ObservableProperty]
    private string _streakText = "0d";

    [ObservableProperty]
    private string _openCountText = "0 open";

    [ObservableProperty]
    private string _unscheduledCountText = "Unscheduled 0";

    [ObservableProperty]
    private bool _hasQuickTasks;

    [ObservableProperty]
    private bool _hasPlannerTasks;

    // =================================================================================
    // Calendar
    // =================================================================================

    [ObservableProperty]
    private DateOnly _selectedDate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CalendarMonthLabel))]
    private DateOnly _calendarMonth;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDayFilter))]
    [NotifyPropertyChangedFor(nameof(IsUnscheduledFilter))]
    private PlannerFilter _filter = PlannerFilter.Day;

    public bool IsDayFilter => Filter == PlannerFilter.Day;

    public bool IsUnscheduledFilter => Filter == PlannerFilter.Unscheduled;

    public string CalendarMonthLabel => CalendarMonth.ToString("MMMM yyyy", CultureInfo.InvariantCulture);

    public string SelectedDayLabel => SelectedDate == TodayDate
        ? "Today"
        : SelectedDate.ToString("ddd d MMM", CultureInfo.InvariantCulture);

    // =================================================================================
    // Settings surfaced in the interface
    // =================================================================================

    /// <summary>
    /// Ticking a task off stops its own timer. On by default, because a finished task that is
    /// still being timed is simply wrong: the time would keep accruing against work that is
    /// over. It is a setting rather than a rule only because somebody may be timing a block of
    /// work rather than a task.
    /// </summary>
    [ObservableProperty]
    private bool _stopTimerWhenTaskCompleted = true;

    partial void OnStopTimerWhenTaskCompletedChanged(bool value)
    {
        try
        {
            _settings.SetBool(SettingKeys.StopTimerWhenTaskCompleted, value);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not save the stop-on-completion setting.", ex);
        }
    }

    [RelayCommand]
    private void ToggleStopTimerWhenTaskCompleted() =>
        StopTimerWhenTaskCompleted = !StopTimerWhenTaskCompleted;

    /// <summary>Light, Dark or System. Applied by the host, which owns the resources.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSystemTheme))]
    [NotifyPropertyChangedFor(nameof(IsLightTheme))]
    [NotifyPropertyChangedFor(nameof(IsDarkTheme))]
    private ThemePreference _theme = ThemePreference.System;

    public bool IsSystemTheme => Theme == ThemePreference.System;

    public bool IsLightTheme => Theme == ThemePreference.Light;

    public bool IsDarkTheme => Theme == ThemePreference.Dark;

    /// <summary>Raised when the user picks a theme. The host applies it and reports back.</summary>
    public event Action<ThemePreference>? ThemeRequested;

    [RelayCommand]
    private void UseSystemTheme() => ThemeRequested?.Invoke(ThemePreference.System);

    [RelayCommand]
    private void UseLightTheme() => ThemeRequested?.Invoke(ThemePreference.Light);

    [RelayCommand]
    private void UseDarkTheme() => ThemeRequested?.Invoke(ThemePreference.Dark);

    /// <summary>Called by the host once a theme has actually been applied.</summary>
    public void ReportTheme(ThemePreference preference) => Theme = preference;

    /// <summary>
    /// Which glass the panels are made of. A third input, independent of both the theme and the
    /// accent: the material decides how much of the desktop comes through, the theme decides
    /// whether that glass is dark or pale, and the accent decides nothing about either.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSolidGlass))]
    [NotifyPropertyChangedFor(nameof(IsFrostedGlass))]
    [NotifyPropertyChangedFor(nameof(IsLiquidGlass))]
    private GlassMaterial _glass = GlassMaterials.Default;

    public bool IsSolidGlass => Glass == GlassMaterial.Solid;

    public bool IsFrostedGlass => Glass == GlassMaterial.Frosted;

    public bool IsLiquidGlass => Glass == GlassMaterial.Liquid;

    /// <summary>Raised when the user picks a material. The host applies it and reports back.</summary>
    public event Action<GlassMaterial>? GlassRequested;

    [RelayCommand]
    private void UseSolidGlass() => GlassRequested?.Invoke(GlassMaterial.Solid);

    [RelayCommand]
    private void UseFrostedGlass() => GlassRequested?.Invoke(GlassMaterial.Frosted);

    [RelayCommand]
    private void UseLiquidGlass() => GlassRequested?.Invoke(GlassMaterial.Liquid);

    /// <summary>Called by the host once a material has actually been applied.</summary>
    public void ReportGlass(GlassMaterial material) => Glass = material;

    /// <summary>
    /// True when a translucent glass is chosen and Windows will not blur behind it.
    ///
    /// Said out loud rather than left as a mystery. "Transparency effects" in Personalisation is
    /// a global switch, and with it off no application on the machine gets a blur; a panel that
    /// looks flat because of that is obeying a preference rather than failing at one, and the
    /// difference is one line of text.
    /// </summary>
    [ObservableProperty]
    private bool _glassBlurUnavailable;

    /// <summary>Called by the host after each repaint, with what the compositor actually did.</summary>
    public void ReportGlassBlur(bool refused) => GlassBlurUnavailable = refused;

    // =================================================================================
    // Inline add / edit form
    // =================================================================================

    [ObservableProperty]
    private bool _isAddingTask;

    [ObservableProperty]
    private string _draftTitle = string.Empty;

    [ObservableProperty]
    private string _draftNote = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDraftError))]
    private string _draftError = string.Empty;

    public bool HasDraftError => !string.IsNullOrEmpty(DraftError);

    [ObservableProperty]
    private string _draftHeader = "New task";

    /// <summary>"Add" for a new task, "Save" when an existing one is being edited.</summary>
    [ObservableProperty]
    private string _draftActionLabel = "Add";

    /// <summary>
    /// Records work that is already done. The selected day becomes the contribution date, so
    /// filling in a day that was missed lights up that square rather than today's.
    /// </summary>
    [ObservableProperty]
    private bool _isDraftCompleted;

    [RelayCommand]
    private void ToggleDraftCompleted() => IsDraftCompleted = !IsDraftCompleted;

    partial void OnIsAddingTaskChanged(bool value)
    {
        Overlay.HasOpenEditor = value;
        ContentSizeChanged?.Invoke();
    }

    partial void OnDraftTitleChanged(string value) => MarkDraftDirty();

    partial void OnDraftNoteChanged(string value) => MarkDraftDirty();

    partial void OnIsDraftCompletedChanged(bool value) => MarkDraftDirty();

    private void MarkDraftDirty()
    {
        if (_restoringDraft || !IsAddingTask)
        {
            return;
        }

        _draftDirtyAtUtc = _clock.UtcNow;
    }

    /// <summary>Writes the draft once typing has paused. Called from the ordinary tick.</summary>
    private void SaveDraftIfSettled(DateTime nowUtc)
    {
        if (_draftDirtyAtUtc is not { } dirty || nowUtc - dirty < DraftDebounce)
        {
            return;
        }

        _draftDirtyAtUtc = null;

        try
        {
            _drafts.Save(new TaskDraft(
                DraftTitle, DraftNote, IsDraftCompleted, _editingRow?.Id,
                Filter == PlannerFilter.Unscheduled ? null : SelectedDate));

            Diag.Write("draft", "saved", ("chars", DraftTitle.Length + DraftNote.Length));
        }
        catch (Exception ex)
        {
            Log.Warn("Could not save the task draft.", ex);
        }
    }

    private void ClearDraftRecovery()
    {
        _draftDirtyAtUtc = null;

        try
        {
            _drafts.Clear();
        }
        catch (Exception ex)
        {
            Log.Warn("Could not clear the saved task draft.", ex);
        }
    }

    /// <summary>
    /// Reopens whatever was being typed when the app last stopped. A draft only survives if it
    /// was never saved and never deliberately abandoned, so this cannot resurrect something the
    /// user already dealt with.
    /// </summary>
    public bool RestoreDraft()
    {
        TaskDraft draft;
        try
        {
            draft = _drafts.Load();
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read the saved task draft.", ex);
            return false;
        }

        if (!draft.HasContent)
        {
            return false;
        }

        _restoringDraft = true;
        try
        {
            Overlay.Pin();
            Overlay.RequestLevel(PanelLevel.Planner, TransitionReason.Command);

            _editingRow = draft.EditingTaskId is { } id ? FindRow(id) : null;
            DraftHeader = _editingRow is null ? "New task" : "Edit task";
            DraftActionLabel = _editingRow is null ? "Add" : "Save";
            DraftTitle = draft.Title;
            DraftNote = draft.Note;
            IsDraftCompleted = draft.IsCompleted;
            DraftError = string.Empty;

            if (draft.ScheduledDate is { } scheduled)
            {
                SelectedDate = scheduled;
            }

            IsAddingTask = true;
        }
        finally
        {
            _restoringDraft = false;
        }

        Diag.Write("draft", "restored", ("editing", draft.EditingTaskId));
        return true;
    }

    // =================================================================================
    // Undo / confirmation
    // =================================================================================

    [ObservableProperty]
    private bool _isUndoVisible;

    [ObservableProperty]
    private string _undoMessage = string.Empty;

    [ObservableProperty]
    private string _switchPrompt = string.Empty;

    [ObservableProperty]
    private string _deletePrompt = string.Empty;

    [ObservableProperty]
    private string _completionTitle = string.Empty;

    partial void OnIsUndoVisibleChanged(bool value) => Overlay.HasTransientMessage = value;

    // =================================================================================
    // Lifecycle
    // =================================================================================

    /// <summary>Loads persisted state and rebuilds every projection. Safe to call again.</summary>
    public void Load()
    {
        ReloadTasks();
        RebuildCalendar();
        RefreshTaskTimes("load");
        RefreshJourney("load");
        RefreshTimerSurface();
    }

    public void ReportError(string message)
    {
        ErrorBanner = message;
        HasError = !string.IsNullOrEmpty(message);
    }

    /// <summary>Says once, without a dialog, that a timer finished while the app was closed.</summary>
    public void ReportCompletedWhileClosed(FocusSession session)
    {
        var title = session.TaskTitle
                    ?? (session.TaskId.HasValue
                        ? _allTasks.FirstOrDefault(t => t.Id == session.TaskId.Value)?.Title
                        : null);

        OfflineNotice = string.IsNullOrEmpty(title)
            ? "Completed while Counter was closed"
            : "Completed while Counter was closed: " + Shorten(title, 40);

        ContentSizeChanged?.Invoke();
    }

    /// <summary>
    /// Pumped by the host on a display cadence. It advances the countdown text, the live time on
    /// the running row, the hover deadlines and the draft debounce, and nothing else: no task
    /// query, no journey query, no statistics query, no geometry.
    /// </summary>
    public void Tick()
    {
        var now = _clock.UtcNow;

        Overlay.Tick(now);
        _focus.CompleteIfDue();
        RefreshTimerSurface();
        ApplyLiveTime();
        SaveDraftIfSettled(now);

        if (IsUndoVisible && now >= _undoExpiresAtUtc)
        {
            CommitPendingDelete();
        }
    }

    private void OnFocusStateChanged()
    {
        RefreshTimerSurface();
        RefreshRowPlayState();
    }

    private void OnFocusCommitted()
    {
        // Any committed transition closed or opened a run, so the totals moved.
        RefreshTaskTimes("focus-committed");
        RefreshStatistics("focus-committed");
    }

    private void OnFocusPersistenceFailed(string message, Exception? ex)
    {
        Log.Error(message, ex);
        ReportError(message);

        // The service has already put the engine back, so the interface has to follow it rather
        // than keep showing a running timer for a session that was never saved.
        RefreshTimerSurface();
        RefreshRowPlayState();
    }

    private void OnFocusSessionCompleted(FocusSession session)
    {
        _completedSessionTask = session.TaskId.HasValue
            ? _allTasks.FirstOrDefault(t => t.Id == session.TaskId.Value)
            : null;

        CompletionTitle = _completedSessionTask?.Title ?? session.TaskTitle ?? "Focus session";

        // Open far enough for the completion card to have room, and hold it open until dismissed.
        Overlay.RequestLevel(PanelLevel.Quick, TransitionReason.Command);
        Overlay.OpenOverlay(ViewModels.OverlayKind.Completed);
        Accent = AccentState.Completed;

        // The service raises this only once the completion has been committed, so the queries
        // behind these refreshes are guaranteed to see the finished session.
        RefreshJourney("session-completed");
        RefreshTaskTimes("session-completed");
        RefreshStatistics("session-completed");
        FocusCompleted?.Invoke(session);
    }

    /// <summary>
    /// Updates only what the countdown affects. It touches no collection and no geometry, so a
    /// running timer can never move or resize the panel.
    /// </summary>
    private void RefreshTimerSurface()
    {
        var session = _focus.Current;

        HasSession = session is not null;
        IsRunning = session?.Status == FocusSessionStatus.Running;
        IsPaused = session?.Status == FocusSessionStatus.Paused;

        if (session is null)
        {
            TimerText = TimeFormat.Countdown(TimeSpan.FromSeconds(DefaultDurationSeconds));
            ActiveTaskTitle = "Choose a task";

            // Nothing is running, so the progress line shows nothing rather than a full bar.
            RemainingFraction = 0d;

            if (Overlay.Overlay != ViewModels.OverlayKind.Completed)
            {
                Accent = AccentState.Idle;
            }
        }
        else
        {
            var remaining = _focus.Remaining;
            TimerText = TimeFormat.Countdown(remaining);
            RemainingFraction = session.RemainingFractionAt(_clock.UtcNow);

            var task = session.TaskId.HasValue
                ? _allTasks.FirstOrDefault(t => t.Id == session.TaskId.Value)
                : null;
            ActiveTaskTitle = task?.Title ?? session.TaskTitle ?? "Focus session";

            if (Overlay.Overlay != ViewModels.OverlayKind.Completed)
            {
                Accent = session.Status switch
                {
                    FocusSessionStatus.Paused => AccentState.Paused,
                    FocusSessionStatus.Running when remaining.TotalSeconds <= 60 => AccentState.FinalMinute,
                    FocusSessionStatus.Running => AccentState.Running,
                    _ => AccentState.Idle
                };
            }
        }

        OnPropertyChanged(nameof(State));
    }

    /// <summary>
    /// Re-derives every row's play glyph and enabled state from the focus service, so quick
    /// view, planner and the notch always agree about what is running.
    /// </summary>
    private void RefreshRowPlayState()
    {
        var activeTaskId = _focus.Current?.TaskId;

        foreach (var row in QuickTasks.Concat(PlannerTasks))
        {
            row.IsFocused = activeTaskId.HasValue && row.Id == activeTaskId.Value;
            row.PlayState = _focus.Preview(row.Model);
            row.StartFocusCommand.NotifyCanExecuteChanged();
            row.StopFocusCommand.NotifyCanExecuteChanged();
            row.ToggleCompleteCommand.NotifyCanExecuteChanged();
        }

        ToggleFocusCommand.NotifyCanExecuteChanged();
        StopFocusCommand.NotifyCanExecuteChanged();
    }

    // =================================================================================
    // Time spent
    // =================================================================================

    /// <summary>
    /// Re-reads per-task totals off the render path. Called after committed changes only, never
    /// on a tick: the run in progress is added in memory instead.
    /// </summary>
    public void RefreshTaskTimes(string reason)
    {
        Diag.Write("time", "refresh-requested", ("reason", reason));

        _scheduler.Run(
            () => _timeReader.ReadTotals(),
            totals =>
            {
                _taskTime.Clear();
                foreach (var total in totals)
                {
                    _taskTime[total.TaskId] = total;
                }

                ApplyTaskTimes();
            },
            ex =>
            {
                Log.Error("Could not read the time spent per task.", ex);
                ReportError("Could not read the time spent on your tasks.");
            });
    }

    private void ApplyTaskTimes()
    {
        foreach (var row in QuickTasks.Concat(PlannerTasks))
        {
            row.Time = _taskTime.TryGetValue(row.Id, out var total) ? total : TaskTimeTotals.Empty(row.Id);
        }

        ApplyLiveTime();
    }

    /// <summary>
    /// Adds the run in progress to the row it belongs to.
    ///
    /// The open segment is already in memory and already capped at the timer's target, so the
    /// running row can tick every second without a single query, and the number it shows is the
    /// same one the database will hold the moment the run is closed.
    /// </summary>
    private void ApplyLiveTime()
    {
        if (_focus.CurrentSegment is not { IsOpen: true } segment
            || _focus.Current is not { Status: FocusSessionStatus.Running } session
            || session.TaskId is not { } taskId)
        {
            return;
        }

        var now = _clock.UtcNow;
        var target = session.TargetUtc;
        var end = target is { } cap && now > cap ? cap : now;
        var live = segment.SecondsAt(end);

        var stored = _taskTime.TryGetValue(taskId, out var total) ? total : TaskTimeTotals.Empty(taskId);
        var withLive = stored with
        {
            FocusSeconds = stored.FocusSeconds + live,
            SessionCount = stored.SessionCount + 1
        };

        foreach (var row in QuickTasks.Concat(PlannerTasks).Where(r => r.Id == taskId))
        {
            row.Time = withLive;
        }
    }

    // =================================================================================
    // Panel commands. Every one of them goes through the state machine.
    // =================================================================================

    [RelayCommand]
    public void ToggleQuickView()
    {
        if (Overlay.Level == PanelLevel.Collapsed)
        {
            Overlay.Pin();
            Overlay.RequestLevel(PanelLevel.Quick, TransitionReason.Command);
        }
        else
        {
            Collapse();
        }
    }

    public void OpenQuickView() => Overlay.RequestLevel(PanelLevel.Quick, TransitionReason.Command);

    /// <summary>
    /// The chevron walks the panel one step further open each press: collapsed to quick view,
    /// quick view to planner, and from anything further open all the way back to the bare notch.
    /// </summary>
    [RelayCommand]
    public void ExpandStep()
    {
        switch (Overlay.Level)
        {
            case PanelLevel.Collapsed:
                Overlay.Pin();
                Overlay.RequestLevel(PanelLevel.Quick, TransitionReason.Click);
                break;
            case PanelLevel.Quick:
                OpenPlanner();
                break;
            default:
                Collapse();
                break;
        }
    }

    [RelayCommand]
    public void OpenPlanner()
    {
        Overlay.Pin();
        Overlay.RequestLevel(PanelLevel.Planner, TransitionReason.Command);
    }

    [RelayCommand]
    public void ClosePlanner()
    {
        CancelDraft();
        Overlay.RequestLevel(PanelLevel.Quick, TransitionReason.Command);
    }

    /// <summary>The chart icon in the expanded header, the tray menu and Ctrl+Shift+S.</summary>
    [RelayCommand]
    public void OpenStatistics()
    {
        RememberReturnLevel();
        Overlay.Pin();
        Overlay.RequestLevel(PanelLevel.Statistics, TransitionReason.Command);
    }

    [RelayCommand]
    public void CloseStatistics() => Back();

    [RelayCommand]
    public void Collapse()
    {
        CancelDraft();
        Overlay.CloseOverlay();
        Overlay.Unpin();
        Overlay.RequestLevel(PanelLevel.Collapsed, TransitionReason.Command);
    }

    [RelayCommand]
    public void Pin() => Overlay.Pin();

    /// <summary>The pin toggle in the planner header.</summary>
    [RelayCommand]
    public void TogglePin() => Overlay.TogglePin();

    private void OnPinChanged() => OnPropertyChanged(nameof(IsPinned));

    /// <summary>Escape closes the innermost thing first: overlay, then panel by panel.</summary>
    [RelayCommand]
    public void Escape()
    {
        if (IsAddingTask)
        {
            CancelDraft();
            return;
        }

        if (Overlay.Overlay == ViewModels.OverlayKind.DurationPicker)
        {
            ClearDurationPickerFlags();
        }

        _switchTarget = null;
        _deleteTarget = null;

        // Statistics and Settings are destinations, so Escape leaves them the way the back
        // button does: to whichever panel they were opened from, not always to the quick view.
        if (Overlay.Overlay == ViewModels.OverlayKind.None
            && Overlay.Level is PanelLevel.Statistics or PanelLevel.Settings)
        {
            Back();
            return;
        }

        Overlay.Escape();
    }

    [RelayCommand]
    public void CloseOverlay()
    {
        if (Overlay.Overlay == ViewModels.OverlayKind.DurationPicker)
        {
            ClearDurationPickerFlags();
        }

        _switchTarget = null;
        _deleteTarget = null;
        Overlay.CloseOverlay();
    }

    private void ClearDurationPickerFlags()
    {
        foreach (var row in QuickTasks.Concat(PlannerTasks))
        {
            row.IsDurationPickerOpen = false;
        }
    }

    // =================================================================================
    // Focus control. Everything here delegates to the one focus service.
    // =================================================================================

    private bool CanToggleFocus() => !IsCommittingFocus;

    /// <summary>The notch play/pause button, the tray command and the Ctrl+Shift+Space hotkey.</summary>
    [RelayCommand(CanExecute = nameof(CanToggleFocus))]
    public void ToggleFocus()
    {
        if (_focus.HasActiveSession)
        {
            var before = _focus.Current!.Status;
            _focus.Toggle();
            Diag.Write("play", "transport", ("from", before), ("to", _focus.Current?.Status));
            RefreshTimerSurface();
            RefreshRowPlayState();
            return;
        }

        // No session yet: start the first incomplete task for today, or open the panel to pick one.
        var candidate = QuickTasks.FirstOrDefault(r => !r.IsCompleted)?.Model
                        ?? TasksForToday().FirstOrDefault(t => !t.IsCompleted);

        if (candidate is null)
        {
            Overlay.Pin();
            OpenQuickView();
            return;
        }

        ApplyPlayOutcome(_focus.Play(candidate), candidate);
    }

    private bool CanStopFocus() => _focus.HasActiveSession && !IsCommittingFocus;

    /// <summary>
    /// The explicit Stop, offered in the expanded header and in the tray. It keeps every second
    /// that was actually run and records that the user is who ended it.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStopFocus))]
    public void StopFocus()
    {
        if (!_focus.Stop(SessionEndReason.StoppedByUser))
        {
            return;
        }

        Diag.Write("play", "stopped", ("reason", SessionEndReason.StoppedByUser));
        RefreshTimerSurface();
        RefreshRowPlayState();
    }

    /// <summary>The per-row Stop. Only ever ends the session belonging to that row.</summary>
    public void RequestStopFocus(TaskRowViewModel row)
    {
        if (_focus.StopFor(row.Id, SessionEndReason.StoppedByUser))
        {
            Diag.Write("play", "stopped-row", ("task", row.Id));
            RefreshTimerSurface();
            RefreshRowPlayState();
        }
    }

    /// <summary>
    /// The one entry point behind every task play button, in both views. The decision is the
    /// service's, not this method's, so quick view and planner cannot diverge.
    /// </summary>
    public void RequestStartFocus(TaskRowViewModel row)
    {
        var outcome = _focus.Play(row.Model);

        Diag.Write("play", "request", ("task", row.Id), ("outcome", outcome),
            ("sessionId", _focus.Current?.Id), ("status", _focus.Current?.Status));

        ApplyPlayOutcome(outcome, row.Model, row);
    }

    private void ApplyPlayOutcome(PlayOutcome outcome, TaskItem task, TaskRowViewModel? row = null)
    {
        switch (outcome)
        {
            case PlayOutcome.NeedsDuration:
                OpenDurationPicker(row ?? FindRow(task.Id) ?? new TaskRowViewModel(task, this));
                break;

            case PlayOutcome.NeedsSwitchConfirmation:
                _switchTarget = row ?? FindRow(task.Id) ?? new TaskRowViewModel(task, this);
                SwitchPrompt = "Switch focus to “" + Shorten(task.Title, 40) + "”?";
                Overlay.OpenOverlay(ViewModels.OverlayKind.SwitchConfirmation);
                break;

            case PlayOutcome.Ignored:
                // A duplicate press. Deliberately nothing at all.
                break;

            case PlayOutcome.Failed:
                ReportError("Could not change the focus session.");
                break;

            default:
                Overlay.CloseOverlay();
                break;
        }

        RefreshTimerSurface();
        RefreshRowPlayState();
    }

    private TaskRowViewModel? FindRow(Guid id)
        => PlannerTasks.FirstOrDefault(r => r.Id == id) ?? QuickTasks.FirstOrDefault(r => r.Id == id);

    [RelayCommand]
    private void ConfirmSwitch()
    {
        if (_switchTarget is not { } row)
        {
            CloseOverlay();
            return;
        }

        _switchTarget = null;
        Overlay.CloseOverlay();

        if (!_focus.ConfirmSwitch(row.Model))
        {
            ReportError("Could not switch the focus session.");
        }

        RefreshTimerSurface();
        RefreshRowPlayState();
    }

    /// <summary>Keeping the current session makes no change of any kind.</summary>
    [RelayCommand]
    private void CancelSwitch()
    {
        _switchTarget = null;
        Overlay.CloseOverlay();
    }

    [RelayCommand]
    private void CancelFocus()
    {
        _focus.Stop(SessionEndReason.StoppedByUser);
        RefreshTimerSurface();
        RefreshRowPlayState();
    }

    [RelayCommand]
    private void DismissCompletion()
    {
        _completedSessionTask = null;
        Overlay.CloseOverlay();
        Accent = AccentState.Idle;
        RefreshTimerSurface();
    }

    /// <summary>The completion state offers this; a finished session never auto-completes a task.</summary>
    [RelayCommand]
    private void MarkCompletedTaskDone()
    {
        if (_completedSessionTask is not null)
        {
            SetCompletion(_completedSessionTask, true);
            ReloadTasks();
            RefreshJourney("completion-card");
            RefreshStatistics("completion-card");
        }

        DismissCompletion();
    }

    // =================================================================================
    // Task operations
    // =================================================================================

    /// <summary>
    /// Ticking a task off. When the setting is on and the task being ticked is the one currently
    /// being focused, its timer stops here, running or paused, and the time already accumulated
    /// is kept. A session pointing at any other task is left completely alone.
    /// </summary>
    public void ToggleTaskCompletion(TaskRowViewModel row)
    {
        if (_mutating)
        {
            return;
        }

        _mutating = true;
        try
        {
            var completing = !row.Model.IsCompleted;
            SetCompletion(row.Model, completing);

            // The session is stopped only after the task change has actually been written, so a
            // failed save cannot leave a stopped timer next to a task that is still open.
            if (completing && StopTimerWhenTaskCompleted && row.Model.IsCompleted)
            {
                if (_focus.StopFor(row.Id, SessionEndReason.TaskCompleted))
                {
                    Diag.Write("play", "stopped-by-completion", ("task", row.Id));
                    Accent = AccentState.Idle;
                }
            }

            row.Refresh();
        }
        finally
        {
            _mutating = false;
        }

        ReloadTasks();
        RefreshTimerSurface();
        RefreshRowPlayState();
        RefreshTaskTimes("task-completion");
        RefreshJourney("task-completion");
        RefreshStatistics("task-completion");
    }

    /// <summary>
    /// Completing a task credits the day it was scheduled for, or today when it has no day.
    /// Un-completing removes the contribution outright, so the streak and the heatmap always
    /// reflect exactly what is stored rather than a running total that can drift. Marking a task
    /// incomplete never restarts a timer: the session it had is over and stays over.
    /// </summary>
    private void SetCompletion(TaskItem task, bool completed)
    {
        var previousCompleted = task.IsCompleted;
        var previousFor = task.CompletedForDate;
        var previousAt = task.CompletedAtUtc;

        task.IsCompleted = completed;
        task.CompletedAtUtc = completed ? _clock.UtcNow : null;
        task.CompletedForDate = completed ? _journey.ContributionDateFor(task.ScheduledDate) : null;
        task.UpdatedAtUtc = _clock.UtcNow;

        Diag.Write("journey", "task-completion", ("task", task.Id), ("completed", completed),
            ("for", task.CompletedForDate));

        try
        {
            _tasks.Update(task);
        }
        catch (Exception ex)
        {
            Log.Error("Could not update the task completion state.", ex);
            ReportError("Could not save the task.");

            task.IsCompleted = previousCompleted;
            task.CompletedAtUtc = previousAt;
            task.CompletedForDate = previousFor;
        }
    }

    /// <summary>Opens the inline confirmation. Nothing is removed until it is confirmed.</summary>
    public void DeleteTask(TaskRowViewModel row)
    {
        _deleteTarget = row;
        DeletePrompt = "Delete “" + Shorten(row.Title, 40) + "”?";
        Overlay.OpenOverlay(ViewModels.OverlayKind.DeleteConfirmation);
    }

    [RelayCommand]
    private void CancelDelete()
    {
        _deleteTarget = null;
        Overlay.CloseOverlay();
    }

    [RelayCommand]
    private void ConfirmDelete()
    {
        if (_deleteTarget is not { } row)
        {
            CloseOverlay();
            return;
        }

        _deleteTarget = null;
        Overlay.CloseOverlay();
        PerformDelete(row);
    }

    /// <summary>
    /// Removes the task from every list. The row itself is only marked deleted, so the sessions,
    /// runs and recorded time attached to it survive and the statistics still answer for them.
    /// </summary>
    private void PerformDelete(TaskRowViewModel row)
    {
        // Commit any earlier pending delete before starting a new undo window.
        CommitPendingDelete();

        // End any session pointing at this task first, so its final write still has a valid
        // foreign key and the run it was recording is closed rather than left open.
        _focus.CancelFor(row.Id);

        try
        {
            _tasks.Delete(row.Id);
        }
        catch (Exception ex)
        {
            Log.Error("Could not delete the task.", ex);
            ReportError("Could not delete the task.");
            return;
        }

        _pendingUndoDelete = row.Model;
        _undoExpiresAtUtc = _clock.UtcNow.AddSeconds(UndoWindowSeconds);
        UndoMessage = "Task deleted";
        IsUndoVisible = true;

        ReloadTasks();

        // Deleting a completed task removes its contribution, but not its recorded time.
        RefreshJourney("task-deleted");
        RefreshStatistics("task-deleted");
    }

    [RelayCommand]
    private void UndoDelete()
    {
        if (_pendingUndoDelete is null)
        {
            IsUndoVisible = false;
            return;
        }

        var restored = _pendingUndoDelete;
        _pendingUndoDelete = null;
        IsUndoVisible = false;

        restored.DeletedAtUtc = null;
        restored.UpdatedAtUtc = _clock.UtcNow;

        try
        {
            _tasks.Restore(restored.Id);
        }
        catch (Exception ex)
        {
            Log.Error("Could not restore the deleted task.", ex);
            ReportError("Could not restore the task.");
        }

        ReloadTasks();
        RefreshJourney("task-restored");
        RefreshStatistics("task-restored");
    }

    private void CommitPendingDelete()
    {
        _pendingUndoDelete = null;
        IsUndoVisible = false;
    }

    // ---------------------------------------------------------------------------------
    // Inline add / edit
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Opens the inline task form. The form lives in the planner, so this opens the planner:
    /// setting the draft flag while the quick view is showing used to leave the button looking
    /// broken, because there was nothing on screen to type into.
    /// </summary>
    [RelayCommand]
    public void BeginAddTask()
    {
        Overlay.Pin();
        Overlay.RequestLevel(PanelLevel.Planner, TransitionReason.Command);

        _restoringDraft = true;
        try
        {
            _editingRow = null;
            DraftHeader = "New task";
            DraftActionLabel = "Add";
            DraftTitle = string.Empty;
            DraftNote = string.Empty;
            DraftError = string.Empty;
            IsDraftCompleted = false;
        }
        finally
        {
            _restoringDraft = false;
        }

        IsAddingTask = true;
        RequestFocusNewTaskField?.Invoke();
    }

    public void BeginEditTask(TaskRowViewModel row)
    {
        OpenPlanner();

        _restoringDraft = true;
        try
        {
            _editingRow = row;
            DraftHeader = "Edit task";
            DraftActionLabel = "Save";
            DraftTitle = row.Model.Title;
            DraftNote = row.Model.Note ?? string.Empty;
            DraftError = string.Empty;
            IsDraftCompleted = row.Model.IsCompleted;
            SelectedDate = row.Model.ScheduledDate ?? SelectedDate;
        }
        finally
        {
            _restoringDraft = false;
        }

        IsAddingTask = true;
        RequestFocusNewTaskField?.Invoke();
    }

    [RelayCommand]
    public void ConfirmDraft()
    {
        var titleCheck = TaskValidator.ValidateTitle(DraftTitle);
        if (!titleCheck.IsValid)
        {
            DraftError = titleCheck.Error!;
            return;
        }

        var noteCheck = TaskValidator.ValidateNote(DraftNote);
        if (!noteCheck.IsValid)
        {
            DraftError = noteCheck.Error!;
            return;
        }

        var now = _clock.UtcNow;
        var title = DraftTitle.Trim();
        var note = string.IsNullOrWhiteSpace(DraftNote) ? null : DraftNote.Trim();
        var scheduled = Filter == PlannerFilter.Unscheduled ? (DateOnly?)null : SelectedDate;

        try
        {
            if (_editingRow is not null)
            {
                var model = _editingRow.Model;
                model.Title = title;
                model.Note = note;
                model.UpdatedAtUtc = now;

                // Moving a completed task to another day moves its contribution with it.
                var dateChanged = model.ScheduledDate != scheduled;
                model.ScheduledDate = scheduled;

                if (IsDraftCompleted)
                {
                    model.IsCompleted = true;
                    model.CompletedAtUtc ??= now;

                    if (dateChanged || model.CompletedForDate is null)
                    {
                        model.CompletedForDate = _journey.ContributionDateFor(scheduled);
                    }
                }
                else
                {
                    model.IsCompleted = false;
                    model.CompletedAtUtc = null;
                    model.CompletedForDate = null;
                }

                _tasks.Update(model);
            }
            else
            {
                var task = new TaskItem
                {
                    Title = title,
                    Note = note,
                    ScheduledDate = scheduled,
                    EstimatedSeconds = DefaultDurationSeconds,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                    SortOrder = _tasks.NextSortOrder(),
                    IsCompleted = IsDraftCompleted,
                    CompletedAtUtc = IsDraftCompleted ? now : null,
                    CompletedForDate = IsDraftCompleted ? _journey.ContributionDateFor(scheduled) : null
                };

                _tasks.Add(task);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not save the task.", ex);
            DraftError = "Could not save. See the log for details.";
            return;
        }

        CancelDraft();
        ReloadTasks();
        RefreshTaskTimes("task-saved");
        RefreshJourney("task-saved");
        RefreshStatistics("task-saved");
    }

    [RelayCommand]
    public void CancelDraft()
    {
        _restoringDraft = true;
        try
        {
            IsAddingTask = false;
            _editingRow = null;
            DraftTitle = string.Empty;
            DraftNote = string.Empty;
            DraftError = string.Empty;
            IsDraftCompleted = false;
        }
        finally
        {
            _restoringDraft = false;
        }

        // The draft is gone, so its recovery copy has to go with it.
        ClearDraftRecovery();
    }

    // ---------------------------------------------------------------------------------
    // Duration picker
    // ---------------------------------------------------------------------------------

    /// <summary>The row the duration popover is anchored to, so the view can position it.</summary>
    public TaskRowViewModel? DurationTarget => _durationTarget;

    public void OpenDurationPicker(TaskRowViewModel row)
    {
        ClearDurationPickerFlags();

        // The popover anchors to a planner row, so make sure the planner is the visible panel
        // before it is positioned, and re-point at the row that lives in that list.
        var target = row;
        if (Overlay.Level != PanelLevel.Planner)
        {
            OpenPlanner();
            target = PlannerTasks.FirstOrDefault(r => r.Id == row.Id) ?? row;
        }

        target.IsDurationPickerOpen = true;
        DurationPicker.Load(target.Model.EstimatedSeconds);
        _durationTarget = target;
        Overlay.OpenOverlay(ViewModels.OverlayKind.DurationPicker);
    }

    [RelayCommand]
    private void SaveDuration()
    {
        if (_durationTarget is null || !DurationPicker.CanStart)
        {
            return;
        }

        PersistDuration(_durationTarget, DurationPicker.TotalSeconds);
        CloseOverlay();
    }

    [RelayCommand]
    private void SaveDurationAndStart()
    {
        if (_durationTarget is null || !DurationPicker.CanStart)
        {
            return;
        }

        var row = _durationTarget;
        PersistDuration(row, DurationPicker.TotalSeconds);
        CloseOverlay();
        RequestStartFocus(row);
    }

    private void PersistDuration(TaskRowViewModel row, long seconds)
    {
        var previous = row.Model.EstimatedSeconds;
        row.Model.EstimatedSeconds = seconds;
        row.Model.UpdatedAtUtc = _clock.UtcNow;

        try
        {
            _tasks.Update(row.Model);
        }
        catch (Exception ex)
        {
            Log.Error("Could not save the focus duration.", ex);
            ReportError("Could not save the focus duration.");
            row.Model.EstimatedSeconds = previous;
        }

        row.Refresh();
        RefreshRowPlayState();
    }

    // ---------------------------------------------------------------------------------
    // Manual time
    // ---------------------------------------------------------------------------------

    /// <summary>Records work that was done without a timer, on a day the user chooses.</summary>
    public void OpenManualTime(TaskRowViewModel row)
    {
        if (Overlay.Level == PanelLevel.Collapsed)
        {
            OpenQuickView();
        }

        ManualTime.Load(row.Id, row.Title, row.Model.ScheduledDate ?? TodayDate);
        Overlay.OpenOverlay(ViewModels.OverlayKind.ManualTime);
    }

    [RelayCommand]
    private void SaveManualTime()
    {
        if (!ManualTime.CanSave)
        {
            return;
        }

        var entry = new ManualTimeEntry
        {
            Id = Guid.NewGuid(),
            TaskId = ManualTime.TaskId,
            TaskTitle = ManualTime.TaskTitle,
            LocalDate = ManualTime.Date,
            Seconds = ManualTime.TotalSeconds,
            Note = string.IsNullOrWhiteSpace(ManualTime.Note) ? null : ManualTime.Note.Trim(),
            CreatedAtUtc = _clock.UtcNow
        };

        try
        {
            _manualTime.Add(entry);
        }
        catch (Exception ex)
        {
            Log.Error("Could not save the manual time entry.", ex);
            ReportError("Could not save the time you added.");
            return;
        }

        Diag.Write("time", "manual-added", ("task", entry.TaskId), ("date", entry.LocalDate),
            ("seconds", entry.Seconds));

        Overlay.CloseOverlay();

        // A positive manual entry is a contribution, so the day it names lights up immediately.
        RefreshTaskTimes("manual-time");
        RefreshJourney("manual-time");
        RefreshStatistics("manual-time");
    }

    [RelayCommand]
    private void CancelManualTime() => Overlay.CloseOverlay();

    // =================================================================================
    // Calendar and filters
    // =================================================================================

    public void SelectDate(DateOnly date)
    {
        SelectedDate = date;
        Filter = PlannerFilter.Day;

        if (date.Year != CalendarMonth.Year || date.Month != CalendarMonth.Month)
        {
            CalendarMonth = new DateOnly(date.Year, date.Month, 1);
        }

        PersistSelectedDate();
        RebuildCalendar();
        RebuildPlannerTasks();
        OnPropertyChanged(nameof(SelectedDayLabel));
    }

    [RelayCommand]
    private void GoToToday() => SelectDate(TodayDate);

    [RelayCommand]
    private void PreviousMonth()
    {
        CalendarMonth = CalendarMonth.AddMonths(-1);
        RebuildCalendar();
    }

    [RelayCommand]
    private void NextMonth()
    {
        CalendarMonth = CalendarMonth.AddMonths(1);
        RebuildCalendar();
    }

    [RelayCommand]
    private void ShowDayFilter()
    {
        Filter = PlannerFilter.Day;
        PersistFilter();
        RebuildPlannerTasks();
    }

    [RelayCommand]
    private void ShowUnscheduledFilter()
    {
        Filter = PlannerFilter.Unscheduled;
        PersistFilter();
        RebuildPlannerTasks();
    }

    private DateOnly LoadSelectedDate()
    {
        var stored = _settings.Get(SettingKeys.LastSelectedDate);
        return DateOnly.TryParse(stored, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day
            : _clock.Today();
    }

    private void PersistSelectedDate()
    {
        try
        {
            _settings.Set(SettingKeys.LastSelectedDate, SelectedDate.ToString("yyyy-MM-dd"));
        }
        catch (Exception ex)
        {
            Log.Warn("Could not save the selected date.", ex);
        }
    }

    private void PersistFilter()
    {
        try
        {
            _settings.Set(SettingKeys.LastPlannerFilter, Filter.ToString());
        }
        catch (Exception ex)
        {
            Log.Warn("Could not save the planner filter.", ex);
        }
    }

    // =================================================================================
    // Journey
    // =================================================================================

    /// <summary>
    /// Recomputes the streak and the heatmap from what is actually stored, off the render path,
    /// and publishes the result on the UI thread. Called after every committed change that can
    /// affect activity, and never on a timer tick.
    /// </summary>
    public void RefreshJourney(string reason)
    {
        Diag.Write("journey", "refresh-requested", ("reason", reason));

        _journey.RefreshAsync(
            StreakCalculator.DefaultWeeks,
            ex =>
            {
                Log.Error("Could not rebuild the journey streak.", ex);
                ReportError("Could not read your focus history.");
            });
    }

    private void OnJourneyChanged(JourneyModel model)
    {
        StreakText = model.StreakText;

        // The control redraws from this list. Handing it a new list is one render pass; it does
        // not change the control's size, so it can never resize the panel around it.
        HeatmapCells = model.Cells;
        HeatmapToday = model.Today.ToDateTime(TimeOnly.MinValue);
        _heatmapAnchor = model.Today;

        // The statistics panel draws the same grid, larger. It reads the same list, so the two
        // cannot show different days however either of them was refreshed.
        Statistics.HeatmapCells = model.Cells;
        Statistics.Today = HeatmapToday;

        Diag.Write("journey", "refresh-applied", ("streak", model.CurrentStreak),
            ("cells", model.Cells.Count));
    }

    // =================================================================================
    // Statistics
    // =================================================================================

    public void RefreshStatistics(string reason)
    {
        Diag.Write("stats", "refresh-requested", ("reason", reason), ("range", Statistics.Range));

        _statistics.RefreshAsync(
            Statistics.Range,
            ex =>
            {
                Log.Error("Could not build the statistics.", ex);
                ReportError("Could not read your history.");
            });
    }

    private void SelectStatisticsRange(StatisticsRange range)
    {
        Statistics.Range = range;

        try
        {
            _settings.Set(SettingKeys.StatisticsRange, range.ToString());
        }
        catch (Exception ex)
        {
            Log.Warn("Could not save the statistics range.", ex);
        }

        RefreshStatistics("range-changed");
    }

    private StatisticsRange LoadStatisticsRange() =>
        Enum.TryParse<StatisticsRange>(_settings.Get(SettingKeys.StatisticsRange), out var range)
            ? range
            : StatisticsRange.Last7Days;

    private void OnStatisticsChanged(StatisticsModel model)
    {
        Statistics.Apply(model, HeatmapCells, _heatmapAnchor == DateOnly.MinValue ? TodayDate : _heatmapAnchor);

        Diag.Write("stats", "refresh-applied", ("range", model.Range),
            ("focus", model.FocusSeconds), ("buckets", model.Chart.Count));
    }

    // =================================================================================
    // Projections
    // =================================================================================

    private void ReloadTasks()
    {
        try
        {
            _allTasks.Clear();
            _allTasks.AddRange(_tasks.GetAll());
        }
        catch (Exception ex)
        {
            Log.Error("Could not load tasks.", ex);
            ReportError("Could not read your tasks. Your data file has not been changed.");
            return;
        }

        RebuildQuickTasks();
        RebuildPlannerTasks();
        RebuildCalendarTaskMarkers();
        RefreshRowPlayState();
        RefreshTimerSurface();
        ApplyTaskTimes();

        var unscheduled = _allTasks.Count(t => t.ScheduledDate is null && !t.IsCompleted);
        UnscheduledCountText = "Unscheduled " + unscheduled;
    }

    private IEnumerable<TaskItem> TasksForToday() =>
        _allTasks.Where(t => t.ScheduledDate == TodayDate);

    private void RebuildQuickTasks()
    {
        var rows = TasksForToday()
            .Where(t => !t.IsCompleted)
            .OrderBy(t => t.SortOrder)
            .ThenBy(t => t.CreatedAtUtc)
            .Take(QuickTaskLimit)
            .ToList();

        SyncRows(QuickTasks, rows);
        HasQuickTasks = QuickTasks.Count > 0;
    }

    private void RebuildPlannerTasks()
    {
        IEnumerable<TaskItem> source = Filter == PlannerFilter.Unscheduled
            ? _allTasks.Where(t => t.ScheduledDate is null)
            : _allTasks.Where(t => t.ScheduledDate == SelectedDate);

        var ordered = source
            .OrderBy(t => t.IsCompleted)
            .ThenBy(t => t.SortOrder)
            .ThenBy(t => t.CreatedAtUtc)
            .ToList();

        SyncRows(PlannerTasks, ordered);
        HasPlannerTasks = PlannerTasks.Count > 0;

        var open = ordered.Count(t => !t.IsCompleted);
        OpenCountText = open + " open";
        OnPropertyChanged(nameof(SelectedDayLabel));
    }

    /// <summary>
    /// Brings a bound collection in line with a new list by identity, in place. Rows that are
    /// still present keep their element, so the button under the pointer is not swapped out
    /// mid-gesture, and a single edit produces one or two collection events rather than one per
    /// row - which is what used to make the panel resize several times over for one change.
    /// </summary>
    private void SyncRows(ObservableCollection<TaskRowViewModel> target, IReadOnlyList<TaskItem> source)
    {
        var changed = false;

        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (source.All(t => t.Id != target[i].Id))
            {
                target.RemoveAt(i);
                changed = true;
            }
        }

        for (var i = 0; i < source.Count; i++)
        {
            var item = source[i];
            var existingIndex = IndexOf(target, item.Id);

            if (existingIndex < 0)
            {
                target.Insert(i, new TaskRowViewModel(item, this)
                {
                    PlayState = _focus.Preview(item),
                    Time = _taskTime.TryGetValue(item.Id, out var total)
                        ? total
                        : TaskTimeTotals.Empty(item.Id)
                });

                changed = true;
                continue;
            }

            target[existingIndex].Adopt(item);

            if (existingIndex != i)
            {
                target.Move(existingIndex, i);
                changed = true;
            }
        }

        if (changed)
        {
            ContentSizeChanged?.Invoke();
        }
    }

    private static int IndexOf(ObservableCollection<TaskRowViewModel> rows, Guid id)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }

    private void RebuildCalendar()
    {
        var first = new DateOnly(CalendarMonth.Year, CalendarMonth.Month, 1);
        var leading = StreakCalculator.MondayIndex(first.DayOfWeek);
        var gridStart = first.AddDays(-leading);
        var today = TodayDate;

        // The grid only has to be rebuilt when the month actually moved.
        if (CalendarDays.Count == 42 && CalendarDays[0].Date == gridStart)
        {
            RebuildCalendarTaskMarkers();
            OnPropertyChanged(nameof(CalendarMonthLabel));
            return;
        }

        CalendarDays.Clear();
        for (var i = 0; i < 42; i++)
        {
            var date = gridStart.AddDays(i);
            CalendarDays.Add(new CalendarDayViewModel(date, date.Month == CalendarMonth.Month, date == today, this)
            {
                IsSelected = date == SelectedDate
            });
        }

        RebuildCalendarTaskMarkers();
        OnPropertyChanged(nameof(CalendarMonthLabel));
    }

    private void RebuildCalendarTaskMarkers()
    {
        if (CalendarDays.Count == 0)
        {
            return;
        }

        var withTasks = _allTasks
            .Where(t => t.ScheduledDate.HasValue && !t.IsCompleted)
            .Select(t => t.ScheduledDate!.Value)
            .ToHashSet();

        foreach (var day in CalendarDays)
        {
            day.HasTasks = withTasks.Contains(day.Date);
            day.IsSelected = day.Date == SelectedDate;
        }
    }

    private static string Shorten(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";

    public void Dispose()
    {
        Overlay.TransitionAccepted -= OnPanelTransition;
        Overlay.OverlayChanged -= OnOverlayChanged;
        Overlay.PinChanged -= OnPinChanged;

        _focus.StateChanged -= OnFocusStateChanged;
        _focus.SessionCompleted -= OnFocusSessionCompleted;
        _focus.PersistenceFailed -= OnFocusPersistenceFailed;
        _focus.Committed -= OnFocusCommitted;

        _journey.Changed -= OnJourneyChanged;
        _statistics.Changed -= OnStatisticsChanged;
    }
}
