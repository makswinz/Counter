using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using FocusNotch.Core.Focus;

namespace FocusNotch.App.ViewModels;

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

    public long TotalSeconds => Hours * 3600L + Minutes * 60L;

    /// <summary>A zero entry is not an entry. Nothing is written unless there is time in it.</summary>
    public bool CanSave => TotalSeconds > 0;

    public string SummaryText => TimeFormat.Compact(TotalSeconds);

    public string DateLabel => Date.ToString("dddd d MMMM", CultureInfo.InvariantCulture);

    public void Load(Guid? taskId, string title, DateOnly date)
    {
        TaskId = taskId;
        TaskTitle = title;
        Date = date;
        Hours = 0;
        Minutes = 30;
        Note = string.Empty;
    }

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
