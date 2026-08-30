using CommunityToolkit.Mvvm.Input;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Counter.Core.Focus;

namespace Counter.App.ViewModels;

/// <summary>
/// The compact "Add time" form.
///
/// Recording work after the fact is deliberately a different thing from running a timer: it has
/// its own date, its own hours and minutes, and it is stored in its own table, so nothing about
/// it can ever be mistaken for a session that actually ran.
/// </summary>
public sealed partial class ManualTimeViewModel : ObservableObject
{
    [ObservableProperty]
    private string _taskTitle = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DateLabel))]
    private DateOnly _date;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalSeconds))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    private int _hours;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalSeconds))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    private int _minutes = 30;

    [ObservableProperty]
    private string _note = string.Empty;

    public Guid? TaskId { get; private set; }

    /// <summary>
    /// Whether this is time being added or time being taken off.
    ///
    /// The same entry either way, with the sign reversed. A timer left running over lunch is the
    /// commonest way a total goes wrong, and the fix for it is the same shape as the fix for
    /// having forgotten to start one.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAdding))]
    [NotifyPropertyChangedFor(nameof(SignedSeconds))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    [NotifyPropertyChangedFor(nameof(ActionText))]
    private bool _isRemoving;

    public bool IsAdding => !IsRemoving;

    /// <summary>How much time is on the dial, unsigned.</summary>
    public long TotalSeconds => Hours * 3600L + Minutes * 60L;

    /// <summary>
    /// What is actually stored: negative when removing.
    ///
    /// Kept as an entry rather than by editing history. The measured segments are a record of
    /// what happened and are never rewritten; a correction is a separate, visible fact about the
    /// same day, which is also what makes it reversible.
    /// </summary>
    public long SignedSeconds => IsRemoving ? -TotalSeconds : TotalSeconds;

    /// <summary>How much can be taken off before the task reaches zero.</summary>
    public long AvailableSeconds { get; private set; }

    /// <summary>
    /// A zero entry is not an entry, and a total cannot be driven below nothing: taking two
    /// hours off a task with one on it would leave a negative that means nothing.
    /// </summary>
    public bool CanSave => TotalSeconds > 0 && (IsAdding || TotalSeconds <= AvailableSeconds);

    public string ActionText => IsRemoving ? "Remove" : "Add";

    public string SummaryText => (IsRemoving ? "-" : "+") + TimeFormat.Compact(TotalSeconds);

    public string DateLabel => Date.ToString("dddd d MMMM", CultureInfo.InvariantCulture);

    /// <param name="available">
    /// The time already on this task. It is the ceiling on a removal, so the dial cannot be used
    /// to invent a negative total.
    /// </param>
    public void Load(Guid? taskId, string title, DateOnly date, long available)
    {
        TaskId = taskId;
        TaskTitle = title;
        Date = date;
        AvailableSeconds = available;
        IsRemoving = false;
        Hours = 0;
        Minutes = 30;
        Note = string.Empty;

        OnPropertyChanged(nameof(AvailableSeconds));
        OnPropertyChanged(nameof(AvailableText));
        OnPropertyChanged(nameof(CanSave));
    }

    public string AvailableText => TimeFormat.Spent(AvailableSeconds) + " on this task";

    [RelayCommand]
    private void UseAdding() => IsRemoving = false;

    [RelayCommand]
    private void UseRemoving() => IsRemoving = true;

    partial void OnHoursChanged(int value)
    {
        var clamped = Math.Clamp(value, 0, 99);
        if (clamped != value)
        {
            Hours = clamped;
        }
    }

    partial void OnMinutesChanged(int value)
    {
        var clamped = Math.Clamp(value, 0, 59);
        if (clamped != value)
        {
            Minutes = clamped;
        }
    }
}
