using FocusNotch.Core.Journey;

namespace FocusNotch.Core.Statistics;

/// <summary>The date filter above the statistics panel.</summary>
public enum StatisticsRange
{
    Today = 0,
    Last7Days = 1,
    Last30Days = 2,
    AllTime = 3
}

/// <summary>How the activity chart is aggregated, chosen from how long the range actually is.</summary>
public enum BucketKind
{
    Hour,
    Day,
    Week,
    Month
}

/// <summary>One bar of the activity chart.</summary>
public sealed record StatBucket(
    string Label,
    string AccessibleLabel,
    DateOnly? Date,
    long FocusSeconds,
    long ManualSeconds)
{
    public long TotalSeconds => FocusSeconds + ManualSeconds;
}

/// <summary>
/// One row of the top-tasks list.
///
/// Only live tasks are ever listed. A deleted task keeps its hours in the totals - the time was
/// spent whatever happened to the task afterwards - but it is not a thing you can look at any
/// more, and a list of what you have been working on should not name something you removed.
/// </summary>
/// <param name="TaskId">Null when the work was never attached to a task.</param>
public sealed record TopTask(
    Guid? TaskId,
    string Title,
    long FocusSeconds,
    long ManualSeconds,
    bool IsCompleted,
    double Share)
{
    public long TotalSeconds => FocusSeconds + ManualSeconds;
}

/// <summary>
/// A day that stands out, with the number that made it stand out.
///
/// Null when the range holds nothing to be best at, so the panel can say so rather than print a
/// zero next to a date that means nothing.
/// </summary>
public sealed record StatHighlight(DateOnly? Date, long Value)
{
    public static readonly StatHighlight None = new(null, 0);

    public bool HasValue => Date is not null && Value > 0;
}

/// <summary>The weekday you work most, and how much of it lands there.</summary>
public sealed record WeekdayHighlight(DayOfWeek? Day, long Seconds)
{
    public static readonly WeekdayHighlight None = new(null, 0);

    public bool HasValue => Day is not null && Seconds > 0;
}

/// <summary>
/// One immutable statistics snapshot. Built entirely from persisted rows plus, for the range
/// that includes today, the run currently in progress, so the totals on screen agree with the
/// history without anything being written to the database every second.
/// </summary>
public sealed class StatisticsModel
{
    public static readonly StatisticsModel Empty = new(
        StatisticsRange.Last7Days,
        default,
        BucketKind.Day,
        0, 0, 0, 0, 0, 0, 0, 0, 0,
        Array.Empty<StatBucket>(),
        Array.Empty<TopTask>(),
        0, 0, 0, 0,
        StatHighlight.None,
        StatHighlight.None,
        WeekdayHighlight.None);

    public StatisticsModel(
        StatisticsRange range,
        ActivityWindow window,
        BucketKind buckets,
        long focusSeconds,
        long manualSeconds,
        int tasksCompleted,
        int sessionsCompleted,
        int currentStreak,
        int longestStreak,
        long averageSessionSeconds,
        int tasksScheduled,
        int tasksScheduledCompleted,
        IReadOnlyList<StatBucket> chart,
        IReadOnlyList<TopTask> topTasks,
        int tasksWorked,
        long averageTaskSeconds,
        int activeDays,
        long longestSessionSeconds,
        StatHighlight bestDay,
        StatHighlight busiestDay,
        WeekdayHighlight busiestWeekday)
    {
        Range = range;
        Window = window;
        BucketKind = buckets;
        FocusSeconds = focusSeconds;
        ManualSeconds = manualSeconds;
        TasksCompleted = tasksCompleted;
        SessionsCompleted = sessionsCompleted;
        CurrentStreak = currentStreak;
        LongestStreak = longestStreak;
        AverageSessionSeconds = averageSessionSeconds;
        TasksScheduled = tasksScheduled;
        TasksScheduledCompleted = tasksScheduledCompleted;
        Chart = chart;
        TopTasks = topTasks;
        TasksWorked = tasksWorked;
        AverageTaskSeconds = averageTaskSeconds;
        ActiveDays = activeDays;
        LongestSessionSeconds = longestSessionSeconds;
        BestDay = bestDay;
        BusiestDay = busiestDay;
        BusiestWeekday = busiestWeekday;
    }

    public StatisticsRange Range { get; }

    public ActivityWindow Window { get; }

    public BucketKind BucketKind { get; }

    /// <summary>Time actually recorded by the timer.</summary>
    public long FocusSeconds { get; }

    /// <summary>Time entered by hand. Kept apart so it can never be counted twice.</summary>
    public long ManualSeconds { get; }

    public long TotalSeconds => FocusSeconds + ManualSeconds;

    public int TasksCompleted { get; }

    public int SessionsCompleted { get; }

    public int CurrentStreak { get; }

    public int LongestStreak { get; }

    public long AverageSessionSeconds { get; }

    /// <summary>Tasks whose scheduled day falls inside the range.</summary>
    public int TasksScheduled { get; }

    public int TasksScheduledCompleted { get; }

    /// <summary>
    /// Completed over scheduled, in the range 0..1. A range with no scheduled tasks is zero
    /// rather than undefined, so nothing downstream has to guard against a division.
    /// </summary>
    public double CompletionRate =>
        TasksScheduled <= 0 ? 0d : (double)TasksScheduledCompleted / TasksScheduled;

    public IReadOnlyList<StatBucket> Chart { get; }

    public IReadOnlyList<TopTask> TopTasks { get; }

    // ==================================================================== the shape of the range

    /// <summary>
    /// How many live tasks had any time put into them.
    ///
    /// The denominator of the average below, and worth showing on its own: four hours across two
    /// tasks and four hours across eleven are very different days.
    /// </summary>
    public int TasksWorked { get; }

    /// <summary>Time per task actually worked on. Zero rather than undefined when there are none.</summary>
    public long AverageTaskSeconds { get; }

    /// <summary>Days in the range with any recorded time at all.</summary>
    public int ActiveDays { get; }

    /// <summary>
    /// Time per day you actually worked, rather than per day on the calendar.
    ///
    /// Averaging over the whole range punishes you for the days you were not at the desk, which
    /// makes the number say more about how the range was chosen than about how you worked.
    /// </summary>
    public long AverageActiveDaySeconds => ActiveDays <= 0 ? 0 : TotalSeconds / ActiveDays;

    /// <summary>The longest single unbroken run in the range.</summary>
    public long LongestSessionSeconds { get; }

    /// <summary>The day with the most recorded time, and how much.</summary>
    public StatHighlight BestDay { get; }

    /// <summary>The day the most tasks were completed, and how many.</summary>
    public StatHighlight BusiestDay { get; }

    /// <summary>The weekday that carries the most of your time across the whole range.</summary>
    public WeekdayHighlight BusiestWeekday { get; }

    /// <summary>True when the range holds nothing at all, so the panel can say so plainly.</summary>
    public bool IsEmpty =>
        TotalSeconds == 0 && TasksCompleted == 0 && SessionsCompleted == 0 && TopTasks.Count == 0;

    /// <summary>The tallest bar, used to scale the chart. Never zero, so nothing divides by it.</summary>
    public long PeakBucketSeconds
    {
        get
        {
            long peak = 0;
            foreach (var bucket in Chart)
            {
                if (bucket.TotalSeconds > peak)
                {
                    peak = bucket.TotalSeconds;
                }
            }

            return peak;
        }
    }
}
