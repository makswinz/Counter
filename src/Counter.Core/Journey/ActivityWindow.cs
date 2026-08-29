namespace Counter.Core.Journey;

/// <summary>The inclusive local-date range the journey surface reads.</summary>
public readonly record struct ActivityWindow(DateOnly From, DateOnly To)
{
    /// <summary>
    /// The window the heatmap needs, plus a year of slack before it so a streak that started
    /// before the first visible column is still counted at its true length.
    /// </summary>
    public static ActivityWindow ForHeatmap(DateOnly today, int weeks)
    {
        var mondayOfCurrentWeek = today.AddDays(-Streaks.StreakCalculator.MondayIndex(today.DayOfWeek));
        var firstVisible = mondayOfCurrentWeek.AddDays(-7 * (weeks - 1));

        // Future days are read too: a completed task dated ahead must be stored and shown,
        // even though it cannot extend the streak that ends today.
        return new ActivityWindow(firstVisible.AddDays(-370), today.AddDays(7 * weeks));
    }

    /// <summary>The window a statistics range needs, given today.</summary>
    public static ActivityWindow ForRange(DateOnly today, Statistics.StatisticsRange range) => range switch
    {
        Statistics.StatisticsRange.Today => new ActivityWindow(today, today),
        Statistics.StatisticsRange.Last7Days => new ActivityWindow(today.AddDays(-6), today),
        Statistics.StatisticsRange.Last30Days => new ActivityWindow(today.AddDays(-29), today),
        // "All time" still needs a bound for the query. Twenty years is far past any plausible
        // history for a local productivity app and keeps the range arithmetic finite.
        _ => new ActivityWindow(today.AddYears(-20), today)
    };

    public int DayCount => To.DayNumber - From.DayNumber + 1;

    public bool Contains(DateOnly day) => day >= From && day <= To;

    public IEnumerable<DateOnly> Days()
    {
        for (var day = From; day <= To; day = day.AddDays(1))
        {
            yield return day;
        }
    }
}
