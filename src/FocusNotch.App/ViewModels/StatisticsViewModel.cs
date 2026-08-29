using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FocusNotch.Core.Focus;
using FocusNotch.Core.Statistics;
using FocusNotch.Core.Streaks;

namespace FocusNotch.App.ViewModels;

/// <summary>One row of the top-tasks list, ready to bind.</summary>
public sealed partial class TopTaskViewModel : ObservableObject
{
    public TopTaskViewModel(TopTask model) => _model = model;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    [NotifyPropertyChangedFor(nameof(TimeText))]
    [NotifyPropertyChangedFor(nameof(ManualText))]
    [NotifyPropertyChangedFor(nameof(HasManual))]
    [NotifyPropertyChangedFor(nameof(ShareText))]
    [NotifyPropertyChangedFor(nameof(SharePercent))]
    [NotifyPropertyChangedFor(nameof(IsCompleted))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private TopTask _model;

    public string Title => Model.Title;

    public string TimeText => TimeFormat.Spent(Model.TotalSeconds);

    public string ManualText => TimeFormat.Spent(Model.ManualSeconds) + " added by hand";

    public bool HasManual => Model.ManualSeconds > 0;

    public double SharePercent => Math.Clamp(Model.Share, 0d, 1d) * 100d;

    public string ShareText => SharePercent.ToString("0", CultureInfo.InvariantCulture) + "%";

    public bool IsCompleted => Model.IsCompleted;

    public string StatusText => Model.IsCompleted ? "Completed" : "Open";

    public void Update(TopTask model) => Model = model;
}

/// <summary>
/// The statistics panel.
///
/// It renders a snapshot and nothing else: every number here came out of
/// <see cref="StatisticsCalculator"/> in one pass, so what the summary says, what the chart
/// draws and what the top-task list ranks can never disagree with each other. Rows are updated
/// in place rather than rebuilt, so a refresh does not change the panel's height and cannot
/// make it resize while it is on screen.
/// </summary>
public sealed partial class StatisticsViewModel : ObservableObject
{
    public StatisticsViewModel()
    {
        Ranges = new[]
        {
            StatisticsRange.Today,
            StatisticsRange.Last7Days,
            StatisticsRange.Last30Days,
            StatisticsRange.AllTime
        };
    }

    public IReadOnlyList<StatisticsRange> Ranges { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsToday))]
    [NotifyPropertyChangedFor(nameof(IsLast7))]
    [NotifyPropertyChangedFor(nameof(IsLast30))]
    [NotifyPropertyChangedFor(nameof(IsAllTime))]
    [NotifyPropertyChangedFor(nameof(RangeLabel))]
    private StatisticsRange _range = StatisticsRange.Last7Days;

    public bool IsToday => Range == StatisticsRange.Today;

    public bool IsLast7 => Range == StatisticsRange.Last7Days;

    public bool IsLast30 => Range == StatisticsRange.Last30Days;

    public bool IsAllTime => Range == StatisticsRange.AllTime;

    public string RangeLabel => StatisticsService.Label(Range);

    // ---------------------------------------------------------------------------------
    // Summary
    // ---------------------------------------------------------------------------------

    [ObservableProperty]
    private string _totalFocusText = "0m";

    [ObservableProperty]
    private string _manualText = string.Empty;

    [ObservableProperty]
    private bool _hasManualTime;

    [ObservableProperty]
    private string _tasksCompletedText = "0";

    [ObservableProperty]
    private string _sessionsText = "0";

    [ObservableProperty]
    private string _currentStreakText = "0d";

    [ObservableProperty]
    private string _longestStreakText = "0d";

    [ObservableProperty]
    private string _averageSessionText = "0m";

    [ObservableProperty]
    private string _completionRateText = "0%";

    [ObservableProperty]
    private string _completionRateDetail = "no tasks scheduled";

    // ---------------------------------------------------------------------------------
    // How the range was actually shaped
    // ---------------------------------------------------------------------------------

    /// <summary>Time per task worked on, and how many that was.</summary>
    [ObservableProperty]
    private string _averageTaskText = "0m";

    [ObservableProperty]
    private string _averageTaskDetail = "no tasks worked on";

    /// <summary>Time per day you actually worked, rather than per day on the calendar.</summary>
    [ObservableProperty]
    private string _averageDayText = "0m";

    [ObservableProperty]
    private string _averageDayDetail = "no active days";

    /// <summary>The single best day in the range, and how much went into it.</summary>
    [ObservableProperty]
    private string _bestDayText = "-";

    [ObservableProperty]
    private string _bestDayDetail = "nothing recorded yet";

    /// <summary>The weekday that carries the most of your time.</summary>
    [ObservableProperty]
    private string _bestWeekdayText = "-";

    [ObservableProperty]
    private string _bestWeekdayDetail = "not enough history";

    /// <summary>The day the most tasks were finished.</summary>
    [ObservableProperty]
    private string _mostTasksText = "-";

    [ObservableProperty]
    private string _mostTasksDetail = "no tasks completed";

    /// <summary>The longest unbroken run in the range.</summary>
    [ObservableProperty]
    private string _longestSessionText = "0m";

    [ObservableProperty]
    private bool _isEmpty = true;

    /// <summary>The twelve-week grid, drawn larger here by the same control the quick view uses.</summary>
    [ObservableProperty]
    private IReadOnlyList<HeatmapCell> _heatmapCells = Array.Empty<HeatmapCell>();

    [ObservableProperty]
    private DateTime _today;

    [ObservableProperty]
    private IReadOnlyList<StatBucket> _chart = Array.Empty<StatBucket>();

    public ObservableCollection<TopTaskViewModel> TopTasks { get; } = new();

    [ObservableProperty]
    private bool _hasTopTasks;

    /// <summary>Set by the shell so the range pills can raise a command without a back-pointer.</summary>
    public Action<StatisticsRange>? RangeRequested { get; set; }

    [RelayCommand]
    private void SelectToday() => RangeRequested?.Invoke(StatisticsRange.Today);

    [RelayCommand]
    private void SelectLast7() => RangeRequested?.Invoke(StatisticsRange.Last7Days);

    [RelayCommand]
    private void SelectLast30() => RangeRequested?.Invoke(StatisticsRange.Last30Days);

    [RelayCommand]
    private void SelectAllTime() => RangeRequested?.Invoke(StatisticsRange.AllTime);

    /// <summary>
    /// Applies a snapshot. The top-task rows are matched by identity and updated in place, so a
    /// refresh replaces text rather than the elements themselves.
    /// </summary>
    public void Apply(StatisticsModel model, IReadOnlyList<HeatmapCell> cells, DateOnly today)
    {
        Range = model.Range;

        TotalFocusText = TimeFormat.Spent(model.TotalSeconds);
        ManualText = TimeFormat.Spent(model.ManualSeconds) + " added by hand";
        HasManualTime = model.ManualSeconds > 0;

        TasksCompletedText = model.TasksCompleted.ToString(CultureInfo.InvariantCulture);
        SessionsText = model.SessionsCompleted.ToString(CultureInfo.InvariantCulture);
        CurrentStreakText = model.CurrentStreak + "d";
        LongestStreakText = model.LongestStreak + "d";
        AverageSessionText = TimeFormat.Spent(model.AverageSessionSeconds);

        CompletionRateText = model.TasksScheduled == 0
            ? "-"
            : (model.CompletionRate * 100).ToString("0", CultureInfo.InvariantCulture) + "%";

        CompletionRateDetail = model.TasksScheduled == 0
            ? "no tasks scheduled"
            : model.TasksScheduledCompleted + " of " + model.TasksScheduled + " scheduled";

        AverageTaskText = TimeFormat.Spent(model.AverageTaskSeconds);
        AverageTaskDetail = model.TasksWorked switch
        {
            0 => "no tasks worked on",
            1 => "across 1 task",
            _ => "across " + model.TasksWorked + " tasks"
        };

        AverageDayText = TimeFormat.Spent(model.AverageActiveDaySeconds);
        AverageDayDetail = model.ActiveDays switch
        {
            0 => "no active days",
            1 => "across 1 active day",
            _ => "across " + model.ActiveDays + " active days"
        };

        BestDayText = model.BestDay.HasValue
            ? TimeFormat.Spent(model.BestDay.Value)
            : "-";

        BestDayDetail = model.BestDay.Date is { } best
            ? best.ToString("ddd d MMM", CultureInfo.CurrentCulture)
            : "nothing recorded yet";

        BestWeekdayText = model.BusiestWeekday.Day is { } weekday
            ? CultureInfo.CurrentCulture.DateTimeFormat.GetDayName(weekday)
            : "-";

        BestWeekdayDetail = model.BusiestWeekday.HasValue
            ? TimeFormat.Spent(model.BusiestWeekday.Seconds) + " in total"
            : "not enough history";

        MostTasksText = model.BusiestDay.HasValue
            ? model.BusiestDay.Value.ToString(CultureInfo.InvariantCulture)
            : "-";

        MostTasksDetail = model.BusiestDay.Date is { } busiest
            ? "on " + busiest.ToString("ddd d MMM", CultureInfo.CurrentCulture)
            : "no tasks completed";

        LongestSessionText = TimeFormat.Spent(model.LongestSessionSeconds);

        Chart = model.Chart;
        HeatmapCells = cells;
        Today = today.ToDateTime(TimeOnly.MinValue);
        IsEmpty = model.IsEmpty;

        SyncTopTasks(model.TopTasks);
    }

    private void SyncTopTasks(IReadOnlyList<TopTask> rows)
    {
        for (var i = TopTasks.Count - 1; i >= rows.Count; i--)
        {
            TopTasks.RemoveAt(i);
        }

        for (var i = 0; i < rows.Count; i++)
        {
            if (i < TopTasks.Count)
            {
                TopTasks[i].Update(rows[i]);
            }
            else
            {
                TopTasks.Add(new TopTaskViewModel(rows[i]));
            }
        }

        HasTopTasks = TopTasks.Count > 0;
    }
}
