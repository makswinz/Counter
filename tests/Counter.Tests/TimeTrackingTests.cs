using Counter.App.ViewModels;
using Counter.Core.Focus;
using Counter.Core.Journey;
using Counter.Core.Models;
using Counter.Core.Statistics;
using Xunit;

namespace Counter.Tests;

/// <summary>
/// Time actually spent, and the rule that ticking a task off stops its own timer.
///
/// Every assertion here is about instants that were recorded, never about a counter that was
/// incremented, which is why none of it needs the clock to run: the tests move time by hand and
/// the numbers follow exactly.
/// </summary>
public class TimeTrackingTests
{
    private static readonly DateTime T0 = new(2026, 8, 29, 9, 0, 0, DateTimeKind.Utc);

    private sealed class Harness
    {
        public Harness()
        {
            Clock = new TestClock(T0);
            Tasks = new FakeTaskRepository();
            Sessions = new FakeSessionRepository();
            Manual = new FakeManualTimeRepository();
            Reader = new RepositoryActivityReader(Tasks, Sessions, Manual);
            Settings = new FakeSettingsStore();

            Focus = new FocusSessionService(new FocusEngine(Clock), Sessions, Clock);
            Journey = new JourneyActivityService(Reader, Clock);
            Statistics = new StatisticsService(Reader, Clock);

            Shell = new ShellViewModel(
                Tasks, Manual, Settings, Focus, Journey, Statistics, Reader, Clock);

            Shell.Load();
        }

        public TestClock Clock { get; }

        public FakeTaskRepository Tasks { get; }

        public FakeSessionRepository Sessions { get; }

        public FakeManualTimeRepository Manual { get; }

        public RepositoryActivityReader Reader { get; }

        public FakeSettingsStore Settings { get; }

        public FocusSessionService Focus { get; }

        public JourneyActivityService Journey { get; }

        public StatisticsService Statistics { get; }

        public ShellViewModel Shell { get; }

        public TaskItem AddTask(string title, long seconds = 3600)
        {
            var task = new TaskItem
            {
                Title = title,
                ScheduledDate = DateOnly.FromDateTime(T0),
                EstimatedSeconds = seconds,
                CreatedAtUtc = Clock.UtcNow,
                UpdatedAtUtc = Clock.UtcNow
            };

            Tasks.Add(task);
            Shell.Load();
            return task;
        }

        public TaskRowViewModel Row(TaskItem task)
        {
            Shell.SelectDate(task.ScheduledDate ?? DateOnly.FromDateTime(T0));
            return Shell.PlannerTasks.Single(r => r.Id == task.Id);
        }

        /// <summary>Every recorded run for a task, closed at the given instant.</summary>
        public long RecordedSeconds(Guid taskId)
        {
            var spans = TimeLedger.ToSpans(
                Sessions.AllSegments.Where(s => s.TaskId == taskId), Clock.UtcNow);
            return TimeLedger.TotalSeconds(spans);
        }
    }

    // ================================================================ Segments

    [Fact]
    public void Starting_opens_a_run_and_pausing_closes_it()
    {
        var h = new Harness();
        var task = h.AddTask("Write");

        h.Focus.Play(task);
        Assert.NotNull(h.Focus.CurrentSegment);
        Assert.True(h.Focus.CurrentSegment!.IsOpen);

        h.Clock.AdvanceSeconds(600);
        h.Focus.Pause();

        Assert.Null(h.Focus.CurrentSegment);
        var segment = Assert.Single(h.Sessions.AllSegments);
        Assert.False(segment.IsOpen);
        Assert.Equal(600, segment.SecondsAt(h.Clock.UtcNow));
    }

    [Fact]
    public void Paused_time_is_never_counted()
    {
        var h = new Harness();
        var task = h.AddTask("Write");

        h.Focus.Play(task);
        h.Clock.AdvanceSeconds(300);
        h.Focus.Pause();

        // An hour goes by with the session paused.
        h.Clock.AdvanceSeconds(3600);

        Assert.Equal(300, h.RecordedSeconds(task.Id));
    }

    [Fact]
    public void Resuming_opens_a_second_run_rather_than_reopening_the_first()
    {
        var h = new Harness();
        var task = h.AddTask("Write");

        h.Focus.Play(task);
        h.Clock.AdvanceSeconds(300);
        h.Focus.Pause();
        h.Clock.AdvanceSeconds(3600);
        h.Focus.Resume();
        h.Clock.AdvanceSeconds(120);
        h.Focus.Stop();

        Assert.Equal(2, h.Sessions.AllSegments.Count);
        Assert.Equal(420, h.RecordedSeconds(task.Id));
    }

    [Fact]
    public void Two_runs_can_never_overlap()
    {
        var h = new Harness();
        var task = h.AddTask("Write");

        h.Focus.Play(task);
        h.Clock.AdvanceSeconds(60);
        h.Focus.Pause();
        h.Clock.AdvanceSeconds(60);
        h.Focus.Resume();
        h.Clock.AdvanceSeconds(60);
        h.Focus.Pause();

        var ordered = h.Sessions.AllSegments.OrderBy(s => s.StartedAtUtc).ToList();
        for (var i = 1; i < ordered.Count; i++)
        {
            Assert.True(ordered[i - 1].EndedAtUtc <= ordered[i].StartedAtUtc);
        }
    }

    [Fact]
    public void Natural_completion_caps_the_run_at_the_planned_duration()
    {
        var h = new Harness();
        var task = h.AddTask("Write", seconds: 600);

        h.Focus.Play(task);

        // The app was busy elsewhere and only noticed the timer an hour late.
        h.Clock.AdvanceSeconds(3600);
        Assert.True(h.Focus.CompleteIfDue());

        // Exactly the planned ten minutes, not the hour that passed.
        Assert.Equal(600, h.RecordedSeconds(task.Id));
    }

    [Fact]
    public void Stopping_by_hand_records_the_time_actually_run()
    {
        var h = new Harness();
        var task = h.AddTask("Write", seconds: 3600);

        h.Focus.Play(task);
        h.Clock.AdvanceSeconds(900);
        Assert.True(h.Focus.Stop(SessionEndReason.StoppedByUser));

        Assert.Equal(900, h.RecordedSeconds(task.Id));

        var session = Assert.Single(h.Sessions.All);
        Assert.Equal(FocusSessionStatus.Cancelled, session.Status);
        Assert.Equal(SessionEndReason.StoppedByUser, session.EndReason);
    }

    // ================================================================ Completing stops the timer

    [Fact]
    public void Completing_the_running_task_stops_its_timer_and_keeps_the_time()
    {
        var h = new Harness();
        var task = h.AddTask("Write");

        h.Focus.Play(task);
        h.Clock.AdvanceSeconds(720);

        h.Shell.ToggleTaskCompletion(h.Row(task));

        Assert.False(h.Focus.HasActiveSession);
        Assert.Equal(720, h.RecordedSeconds(task.Id));

        var session = Assert.Single(h.Sessions.All);
        Assert.Equal(SessionEndReason.TaskCompleted, session.EndReason);
    }

    [Fact]
    public void Completing_the_paused_task_stops_it_too()
    {
        var h = new Harness();
        var task = h.AddTask("Write");

        h.Focus.Play(task);
        h.Clock.AdvanceSeconds(300);
        h.Focus.Pause();
        h.Clock.AdvanceSeconds(600);

        h.Shell.ToggleTaskCompletion(h.Row(task));

        Assert.False(h.Focus.HasActiveSession);
        Assert.Equal(300, h.RecordedSeconds(task.Id));
        Assert.Equal(SessionEndReason.TaskCompleted, Assert.Single(h.Sessions.All).EndReason);
    }

    [Fact]
    public void Completing_a_different_task_leaves_the_active_session_alone()
    {
        var h = new Harness();
        var running = h.AddTask("Running");
        var other = h.AddTask("Other");

        h.Focus.Play(running);
        h.Clock.AdvanceSeconds(120);

        h.Shell.ToggleTaskCompletion(h.Row(other));

        Assert.True(h.Focus.IsRunning);
        Assert.Equal(running.Id, h.Focus.Current!.TaskId);
    }

    [Fact]
    public void Marking_a_task_incomplete_again_does_not_restart_its_timer()
    {
        var h = new Harness();
        var task = h.AddTask("Write");

        h.Focus.Play(task);
        h.Clock.AdvanceSeconds(120);
        h.Shell.ToggleTaskCompletion(h.Row(task));
        Assert.False(h.Focus.HasActiveSession);

        h.Shell.ToggleTaskCompletion(h.Row(task));

        Assert.False(h.Focus.HasActiveSession);
        Assert.False(h.Tasks.Get(task.Id)!.IsCompleted);
    }

    [Fact]
    public void The_setting_can_be_turned_off_and_then_completion_leaves_the_timer_running()
    {
        var h = new Harness();
        var task = h.AddTask("Write");

        h.Shell.StopTimerWhenTaskCompleted = false;
        h.Focus.Play(task);
        h.Clock.AdvanceSeconds(120);

        h.Shell.ToggleTaskCompletion(h.Row(task));

        Assert.True(h.Focus.IsRunning);
    }

    [Fact]
    public void The_setting_is_on_by_default_and_is_persisted()
    {
        var h = new Harness();
        Assert.True(h.Shell.StopTimerWhenTaskCompleted);

        h.Shell.StopTimerWhenTaskCompleted = false;

        Assert.False(h.Settings.GetBool(SettingKeys.StopTimerWhenTaskCompleted, true));
    }

    [Fact]
    public void Completion_and_a_play_press_together_cannot_leave_inconsistent_state()
    {
        var h = new Harness();
        var task = h.AddTask("Write");

        h.Focus.Play(task);
        h.Clock.AdvanceSeconds(60);

        var row = h.Row(task);

        // Complete, then immediately press play on the same row. Whatever the second press is
        // taken to mean, the invariants have to hold: never two live sessions, never two runs
        // recording at once, and the first session's run properly closed with its time kept.
        h.Shell.ToggleTaskCompletion(row);
        h.Shell.RequestStartFocus(row);

        Assert.True(h.Sessions.GetActiveSessions().Count <= 1);
        Assert.True(h.Sessions.GetOpenSegments().Count <= 1);

        var completedSession = h.Sessions.All.Single(s => s.EndReason == SessionEndReason.TaskCompleted);
        var closedRun = h.Sessions.AllSegments.Single(g => g.SessionId == completedSession.Id);
        Assert.False(closedRun.IsOpen);
        Assert.Equal(60, closedRun.SecondsAt(h.Clock.UtcNow));
    }

    // ================================================================ Manual time

    [Fact]
    public void Manual_time_is_stored_separately_and_counted_once()
    {
        var h = new Harness();
        var task = h.AddTask("Write");

        h.Focus.Play(task);
        h.Clock.AdvanceSeconds(600);
        h.Focus.Stop();

        h.Shell.OpenManualTime(h.Row(task));
        h.Shell.ManualTime.Hours = 1;
        h.Shell.ManualTime.Minutes = 30;
        h.Shell.SaveManualTimeCommand.Execute(null);

        var entry = Assert.Single(h.Manual.All);
        Assert.Equal(5400, entry.Seconds);
        Assert.Equal(task.Id, entry.TaskId);

        // The timer's own runs are untouched: manual time never becomes a segment.
        Assert.Equal(600, h.RecordedSeconds(task.Id));

        var totals = h.Reader.ReadTotals().Single(t => t.TaskId == task.Id);
        Assert.Equal(600, totals.FocusSeconds);
        Assert.Equal(5400, totals.ManualSeconds);
        Assert.Equal(6000, totals.TotalSeconds);
    }

    [Fact]
    public void A_manual_entry_with_no_time_is_refused()
    {
        var h = new Harness();
        var task = h.AddTask("Write");

        h.Shell.OpenManualTime(h.Row(task));
        h.Shell.ManualTime.Hours = 0;
        h.Shell.ManualTime.Minutes = 0;

        Assert.False(h.Shell.ManualTime.CanSave);

        h.Shell.SaveManualTimeCommand.Execute(null);
        Assert.Empty(h.Manual.All);
    }

    [Fact]
    public void Manual_time_lands_on_the_date_it_names()
    {
        var h = new Harness();
        var task = h.AddTask("Write");
        var missedDay = DateOnly.FromDateTime(T0).AddDays(-3);

        h.Shell.OpenManualTime(h.Row(task));
        h.Shell.ManualTime.Date = missedDay;
        h.Shell.ManualTime.Minutes = 45;
        h.Shell.SaveManualTimeCommand.Execute(null);

        Assert.Equal(missedDay, Assert.Single(h.Manual.All).LocalDate);
        Assert.Equal(1, h.Journey.Current.On(missedDay).Contributions);
        Assert.Equal(2700, h.Journey.Current.On(missedDay).ManualSeconds);
    }

    // ================================================================ Rows

    [Fact]
    public void A_row_shows_the_time_it_has_actually_had()
    {
        var h = new Harness();
        var task = h.AddTask("Write");

        h.Focus.Play(task);
        h.Clock.AdvanceSeconds(2520);   // 42 minutes
        h.Focus.Stop();

        h.Shell.RefreshTaskTimes("test");

        Assert.Equal("42m spent", h.Row(task).SpentText);
    }

    [Fact]
    public void A_running_row_ticks_from_memory_without_a_single_query()
    {
        var h = new Harness();
        var task = h.AddTask("Write");

        h.Focus.Play(task);
        h.Shell.RefreshTaskTimes("test");

        var readsBefore = h.Reader.Reads;
        var row = h.Row(task);

        h.Clock.AdvanceSeconds(65);
        h.Shell.Tick();

        Assert.Equal(65, row.Time.FocusSeconds);
        Assert.Equal("1m spent", row.SpentText);
        Assert.Equal(readsBefore, h.Reader.Reads);
    }

    [Fact]
    public void A_running_row_stops_growing_once_the_planned_time_is_up()
    {
        var h = new Harness();
        var task = h.AddTask("Write", seconds: 600);

        h.Focus.Play(task);
        h.Shell.RefreshTaskTimes("test");
        var row = h.Row(task);

        h.Clock.AdvanceSeconds(3600);
        h.Shell.Tick();

        // CompleteIfDue closes the run at the target, so the row settles at the planned ten
        // minutes rather than climbing to the hour that actually elapsed.
        Assert.Equal(600, row.Time.TotalSeconds);
    }

    [Fact]
    public void The_task_summary_reports_the_plan_the_time_and_the_sessions()
    {
        var h = new Harness();
        var task = h.AddTask("Write", seconds: 3600);

        h.Focus.Play(task);
        h.Clock.AdvanceSeconds(1500);
        h.Focus.Stop();
        h.Shell.RefreshTaskTimes("test");

        var summary = h.Row(task).DetailSummary;

        Assert.Contains("Planned 1h 00m", summary);
        Assert.Contains("Spent 25m", summary);
        Assert.Contains("1 focus session", summary);
        Assert.Contains("Not completed", summary);
    }

    // ================================================================ Splitting at midnight

    [Fact]
    public void A_session_crossing_midnight_splits_across_both_local_days()
    {
        var start = new DateTime(2026, 8, 28, 23, 30, 0, DateTimeKind.Utc);
        var spans = new[]
        {
            new RunSpan(Guid.NewGuid(), null, start, start.AddHours(1))
        };

        var byDay = TimeLedger.SecondsByLocalDay(spans, TimeZoneInfo.Utc);

        Assert.Equal(1800, byDay[new DateOnly(2026, 8, 28)]);
        Assert.Equal(1800, byDay[new DateOnly(2026, 8, 29)]);
    }

    [Fact]
    public void A_run_spanning_three_days_lands_on_all_three()
    {
        var start = new DateTime(2026, 8, 28, 22, 0, 0, DateTimeKind.Utc);
        var spans = new[]
        {
            new RunSpan(Guid.NewGuid(), null, start, start.AddDays(2))
        };

        var byDay = TimeLedger.SecondsByLocalDay(spans, TimeZoneInfo.Utc);

        Assert.Equal(3, byDay.Count);
        Assert.Equal(2 * 3600, byDay[new DateOnly(2026, 8, 28)]);
        Assert.Equal(24 * 3600, byDay[new DateOnly(2026, 8, 29)]);
        Assert.Equal(22 * 3600, byDay[new DateOnly(2026, 8, 30)]);
    }

    [Fact]
    public void An_offset_timezone_moves_the_boundary_with_it()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone(
            "Test+2", TimeSpan.FromHours(2), "Test+2", "Test+2");

        // 22:30 UTC is 00:30 the next day locally, so the whole hour belongs to the later day.
        var start = new DateTime(2026, 8, 28, 22, 30, 0, DateTimeKind.Utc);
        var spans = new[] { new RunSpan(Guid.NewGuid(), null, start, start.AddMinutes(30)) };

        var byDay = TimeLedger.SecondsByLocalDay(spans, zone);

        Assert.Equal(1800, Assert.Single(byDay).Value);
        Assert.Equal(new DateOnly(2026, 8, 29), byDay.Keys.Single());
    }

    [Fact]
    public void Hours_of_the_day_are_split_the_same_way()
    {
        var day = new DateOnly(2026, 8, 29);
        var start = new DateTime(2026, 8, 29, 9, 45, 0, DateTimeKind.Utc);
        var spans = new[] { new RunSpan(Guid.NewGuid(), null, start, start.AddMinutes(30)) };

        var byHour = TimeLedger.SecondsByLocalHour(spans, day, TimeZoneInfo.Utc);

        Assert.Equal(900, byHour[9]);
        Assert.Equal(900, byHour[10]);
    }

    // ================================================================ Totals

    [Fact]
    public void A_task_total_never_double_counts_its_sessions()
    {
        var h = new Harness();
        var task = h.AddTask("Write", seconds: 3600);

        for (var i = 0; i < 3; i++)
        {
            h.Focus.Play(task);
            h.Clock.AdvanceSeconds(300);
            h.Focus.Stop();
            h.Clock.AdvanceSeconds(60);
        }

        var totals = h.Reader.ReadTotals().Single(t => t.TaskId == task.Id);

        Assert.Equal(900, totals.FocusSeconds);
        Assert.Equal(3, totals.SessionCount);
    }
}
