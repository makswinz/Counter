using Counter.Core.Journey;
using Counter.Core.Models;

namespace Counter.Core.Streaks;

/// <summary>
/// Everything about the journey streak is derived from what is persisted. Nothing here
/// maintains a counter that could drift out of sync with the stored data.
/// </summary>
public static class StreakCalculator
{
    public const int DefaultWeeks = 12;

    /// <summary>A productive day is a local calendar day with at least one completed session.</summary>
    public static IReadOnlyDictionary<DateOnly, int> CountByLocalDay(
        IEnumerable<DateTime> completedAtUtc,
        TimeZoneInfo timeZone)
    {
        var counts = new Dictionary<DateOnly, int>();

        foreach (var utc in completedAtUtc)
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(utc, DateTimeKind.Utc), timeZone);
            var day = DateOnly.FromDateTime(local);
            counts[day] = counts.TryGetValue(day, out var existing) ? existing + 1 : 1;
        }

        return counts;
    }

    public static IReadOnlyDictionary<DateOnly, int> CountByLocalDay(
        IEnumerable<FocusSession> sessions,
        TimeZoneInfo timeZone)
        => CountByLocalDay(
            sessions
                .Where(s => s.Status == FocusSessionStatus.Completed && s.CompletedAtUtc.HasValue)
                .Select(s => s.CompletedAtUtc!.Value),
            timeZone);

    /// <summary>
    /// Counts contributions that already carry a local calendar date. One entry in
    /// <paramref name="dates"/> is one contribution, so a day with a completed task and a
    /// completed focus session counts as two.
    /// </summary>
    public static IReadOnlyDictionary<DateOnly, int> CountByDate(params IEnumerable<DateOnly>[] dates)
    {
        var counts = new Dictionary<DateOnly, int>();

        foreach (var source in dates)
        {
            foreach (var day in source)
            {
                counts[day] = counts.TryGetValue(day, out var existing) ? existing + 1 : 1;
            }
        }

        return counts;
    }

    /// <summary>Reduces a day-by-day activity map to plain contribution counts.</summary>
    public static IReadOnlyDictionary<DateOnly, int> CountByDay(
        IReadOnlyDictionary<DateOnly, DayActivity> activity)
    {
        var counts = new Dictionary<DateOnly, int>(activity.Count);

        foreach (var (day, value) in activity)
        {
            if (value.Contributions > 0)
            {
                counts[day] = value.Contributions;
            }
        }

        return counts;
    }

    /// <summary>
    /// Consecutive productive days ending today, or ending yesterday when today has no
    /// contribution yet, so a live streak is not reported as broken before the day is actually
    /// over. A day in the future never contributes, however it was recorded.
    /// </summary>
    public static int CurrentStreak(IReadOnlyDictionary<DateOnly, int> countsByDay, DateOnly today)
    {
        DateOnly cursor;

        if (countsByDay.TryGetValue(today, out var todayCount) && todayCount > 0)
        {
            cursor = today;
        }
        else
        {
            var yesterday = today.AddDays(-1);
            if (countsByDay.TryGetValue(yesterday, out var yesterdayCount) && yesterdayCount > 0)
            {
                cursor = yesterday;
            }
            else
            {
                return 0;
            }
        }

        var streak = 0;
        while (countsByDay.TryGetValue(cursor, out var count) && count > 0)
        {
            streak++;
            cursor = cursor.AddDays(-1);
        }

        return streak;
    }

    /// <summary>
    /// The longest run of consecutive productive days anywhere in the record, ignoring days in
    /// the future so a task dated ahead cannot invent a streak that has not been lived yet.
    /// </summary>
    public static int LongestStreak(IReadOnlyDictionary<DateOnly, int> countsByDay, DateOnly today)
    {
        var days = countsByDay
            .Where(pair => pair.Value > 0 && pair.Key <= today)
            .Select(pair => pair.Key)
            .OrderBy(day => day)
            .ToList();

        if (days.Count == 0)
        {
            return 0;
        }

        var longest = 1;
        var run = 1;

        for (var i = 1; i < days.Count; i++)
        {
            run = days[i] == days[i - 1].AddDays(1) ? run + 1 : 1;

            if (run > longest)
            {
                longest = run;
            }
        }

        return longest;
    }

    /// <summary>0 contributions maps to empty, then levels 1..4 with 4 or more saturating.</summary>
    public static int Intensity(int contributions) => contributions switch
    {
        <= 0 => 0,
        1 => 1,
        2 => 2,
        3 => 3,
        _ => 4
    };

    /// <summary>
    /// A contribution-style grid of <paramref name="weeks"/> columns by 7 rows, Monday first,
    /// with the column containing <paramref name="today"/> last.
    /// </summary>
    public static IReadOnlyList<HeatmapCell> BuildHeatmap(
        IReadOnlyDictionary<DateOnly, DayActivity> activityByDay,
        DateOnly today,
        int weeks = DefaultWeeks)
    {
        if (weeks < 1)
        {
            weeks = 1;
        }

        var mondayOfCurrentWeek = today.AddDays(-MondayIndex(today.DayOfWeek));
        var start = mondayOfCurrentWeek.AddDays(-7 * (weeks - 1));

        var cells = new List<HeatmapCell>(weeks * 7);
        for (var week = 0; week < weeks; week++)
        {
            for (var row = 0; row < 7; row++)
            {
                var date = start.AddDays(week * 7 + row);

                if (!activityByDay.TryGetValue(date, out var activity))
                {
                    activity = DayActivity.Empty(date);
                }

                cells.Add(new HeatmapCell(date, activity, Intensity(activity.Contributions), week, row, date > today));
            }
        }

        return cells;
    }

    /// <summary>
    /// The same grid from plain contribution counts, for callers that have nothing more to say
    /// about a day than how many things happened on it.
    /// </summary>
    public static IReadOnlyList<HeatmapCell> BuildHeatmap(
        IReadOnlyDictionary<DateOnly, int> countsByDay,
        DateOnly today,
        int weeks = DefaultWeeks)
    {
        var activity = new Dictionary<DateOnly, DayActivity>(countsByDay.Count);

        foreach (var (day, count) in countsByDay)
        {
            // Nothing here knows whether a contribution was a task, a session or a hand-entered
            // block, so they are all reported as completed tasks: the count is what matters and
            // no detail is invented.
            activity[day] = new DayActivity(day, count, 0, 0, 0, 0);
        }

        return BuildHeatmap(activity, today, weeks);
    }

    /// <summary>Monday = 0 through Sunday = 6.</summary>
    public static int MondayIndex(DayOfWeek dayOfWeek) => ((int)dayOfWeek + 6) % 7;
}
