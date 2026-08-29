using Counter.Core.Journey;
using Counter.Core.Models;
using Counter.Core.Statistics;
using Xunit;

namespace Counter.Tests;

/// <summary>
/// The statistics surface, asserted against hand-built history.
///
/// Everything the panel shows is a pure function of the snapshot, the range and an instant, so
/// none of this needs a database and none of it needs to wait: the numbers are checked directly
/// against the runs that were recorded.
/// </summary>
public class StatisticsTests
{
    private static readonly DateOnly Today = new(2026, 8, 29);
    private static readonly DateTime Now = new(2026, 8, 29, 18, 0, 0, DateTimeKind.Utc);

    /// <summary>An instant on a local day. The tests run in UTC, so local and UTC agree.</summary>
    private static DateTime At(DateOnly day, int hour, int minute = 0)
        => DateTime.SpecifyKind(day.ToDateTime(new TimeOnly(hour, minute)), DateTimeKind.Utc);

    private static StatisticsModel Build(FakeActivityReader reader, StatisticsRange range)
        => StatisticsCalculator.Build(
            reader.Read(default, default, TimeZoneInfo.Utc), range, Today, Now, TimeZoneInfo.Utc);

    // ================================================================ Ranges

    [Fact]
    public void Today_counts_only_today()
    {
        var reader = new FakeActivityReader()
            .WithRun(At(Today, 9), At(Today, 10))
            .WithRun(At(Today.AddDays(-1), 9), At(Today.AddDays(-1), 11));

        var model = Build(reader, StatisticsRange.Today);

        Assert.Equal(3600, model.FocusSeconds);
        Assert.Equal(24, model.Chart.Count);
        Assert.Equal(BucketKind.Hour, model.BucketKind);
    }

    [Fact]
    public void Seven_days_counts_the_last_seven_including_today()
    {
        var reader = new FakeActivityReader();

        for (var back = 0; back < 10; back++)
        {
            var day = Today.AddDays(-back);
            reader.WithRun(At(day, 9), At(day, 10));
        }

        var model = Build(reader, StatisticsRange.Last7Days);

        Assert.Equal(7 * 3600, model.FocusSeconds);
        Assert.Equal(7, model.Chart.Count);
        Assert.Equal(BucketKind.Day, model.BucketKind);
    }

    [Fact]
    public void Thirty_days_counts_thirty_daily_buckets()
    {
        var reader = new FakeActivityReader().WithRun(At(Today, 9), At(Today, 10));
        var model = Build(reader, StatisticsRange.Last30Days);

        Assert.Equal(30, model.Chart.Count);
        Assert.Equal(BucketKind.Day, model.BucketKind);
    }

    [Fact]
    public void All_time_reaches_back_past_the_thirty_day_window()
    {
        var old = Today.AddDays(-120);
        var reader = new FakeActivityReader()
            .WithRun(At(old, 9), At(old, 11))
            .WithRun(At(Today, 9), At(Today, 10));

        var model = Build(reader, StatisticsRange.AllTime);

        Assert.Equal(3 * 3600, model.FocusSeconds);

        // A hundred and twenty days is bucketed by week rather than as a hundred and twenty bars.
        Assert.Equal(BucketKind.Week, model.BucketKind);
        Assert.True(model.Chart.Count is > 15 and < 25);
    }

    // ================================================================ Summary numbers

    [Fact]
    public void Completed_tasks_and_sessions_are_counted_separately()
    {
        var reader = new FakeActivityReader()
            .WithCompletedTask(Today)
            .WithCompletedTask(Today)
            .WithCompletedSession(Today);

        var model = Build(reader, StatisticsRange.Today);

        Assert.Equal(2, model.TasksCompleted);
        Assert.Equal(1, model.SessionsCompleted);
    }

    [Fact]
    public void Focus_time_is_added_up_across_sessions()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var reader = new FakeActivityReader()
            .WithRun(At(Today, 9), At(Today, 10), sessionId: a)
            .WithRun(At(Today, 11), At(Today, 11, 30), sessionId: b);

        var model = Build(reader, StatisticsRange.Today);

        Assert.Equal(5400, model.FocusSeconds);
        Assert.Equal(2700, model.AverageSessionSeconds);
    }

    [Fact]
    public void The_average_of_no_sessions_is_zero_rather_than_a_division()
    {
        var model = Build(new FakeActivityReader(), StatisticsRange.Today);

        Assert.Equal(0, model.AverageSessionSeconds);
        Assert.True(model.IsEmpty);
    }

    [Fact]
    public void The_completion_rate_is_completed_over_scheduled_in_range()
    {
        var reader = new FakeActivityReader()
            .WithCompletedTask(Today)
            .WithCompletedTask(Today)
            .WithOpenTask(Today)
            .WithOpenTask(Today);

        var model = Build(reader, StatisticsRange.Today);

        Assert.Equal(4, model.TasksScheduled);
        Assert.Equal(2, model.TasksScheduledCompleted);
        Assert.Equal(0.5, model.CompletionRate, 3);
    }

    [Fact]
    public void A_range_with_no_scheduled_tasks_has_a_completion_rate_of_zero()
    {
        var model = Build(new FakeActivityReader(), StatisticsRange.Today);

        Assert.Equal(0, model.TasksScheduled);
        Assert.Equal(0d, model.CompletionRate);
    }

    [Fact]
    public void The_streaks_come_from_the_same_contributions_the_journey_uses()
    {
        var reader = new FakeActivityReader();

        for (var back = 0; back < 3; back++)
        {
            reader.WithCompletedTask(Today.AddDays(-back));
        }

        // An older, longer run.
        for (var back = 20; back < 26; back++)
        {
            reader.WithCompletedTask(Today.AddDays(-back));
        }

        var model = Build(reader, StatisticsRange.AllTime);

        Assert.Equal(3, model.CurrentStreak);
        Assert.Equal(6, model.LongestStreak);
    }

    // ================================================================ Manual time

    [Fact]
    public void Manual_time_is_included_once_and_stays_separable()
    {
        var reader = new FakeActivityReader()
            .WithRun(At(Today, 9), At(Today, 10))
            .WithManual(Today, 1800);

        var model = Build(reader, StatisticsRange.Today);

        Assert.Equal(3600, model.FocusSeconds);
        Assert.Equal(1800, model.ManualSeconds);
        Assert.Equal(5400, model.TotalSeconds);
    }

    // ================================================================ Top tasks

    [Fact]
    public void Top_tasks_are_ranked_by_the_time_they_actually_took()
    {
        var big = Guid.NewGuid();
        var small = Guid.NewGuid();

        var reader = new FakeActivityReader();
        reader.Tasks.Add(new TaskRecord(big, "Big", Today, false, null, 3600, false));
        reader.Tasks.Add(new TaskRecord(small, "Small", Today, false, null, 3600, false));
        reader.WithRun(At(Today, 9), At(Today, 11), big);
        reader.WithRun(At(Today, 12), At(Today, 12, 30), small);

        var model = Build(reader, StatisticsRange.Today);

        Assert.Equal(2, model.TopTasks.Count);
        Assert.Equal("Big", model.TopTasks[0].Title);
        Assert.Equal(7200, model.TopTasks[0].FocusSeconds);
        Assert.Equal(0.8, model.TopTasks[0].Share, 2);
        Assert.Equal("Small", model.TopTasks[1].Title);
    }

    [Fact]
    public void A_deleted_task_is_not_listed_but_its_hours_are_still_counted()
    {
        // The two halves of the rule, and they pull in opposite directions on purpose. A list of
        // what you have been working on should not name something you deleted. The total is a
        // measurement of time you actually spent, and deleting a task afterwards does not give
        // that time back - rewriting it would also make the chart disagree with the heatmap,
        // which counts the same hours from the same rows.
        var live = Guid.NewGuid();
        var gone = Guid.NewGuid();

        var reader = new FakeActivityReader();
        reader.Tasks.Add(new TaskRecord(live, "Still here", Today, false, null, 3600, false));
        reader.Tasks.Add(new TaskRecord(gone, "Gone but worked on", Today, false, null, 3600, true));
        reader.WithRun(At(Today, 9), At(Today, 10), live);
        reader.WithRun(At(Today, 11), At(Today, 12), gone);

        var model = Build(reader, StatisticsRange.Today);

        Assert.Equal("Still here", Assert.Single(model.TopTasks).Title);
        Assert.DoesNotContain(model.TopTasks, row => row.Title.Contains("Gone"));
        Assert.Equal(7200, model.FocusSeconds);
    }

    [Fact]
    public void A_deleted_task_is_not_counted_in_the_per_task_average()
    {
        // It cannot be: the average is time divided by the tasks you can still look at, and a
        // deleted one is not among them. Two hours across one live task is an hour per task,
        // not thirty minutes each across two.
        var live = Guid.NewGuid();
        var gone = Guid.NewGuid();

        var reader = new FakeActivityReader();
        reader.Tasks.Add(new TaskRecord(live, "Still here", Today, false, null, 3600, false));
        reader.Tasks.Add(new TaskRecord(gone, "Gone", Today, false, null, 3600, true));
        reader.WithRun(At(Today, 9), At(Today, 10), live);
        reader.WithRun(At(Today, 11), At(Today, 12), gone);

        var model = Build(reader, StatisticsRange.Today);

        Assert.Equal(1, model.TasksWorked);
        Assert.Equal(3600, model.AverageTaskSeconds);
    }

    [Fact]
    public void A_task_whose_row_is_gone_entirely_is_not_listed()
    {
        // A deletion keeps the row and stamps it, so an id with no row at all is one that no
        // longer exists in any form. The title copies kept on sessions exist so that history
        // survives; reaching for them here would resurrect a name that was removed.
        var id = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        var reader = new FakeActivityReader();
        reader.Sessions.Add(new SessionRecord(
            sessionId, id, "Remembered by its session", FocusSessionStatus.Completed,
            SessionEndReason.Completed, 3600, At(Today, 9), At(Today, 10), Today));
        reader.WithRun(At(Today, 9), At(Today, 10), id, sessionId);

        var model = Build(reader, StatisticsRange.Today);

        Assert.Empty(model.TopTasks);
        Assert.Equal(3600, model.FocusSeconds);
    }

    // ================================================================ The shape of a range

    [Fact]
    public void The_per_task_average_divides_by_tasks_actually_worked_on()
    {
        // Not by every task that exists. A range where you touched two of your nine tasks is a
        // range with two tasks in it.
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var untouched = Guid.NewGuid();

        var reader = new FakeActivityReader();
        reader.Tasks.Add(new TaskRecord(first, "First", Today, false, null, 3600, false));
        reader.Tasks.Add(new TaskRecord(second, "Second", Today, false, null, 3600, false));
        reader.Tasks.Add(new TaskRecord(untouched, "Never started", Today, false, null, 3600, false));

        reader.WithRun(At(Today, 9), At(Today, 10), first);
        reader.WithRun(At(Today, 11), At(Today, 13), second);

        var model = Build(reader, StatisticsRange.Today);

        Assert.Equal(2, model.TasksWorked);
        Assert.Equal(5400, model.AverageTaskSeconds);
    }

    [Fact]
    public void The_daily_average_divides_by_days_worked_rather_than_days_in_the_range()
    {
        // Averaging over the whole window punishes you for the days you were not at the desk,
        // which makes the number say more about how the range was chosen than about how you
        // worked. Four hours over two active days in a seven day range is two hours a day.
        var reader = new FakeActivityReader();
        var id = Guid.NewGuid();
        reader.Tasks.Add(new TaskRecord(id, "Work", Today, false, null, 3600, false));

        reader.WithRun(At(Today.AddDays(-3), 9), At(Today.AddDays(-3), 11), id);
        reader.WithRun(At(Today, 9), At(Today, 11), id);

        var model = Build(reader, StatisticsRange.Last7Days);

        Assert.Equal(2, model.ActiveDays);
        Assert.Equal(7200, model.AverageActiveDaySeconds);
    }

    [Fact]
    public void The_best_day_is_the_one_with_the_most_time_on_it()
    {
        var reader = new FakeActivityReader();
        var id = Guid.NewGuid();
        reader.Tasks.Add(new TaskRecord(id, "Work", Today, false, null, 3600, false));

        reader.WithRun(At(Today.AddDays(-2), 9), At(Today.AddDays(-2), 10), id);
        reader.WithRun(At(Today.AddDays(-1), 9), At(Today.AddDays(-1), 13), id);
        reader.WithRun(At(Today, 9), At(Today, 11), id);

        var model = Build(reader, StatisticsRange.Last7Days);

        Assert.Equal(Today.AddDays(-1), model.BestDay.Date);
        Assert.Equal(14400, model.BestDay.Value);
        Assert.True(model.BestDay.HasValue);
    }

    [Fact]
    public void The_busiest_weekday_sums_every_week_in_the_range()
    {
        // Seven days apart is the same weekday twice, and the two have to add up: one long
        // Monday and one short Monday beat a single medium Tuesday between them.
        var reader = new FakeActivityReader();
        var id = Guid.NewGuid();
        reader.Tasks.Add(new TaskRecord(id, "Work", Today, false, null, 3600, false));

        var monday = Today.AddDays(-(int)Today.DayOfWeek + 1);
        if (monday > Today) { monday = monday.AddDays(-7); }

        reader.WithRun(At(monday, 9), At(monday, 11), id);
        reader.WithRun(At(monday.AddDays(-7), 9), At(monday.AddDays(-7), 11), id);
        reader.WithRun(At(monday.AddDays(1), 9), At(monday.AddDays(1), 12), id);

        var model = Build(reader, StatisticsRange.AllTime);

        Assert.Equal(DayOfWeek.Monday, model.BusiestWeekday.Day);
        Assert.Equal(14400, model.BusiestWeekday.Seconds);
    }

    [Fact]
    public void The_busiest_day_counts_completions_rather_than_time()
    {
        // A day of many small wins is a different kind of good day from one long stretch, and
        // both are worth being able to see.
        var reader = new FakeActivityReader();

        foreach (var index in Enumerable.Range(0, 3))
        {
            reader.Tasks.Add(new TaskRecord(
                Guid.NewGuid(), "Done " + index, Today.AddDays(-1), true, Today.AddDays(-1), 600, false));
        }

        reader.Tasks.Add(new TaskRecord(Guid.NewGuid(), "One", Today, true, Today, 600, false));

        var model = Build(reader, StatisticsRange.Last7Days);

        Assert.Equal(Today.AddDays(-1), model.BusiestDay.Date);
        Assert.Equal(3, model.BusiestDay.Value);
    }

    [Fact]
    public void A_deleted_task_never_becomes_the_busiest_day()
    {
        var reader = new FakeActivityReader();

        foreach (var index in Enumerable.Range(0, 4))
        {
            reader.Tasks.Add(new TaskRecord(
                Guid.NewGuid(), "Gone " + index, Today.AddDays(-1), true, Today.AddDays(-1), 600, true));
        }

        reader.Tasks.Add(new TaskRecord(Guid.NewGuid(), "One", Today, true, Today, 600, false));

        var model = Build(reader, StatisticsRange.Last7Days);

        Assert.Equal(Today, model.BusiestDay.Date);
        Assert.Equal(1, model.BusiestDay.Value);
    }

    [Fact]
    public void The_longest_run_is_one_session_rather_than_a_days_total()
    {
        // Three separate hours is not a three hour run, and the difference is the whole point of
        // the figure.
        var reader = new FakeActivityReader();
        var id = Guid.NewGuid();
        var marathon = Guid.NewGuid();
        reader.Tasks.Add(new TaskRecord(id, "Work", Today, false, null, 3600, false));

        reader.WithRun(At(Today, 6), At(Today, 7), id);
        reader.WithRun(At(Today, 8), At(Today, 9), id);
        reader.WithRun(At(Today, 10), At(Today, 11), id);
        reader.WithRun(At(Today, 13), At(Today, 15), id, marathon);

        var model = Build(reader, StatisticsRange.Today);

        Assert.Equal(7200, model.LongestSessionSeconds);
    }

    [Fact]
    public void An_empty_range_reports_nothing_rather_than_zero_dated_highlights()
    {
        // Null rather than a zero beside a date that means nothing, so the panel can say there
        // is nothing to show instead of printing a confident lie.
        var model = Build(new FakeActivityReader(), StatisticsRange.Last7Days);

        Assert.False(model.BestDay.HasValue);
        Assert.Null(model.BestDay.Date);
        Assert.False(model.BusiestDay.HasValue);
        Assert.False(model.BusiestWeekday.HasValue);
        Assert.Equal(0, model.ActiveDays);
        Assert.Equal(0, model.AverageActiveDaySeconds);
        Assert.Equal(0, model.AverageTaskSeconds);
        Assert.Equal(0, model.LongestSessionSeconds);
    }

    [Fact]
    public void The_top_task_list_is_capped()
    {
        var reader = new FakeActivityReader();

        for (var i = 0; i < 12; i++)
        {
            var id = Guid.NewGuid();
            reader.Tasks.Add(new TaskRecord(id, "Task " + i, Today, false, null, 3600, false));
            reader.WithRun(At(Today, 6).AddMinutes(i * 10), At(Today, 6).AddMinutes(i * 10 + 5), id);
        }

        Assert.Equal(StatisticsCalculator.TopTaskCount, Build(reader, StatisticsRange.Today).TopTasks.Count);
    }

    // ================================================================ Midnight

    [Fact]
    public void A_session_crossing_midnight_is_split_between_the_two_days()
    {
        var yesterday = Today.AddDays(-1);

        var reader = new FakeActivityReader()
            .WithRun(At(yesterday, 23, 30), At(Today, 0, 30));

        var model = Build(reader, StatisticsRange.Last7Days);

        var first = model.Chart.Single(b => b.Date == yesterday);
        var second = model.Chart.Single(b => b.Date == Today);

        Assert.Equal(1800, first.FocusSeconds);
        Assert.Equal(1800, second.FocusSeconds);
    }

    [Fact]
    public void A_run_that_started_before_the_range_only_contributes_the_part_inside_it()
    {
        // Starts three hours before today began and finishes two hours into it.
        var reader = new FakeActivityReader()
            .WithRun(At(Today.AddDays(-1), 21), At(Today, 2));

        var model = Build(reader, StatisticsRange.Today);

        Assert.Equal(2 * 3600, model.FocusSeconds);
    }

    // ================================================================ Live time

    [Fact]
    public void A_run_still_in_progress_is_included_up_to_now_without_being_written()
    {
        var reader = new FakeActivityReader();

        // An open run started an hour before "now".
        reader.Segments.Add(new FocusSegment
        {
            Id = Guid.NewGuid(),
            SessionId = Guid.NewGuid(),
            StartedAtUtc = Now.AddHours(-1),
            EndedAtUtc = null
        });

        var model = Build(reader, StatisticsRange.Today);

        Assert.Equal(3600, model.FocusSeconds);

        // Nothing was mutated: the reader's own row is still open.
        Assert.True(reader.Segments[0].IsOpen);
    }

    // ================================================================ Empty state

    [Fact]
    public void An_empty_history_produces_zeros_and_an_empty_state()
    {
        var model = Build(new FakeActivityReader(), StatisticsRange.AllTime);

        Assert.True(model.IsEmpty);
        Assert.Equal(0, model.TotalSeconds);
        Assert.Equal(0, model.TasksCompleted);
        Assert.Equal(0, model.SessionsCompleted);
        Assert.Equal(0, model.CurrentStreak);
        Assert.Equal(0, model.LongestStreak);
        Assert.Empty(model.TopTasks);
        Assert.Equal(0, model.PeakBucketSeconds);
    }

    [Fact]
    public void The_range_labels_read_the_way_the_filter_pills_do()
    {
        Assert.Equal("Today", StatisticsService.Label(StatisticsRange.Today));
        Assert.Equal("7 days", StatisticsService.Label(StatisticsRange.Last7Days));
        Assert.Equal("30 days", StatisticsService.Label(StatisticsRange.Last30Days));
        Assert.Equal("All time", StatisticsService.Label(StatisticsRange.AllTime));
    }

    [Fact]
    public void The_service_publishes_one_snapshot_per_refresh()
    {
        var reader = new FakeActivityReader().WithRun(At(Today, 9), At(Today, 10));
        var service = new StatisticsService(reader, new TestClock(Now));

        var published = new List<StatisticsModel>();
        service.Changed += published.Add;

        service.RefreshAsync(StatisticsRange.Today);

        Assert.Single(published);
        Assert.Equal(3600, published[0].FocusSeconds);
        Assert.Same(published[0], service.Current);
    }

    [Fact]
    public void A_failing_read_is_reported_rather_than_thrown_at_the_caller()
    {
        var reader = new FakeActivityReader { Fail = new InvalidOperationException("no") };
        var service = new StatisticsService(reader, new TestClock(Now));

        Exception? seen = null;
        service.RefreshAsync(StatisticsRange.Today, ex => seen = ex);

        Assert.NotNull(seen);
        Assert.Same(StatisticsModel.Empty, service.Current);
    }
}
