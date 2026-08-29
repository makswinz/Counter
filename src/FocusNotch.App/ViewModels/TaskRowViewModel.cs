using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FocusNotch.Core.Focus;
using FocusNotch.Core.Models;
using FocusNotch.Core.Statistics;

namespace FocusNotch.App.ViewModels;

/// <summary>
/// One task row. Rows are reused rather than rebuilt: the shell matches incoming tasks against
/// the rows already on screen by identity and updates them in place. That keeps a button the
/// pointer is currently on from being replaced underneath the gesture, and stops a single edit
/// from emitting a burst of collection changes that each ask the window to resize.
/// </summary>
public sealed partial class TaskRowViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;

    public TaskRowViewModel(TaskItem model, ShellViewModel shell)
    {
        Model = model;
        _shell = shell;
    }

    public TaskItem Model { get; private set; }

    public Guid Id => Model.Id;

    public string Title => Model.Title;

    public string? Note => Model.Note;

    public bool HasNote => !string.IsNullOrWhiteSpace(Model.Note);

    public bool IsCompleted => Model.IsCompleted;

    /// <summary>
    /// What pressing the circle will do, used as both the tooltip and the accessible name. A
    /// checkbox whose only label is its own colour is unreadable to a screen reader and
    /// ambiguous to everyone else.
    /// </summary>
    public string CompletionLabel => IsCompleted ? "Mark not complete" : "Mark complete";

    public string DurationLabel => TimeFormat.Compact(Model.EstimatedSeconds);

    public string ScheduleLabel => Model.ScheduledDate.HasValue
        ? Model.ScheduledDate.Value.ToString("d MMM", CultureInfo.InvariantCulture)
        : "Unscheduled";

    public bool IsScheduledToday => Model.ScheduledDate == _shell.TodayDate;

    public string TodayOrDateLabel => IsScheduledToday ? "Today" : ScheduleLabel;

    [ObservableProperty]
    private bool _isFocused;

    [ObservableProperty]
    private bool _isDurationPickerOpen;

    /// <summary>
    /// Actual time recorded against this task: every closed run, plus the run in progress when
    /// this is the task being focused, plus anything entered by hand. It is pushed in by the
    /// shell rather than queried here, so a running row can tick without touching the database.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpentText))]
    [NotifyPropertyChangedFor(nameof(HasSpentTime))]
    [NotifyPropertyChangedFor(nameof(DetailSummary))]
    private TaskTimeTotals _time = TaskTimeTotals.Empty(Guid.Empty);

    public string SpentText => TimeFormat.Spent(Time.TotalSeconds) + " spent";

    public bool HasSpentTime => Time.TotalSeconds > 0;

    /// <summary>
    /// The whole story of one task in a few lines, shown on the time pill: what was planned,
    /// what was actually spent, how many sessions it took and when it was last worked on.
    /// </summary>
    public string DetailSummary
    {
        get
        {
            var text = new StringBuilder();
            text.Append("Planned ").Append(TimeFormat.Compact(Model.EstimatedSeconds));
            text.Append('\n').Append("Spent ").Append(TimeFormat.Spent(Time.TotalSeconds));

            if (Time.ManualSeconds > 0)
            {
                text.Append(" (").Append(TimeFormat.Spent(Time.ManualSeconds)).Append(" added by hand)");
            }

            text.Append('\n')
                .Append(Time.SessionCount)
                .Append(Time.SessionCount == 1 ? " focus session" : " focus sessions");

            if (Time.LastFocusedUtc is { } last)
            {
                text.Append('\n').Append("Last focused ")
                    .Append(last.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture));
            }

            text.Append('\n').Append(Model.IsCompleted ? "Completed" : "Not completed");
            return text.ToString();
        }
    }

    /// <summary>
    /// What pressing play on this row would do right now, taken from the focus service rather
    /// than guessed locally, so the glyph the user sees and the action they get can never
    /// disagree.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsPause))]
    [NotifyPropertyChangedFor(nameof(IsActiveSession))]
    [NotifyPropertyChangedFor(nameof(PlayTooltip))]
    private PlayOutcome _playState = PlayOutcome.Started;

    /// <summary>True when this row is the running session, so the button shows pause.</summary>
    public bool ShowsPause => PlayState == PlayOutcome.Paused;

    /// <summary>True when this row owns the live session, running or paused, so Stop is offered.</summary>
    public bool IsActiveSession => PlayState is PlayOutcome.Paused or PlayOutcome.Resumed;

    public string PlayTooltip => PlayState switch
    {
        PlayOutcome.Paused => "Pause this focus session",
        PlayOutcome.Resumed => "Resume this focus session",
        PlayOutcome.NeedsSwitchConfirmation => "Switch focus to this task",
        PlayOutcome.NeedsDuration => "Set a focus duration",
        _ => "Start focus"
    };

    /// <summary>Swaps in an updated model without replacing the row the view is bound to.</summary>
    public void Adopt(TaskItem model)
    {
        Model = model;
        Refresh();
    }

    /// <summary>Re-reads every projection of the underlying model after an edit.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Note));
        OnPropertyChanged(nameof(HasNote));
        OnPropertyChanged(nameof(IsCompleted));
        OnPropertyChanged(nameof(CompletionLabel));
        OnPropertyChanged(nameof(DurationLabel));
        OnPropertyChanged(nameof(ScheduleLabel));
        OnPropertyChanged(nameof(IsScheduledToday));
        OnPropertyChanged(nameof(TodayOrDateLabel));
        OnPropertyChanged(nameof(DetailSummary));
        StartFocusCommand.NotifyCanExecuteChanged();
        ToggleCompleteCommand.NotifyCanExecuteChanged();
        StopFocusCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// False only while a focus transition is being committed, so a second press that arrives
    /// during the write is refused by the command as well as by the service behind it.
    /// </summary>
    private bool CanAct() => !_shell.IsCommittingFocus;

    [RelayCommand(CanExecute = nameof(CanAct))]
    private void ToggleComplete() => _shell.ToggleTaskCompletion(this);

    [RelayCommand(CanExecute = nameof(CanAct))]
    private void StartFocus() => _shell.RequestStartFocus(this);

    /// <summary>The explicit Stop. Keeps every second recorded and files it under the user.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private void StopFocus() => _shell.RequestStopFocus(this);

    [RelayCommand]
    private void Delete() => _shell.DeleteTask(this);

    [RelayCommand]
    private void OpenDurationPicker() => _shell.OpenDurationPicker(this);

    [RelayCommand]
    private void AddTime() => _shell.OpenManualTime(this);

    [RelayCommand]
    private void Edit() => _shell.BeginEditTask(this);
}
