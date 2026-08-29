using System.Globalization;
using Counter.Core.Focus;
using Counter.Core.Journey;
using Counter.Core.Models;
using Counter.Core.Streaks;

namespace Counter.Core.Statistics;

/// <summary>
/// Turns raw history into the statistics panel.
///
/// Everything is a pure function of the snapshot, the range and an instant, so the whole surface
/// can be asserted without a database and without waiting for a clock. Running time is split at
/// local midnight before anything is bucketed, so a session that crossed midnight lands on both
/// days rather than on whichever one it started in.
/// </summary>
public static class StatisticsCalculator
{
    /// <summary>How many rows the top-tasks list shows.</summary>
    public const int TopTaskCount = 5;

    public static StatisticsModel Build(
        ActivitySnapshot snapshot,
        StatisticsRange range,
        DateOnly today,
        DateTime nowUtc,
        TimeZoneInfo zone)
    {
        var window = ActivityWindow.ForRange(today, range);

        var targets = snapshot.Sessions.ToDictionary(s => s.Id, JourneyActivityService.TargetOf);
        var allSpans = TimeLedger.ToSpans(
            snapshot.Segments,
            nowUtc,
            sessionId => targets.TryGetValue(sessionId, out var target) ? target : null);

        // Clip every run to the window in local time, so a session that started before the range
        // only contributes the part that falls inside it.
        var spans = ClipToWindow(allSpans, window, zone);

        var manual = snapshot.ManualEntries
            .Where(entry => entry.Seconds > 0 && window.Contains(entry.LocalDate))
            .ToList();

        var focusByDay = TimeLedger.SecondsByLocalDay(spans, zone);
        var manualByDay = TimeLedger.ManualSecondsByLocalDay(manual);

        var focusSeconds = TimeLedger.TotalSeconds(spans);
        var manualSeconds = manual.Sum(entry => entry.Seconds);

        var tasksCompleted = snapshot.Tasks.Count(
            task => task is { IsCompleted: true, IsDeleted: false }
                    && task.CompletedForDate is { } day && window.Contains(day));

        var sessionsCompleted = snapshot.Sessions.Count(
            session => session.Status == FocusSessionStatus.Completed
                       && session.CompletedForDate is { } day && window.Contains(day));

        // A session counts towards the average when it actually ran inside the range.
        var sessionsWithTime = spans.Select(span => span.SessionId).Distinct().Count();
        var averageSessionSeconds = sessionsWithTime == 0 ? 0 : focusSeconds / sessionsWithTime;

        var scheduled = snapshot.Tasks
            .Where(task => !task.IsDeleted && task.ScheduledDate is { } day && window.Contains(day))
            .ToList();

        var (buckets, kind) = BuildChart(range, window, focusByDay, manualByDay, spans, today, zone);

        var streakDays = JourneyActivityService.BuildDays(snapshot, nowUtc, zone);
        var counts = StreakCalculator.CountByDay(streakDays);

        var topTasks = BuildTopTasks(snapshot, spans, manual, focusSeconds + manualSeconds);

        // Per task, counting only tasks that still exist. Time on a deleted task stays in the
        // total but cannot be divided by a task that is not there.
        var live = new HashSet<Guid>(
            snapshot.Tasks.Where(task => !task.IsDeleted).Select(task => task.Id));

        var workedTasks = new HashSet<Guid>(
            TimeLedger.SecondsByTask(spans).Where(pair => pair.Value > 0).Select(pair => pair.Key));

        workedTasks.UnionWith(
            manual.Where(entry => entry.TaskId is { } id && live.Contains(id))
                  .Select(entry => entry.TaskId!.Value));

        workedTasks.IntersectWith(live);

        var taskSeconds = topTasks.Sum(row => row.TotalSeconds);
        var averageTaskSeconds = workedTasks.Count == 0 ? 0 : taskSeconds / workedTasks.Count;

        // A day counts as active when anything at all was recorded on it, from either source.
        var perDay = new Dictionary<DateOnly, long>();

        foreach (var (day, seconds) in focusByDay)
        {
            perDay[day] = perDay.GetValueOrDefault(day) + seconds;
        }

        foreach (var (day, seconds) in manualByDay)
        {
            perDay[day] = perDay.GetValueOrDefault(day) + seconds;
        }

        var activeDays = perDay.Count(pair => pair.Value > 0);

        var bestDay = perDay.Count == 0
            ? StatHighlight.None
            : perDay.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key)
                    .Select(pair => new StatHighlight(pair.Key, pair.Value)).First();

        // Which weekday carries the most, summed across every week in the range. Over a short
        // range this is simply the best day again, which is honest: one Tuesday is all the
        // evidence there is.
        var byWeekday = new long[7];

        foreach (var (day, seconds) in perDay)
        {
            byWeekday[(int)day.DayOfWeek] += seconds;
        }

        var busiestWeekday = WeekdayHighlight.None;

        for (var index = 0; index < byWeekday.Length; index++)
        {
            if (byWeekday[index] > busiestWeekday.Seconds)
            {
                busiestWeekday = new WeekdayHighlight((DayOfWeek)index, byWeekday[index]);
            }
        }

        // The day the most tasks were finished. Completions, not time: a day of many small wins
        // is a different kind of good day from a day of one long stretch.
        var completionsByDay = snapshot.Tasks
            .Where(task => task is { IsCompleted: true, IsDeleted: false }
                           && task.CompletedForDate is { } day && window.Contains(day))
            .GroupBy(task => task.CompletedForDate!.Value)
            .ToList();

        var busiestDay = completionsByDay.Count == 0
            ? StatHighlight.None
            : completionsByDay.OrderByDescending(group => group.Count()).ThenBy(group => group.Key)
                              .Select(group => new StatHighlight(group.Key, group.Count())).First();

        // The longest unbroken run. Spans are already clipped to the window, so a run that
        // started before the range is measured by the part that falls inside it.
        long longestSession = 0;

        foreach (var group in spans.GroupBy(span => span.SessionId))
        {
            var seconds = TimeLedger.TotalSeconds(group.ToList());

            if (seconds > longestSession)
            {
                longestSession = seconds;
            }
        }

        return new StatisticsModel(
            range,
            window,
            kind,
            focusSeconds,
            manualSeconds,
            tasksCompleted,
            sessionsCompleted,
            StreakCalculator.CurrentStreak(counts, today),
            StreakCalculator.LongestStreak(counts, today),
            averageSessionSeconds,
            scheduled.Count,
            scheduled.Count(task => task.IsCompleted),
            buckets,
            topTasks,
            workedTasks.Count,
            averageTaskSeconds,
            activeDays,
            longestSession,
            bestDay,
            busiestDay,
            busiestWeekday);
    }

    /// <summary>
    /// Cuts each run down to the part that falls inside the window, in local time. Doing it here
    /// rather than in the query keeps the boundary arithmetic in one testable place.
    /// </summary>
    private static IReadOnlyList<RunSpan> ClipToWindow(
        IReadOnlyList<RunSpan> spans, ActivityWindow window, TimeZoneInfo zone)
    {
        var fromUtc = ToUtc(window.From.ToDateTime(TimeOnly.MinValue), zone);
        var toUtc = ToUtc(window.To.AddDays(1).ToDateTime(TimeOnly.MinValue), zone);

        var clipped = new List<RunSpan>(spans.Count);

        foreach (var span in spans)
        {
            var start = span.StartUtc < fromUtc ? fromUtc : span.StartUtc;
            var end = span.EndUtc > toUtc ? toUtc : span.EndUtc;

            if (end > start)
            {
                clipped.Add(span with { StartUtc = start, EndUtc = end });
            }
        }

        return clipped;
    }

    private static (IReadOnlyList<StatBucket> Buckets, BucketKind Kind) BuildChart(
        StatisticsRange range,
        ActivityWindow window,
        IReadOnlyDictionary<DateOnly, long> focusByDay,
        IReadOnlyDictionary<DateOnly, long> manualByDay,
        IReadOnlyList<RunSpan> spans,
        DateOnly today,
        TimeZoneInfo zone)
    {
        if (range == StatisticsRange.Today)
        {
            var byHour = TimeLedger.SecondsByLocalHour(spans, today, zone);
            var manualToday = manualByDay.GetValueOrDefault(today);

            var hours = new List<StatBucket>(24);
            for (var hour = 0; hour < 24; hour++)
            {
                // Hand-entered time has no clock position, so it is shown once, on the hour the
                // day is usually looked at, rather than smeared across a day it never occupied.
                var manualHere = hour == 12 ? manualToday : 0;

                hours.Add(new StatBucket(
                    hour % 6 == 0 ? hour.ToString("00", CultureInfo.InvariantCulture) : string.Empty,
                    hour.ToString("00", CultureInfo.InvariantCulture) + ":00",
                    today,
                    byHour.GetValueOrDefault(hour),
                    manualHere));
            }

            return (hours, BucketKind.Hour);
        }

        var first = window.From;
        var last = window.To;

        if (range == StatisticsRange.AllTime)
        {
            // "All time" is bounded by the query, not by history. Start at the first day that
            // actually holds something so the chart is not mostly empty columns.
            var earliest = focusByDay.Keys.Concat(manualByDay.Keys).DefaultIfEmpty(today).Min();
            first = earliest < last ? earliest : last;
        }

        var dayCount = last.DayNumber - first.DayNumber + 1;

        if (range != StatisticsRange.AllTime || dayCount <= 60)
        {
            return (BuildDaily(first, last, focusByDay, manualByDay), BucketKind.Day);
        }

        return dayCount <= 730
            ? (BuildWeekly(first, last, focusByDay, manualByDay), BucketKind.Week)
            : (BuildMonthly(first, last, focusByDay, manualByDay), BucketKind.Month);
    }

    private static IReadOnlyList<StatBucket> BuildDaily(
        DateOnly first,
        DateOnly last,
        IReadOnlyDictionary<DateOnly, long> focusByDay,
        IReadOnlyDictionary<DateOnly, long> manualByDay)
    {
        var buckets = new List<StatBucket>();
        var count = last.DayNumber - first.DayNumber + 1;

        // Labelling every column of a thirty-day chart makes it unreadable, so only every fifth
        // one is labelled once there are more than ten.
        var step = count > 10 ? 5 : 1;
        var index = 0;

        for (var day = first; day <= last; day = day.AddDays(1), index++)
        {
            var label = (count - 1 - index) % step == 0
                ? day.ToString("d MMM", CultureInfo.InvariantCulture)
                : string.Empty;

            buckets.Add(new StatBucket(
                count <= 10 ? day.ToString("ddd", CultureInfo.InvariantCulture) : label,
                day.ToString("dddd d MMMM", CultureInfo.InvariantCulture),
                day,
                focusByDay.GetValueOrDefault(day),
                manualByDay.GetValueOrDefault(day)));
        }

        return buckets;
    }

    private static IReadOnlyList<StatBucket> BuildWeekly(
        DateOnly first,
        DateOnly last,
        IReadOnlyDictionary<DateOnly, long> focusByDay,
        IReadOnlyDictionary<DateOnly, long> manualByDay)
    {
        var buckets = new List<StatBucket>();
        var cursor = first.AddDays(-StreakCalculator.MondayIndex(first.DayOfWeek));

        while (cursor <= last)
        {
            long focus = 0, manual = 0;
            for (var offset = 0; offset < 7; offset++)
            {
                var day = cursor.AddDays(offset);
                focus += focusByDay.GetValueOrDefault(day);
                manual += manualByDay.GetValueOrDefault(day);
            }

            buckets.Add(new StatBucket(
                cursor.Day == 1 || cursor.Day <= 7 ? cursor.ToString("MMM", CultureInfo.InvariantCulture) : string.Empty,
                "week of " + cursor.ToString("d MMMM yyyy", CultureInfo.InvariantCulture),
                cursor,
                focus,
                manual));

            cursor = cursor.AddDays(7);
        }

        return buckets;
    }

    private static IReadOnlyList<StatBucket> BuildMonthly(
        DateOnly first,
        DateOnly last,
        IReadOnlyDictionary<DateOnly, long> focusByDay,
        IReadOnlyDictionary<DateOnly, long> manualByDay)
    {
        var buckets = new List<StatBucket>();
        var cursor = new DateOnly(first.Year, first.Month, 1);

        while (cursor <= last)
        {
            var next = cursor.AddMonths(1);

            long focus = 0, manual = 0;
            for (var day = cursor; day < next; day = day.AddDays(1))
            {
                focus += focusByDay.GetValueOrDefault(day);
                manual += manualByDay.GetValueOrDefault(day);
            }

            buckets.Add(new StatBucket(
                cursor.Month == 1 ? cursor.ToString("yyyy", CultureInfo.InvariantCulture)
                                  : cursor.ToString("MMM", CultureInfo.InvariantCulture),
                cursor.ToString("MMMM yyyy", CultureInfo.InvariantCulture),
                cursor,
                focus,
                manual));

            cursor = next;
        }

        return buckets;
    }

    /// <summary>
    /// The live tasks with the most time in the range.
    ///
    /// A deleted task is not listed. Its hours stay in the totals, because the time was spent
    /// whatever happened to the task afterwards and rewriting that would make the chart disagree
    /// with the heatmap; but a list of what you have been working on should not name something
    /// you removed, and a row reading "Deleted task" is worse than no row at all.
    /// </summary>
    private static IReadOnlyList<TopTask> BuildTopTasks(
        ActivitySnapshot snapshot,
        IReadOnlyList<RunSpan> spans,
        IReadOnlyList<ManualTimeEntry> manual,
        long totalSeconds)
    {
        var focusByTask = TimeLedger.SecondsByTask(spans);
        var manualByTask = TimeLedger.ManualSecondsByTask(manual);

        var ids = new HashSet<Guid>(focusByTask.Keys);
        ids.UnionWith(manualByTask.Keys);

        if (ids.Count == 0)
        {
            return Array.Empty<TopTask>();
        }

        // Titles come from the live task row and from nowhere else. The copies kept on sessions
        // and manual entries exist so that history survives a deletion; now that a deleted task
        // is not listed, reaching for them could only ever resurrect a name that was removed.
        var tasksById = snapshot.Tasks.ToDictionary(task => task.Id);

        var rows = new List<TopTask>(ids.Count);

        foreach (var id in ids)
        {
            var focus = focusByTask.GetValueOrDefault(id);
            var manualSeconds = manualByTask.GetValueOrDefault(id);
            var total = focus + manualSeconds;

            if (total <= 0)
            {
                continue;
            }

            // Unknown is treated as gone. A task row is kept when it is deleted, so an id with
            // no row at all is one that no longer exists in any form.
            if (!tasksById.TryGetValue(id, out var task) || task.IsDeleted)
            {
                continue;
            }

            rows.Add(new TopTask(
                id,
                task.Title,
                focus,
                manualSeconds,
                task.IsCompleted,
                totalSeconds <= 0 ? 0d : (double)total / totalSeconds));
        }

        return rows
            .OrderByDescending(row => row.TotalSeconds)
            .ThenBy(row => row.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(TopTaskCount)
            .ToList();
    }

    private static DateTime ToUtc(DateTime local, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone);
}
