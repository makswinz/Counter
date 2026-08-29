using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Counter.App.ViewModels;

public sealed partial class CalendarDayViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;

    public CalendarDayViewModel(DateOnly date, bool isCurrentMonth, bool isToday, ShellViewModel shell)
    {
        Date = date;
        IsCurrentMonth = isCurrentMonth;
        IsToday = isToday;
        _shell = shell;
    }

    public DateOnly Date { get; }

    public string DayNumber => Date.Day.ToString();

    public bool IsCurrentMonth { get; }

    public bool IsToday { get; }

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _hasTasks;

    public string AccessibleName => Date.ToString("dddd d MMMM yyyy", CultureInfo.InvariantCulture);

    [RelayCommand]
    private void Select() => _shell.SelectDate(Date);
}
