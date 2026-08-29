using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Counter.Core.Focus;
using Counter.Core.Models;
using Counter.Core.Validation;

namespace Counter.App.ViewModels;

/// <summary>One of the fixed durations offered above the columns.</summary>
public sealed record DurationPreset(string Label, long Seconds);

/// <summary>
/// Backs the compact "Focus duration" popover anchored to a task row.
///
/// Three independent columns: hours 0 to 99, minutes 0 to 59, seconds 0 to 59. Each one clamps
/// to its own range and never carries into its neighbour. Wrapping looks clever and is horrible
/// to use: pressing up on 59 seconds and watching the minutes change is exactly the kind of
/// thing that makes somebody re-check a value they had already set correctly.
/// </summary>
public sealed partial class DurationPickerViewModel : ObservableObject
{
    /// <summary>Seconds step in fives; hours and minutes step by one.</summary>
    private const int SecondStep = 5;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalSeconds))]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyPropertyChangedFor(nameof(ValidationMessage))]
    [NotifyPropertyChangedFor(nameof(HasValidationMessage))]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    private int _hours;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalSeconds))]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyPropertyChangedFor(nameof(ValidationMessage))]
    [NotifyPropertyChangedFor(nameof(HasValidationMessage))]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    private int _minutes = (int)(FocusDefaults.DefaultSeconds / 60);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalSeconds))]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyPropertyChangedFor(nameof(ValidationMessage))]
    [NotifyPropertyChangedFor(nameof(HasValidationMessage))]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    private int _seconds;

    /// <summary>25m, 45m, 1h, 2h. One press fills all three columns.</summary>
    public IReadOnlyList<DurationPreset> Presets { get; } =
        FocusDefaults.Presets.Select(p => new DurationPreset(p.Label, p.Seconds)).ToList();

    public long TotalSeconds => Hours * 3600L + Minutes * 60L + Seconds;

    public bool CanStart => TaskValidator.ValidateDuration(TotalSeconds).IsValid;

    public string? ValidationMessage => TaskValidator.ValidateDuration(TotalSeconds).Error;

    public bool HasValidationMessage => !string.IsNullOrEmpty(ValidationMessage);

    /// <summary>The chosen value read back in words, so the columns are never ambiguous.</summary>
    public string SummaryText => TimeFormat.Compact(TotalSeconds);

    /// <summary>Loads an existing duration, so reopening the picker preserves what was set.</summary>
    public void Load(long totalSeconds)
    {
        totalSeconds = Math.Clamp(totalSeconds, 0, FocusDefaults.MaxSeconds);
        Hours = (int)(totalSeconds / 3600);
        Minutes = (int)(totalSeconds % 3600 / 60);
        Seconds = (int)(totalSeconds % 60);
    }

    partial void OnHoursChanged(int value)
    {
        var clamped = Math.Clamp(value, 0, FocusDefaults.MaxHours);
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

    partial void OnSecondsChanged(int value)
    {
        var clamped = Math.Clamp(value, 0, 59);
        if (clamped != value)
        {
            Seconds = clamped;
        }
    }

    [RelayCommand]
    private void IncrementHours() => Hours = Math.Min(FocusDefaults.MaxHours, Hours + 1);

    [RelayCommand]
    private void DecrementHours() => Hours = Math.Max(0, Hours - 1);

    [RelayCommand]
    private void IncrementMinutes() => Minutes = Math.Min(59, Minutes + 1);

    [RelayCommand]
    private void DecrementMinutes() => Minutes = Math.Max(0, Minutes - 1);

    [RelayCommand]
    private void IncrementSeconds() => Seconds = Math.Min(59, Seconds + SecondStep);

    [RelayCommand]
    private void DecrementSeconds() => Seconds = Math.Max(0, Seconds - SecondStep);

    [RelayCommand]
    private void ApplyPreset(DurationPreset? preset)
    {
        if (preset is not null)
        {
            Load(preset.Seconds);
        }
    }
}
