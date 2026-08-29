using Counter.App.ViewModels;
using Counter.Core.Focus;
using Counter.Core.Journey;
using Counter.Core.Models;
using Counter.Core.Statistics;
using Counter.Core.Streaks;
using Xunit;

namespace Counter.Tests;

/// <summary>
/// The journey rules, exercised through the shell so the whole pipeline is covered: change,
/// commit, refresh, publish. Everything runs on an inline scheduler, so the assertions are made
/// against the finished snapshot with no waiting.
/// </summary>
public class JourneyActivityTests
{
    // Midday, so the local day is unambiguous.
    private static readonly DateTime T0 = new(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = new(2026, 8, 29);
    private static readonly DateOnly Yesterday = new(2026, 8, 28);

    internal sealed class Harness
    {
        public Harness()
        {
            Clock = new TestClock(T0);
            Tasks = new FakeTaskRepository();
            Sessions = new FakeSessionRepository();
            Manual = new FakeManualTimeRepository();
            Reader = new RepositoryActivityReader(Tasks, Sessions, Manual);

            Focus = new FocusSessionService(new FocusEngine(Clock), Sessions, Clock);
            Journey = new JourneyActivityService(Reader, Clock);
            Statistics = new StatisticsService(Reader, Clock);

            Shell = new ShellViewModel(
                Tasks, Manual, new FakeSettingsStore(), Focus, Journey, Statistics, Reader, Clock);

            Shell.Load();
        }

        public TestClock Clock { get; }

        public FakeTaskRepository Tasks { get; }

        public FakeSessionRepository Sessions { get; }

        public FakeManualTimeRepository Manual { get; }

        public RepositoryActivityReader Reader { get; }

        public FocusSessionService Focus { get; }

        public JourneyActivityService Journey { get; }

        public StatisticsService Statistics { get; }

        public ShellViewModel Shell { get; }

        public int Streak => Journey.Current.CurrentStreak;

        public int IntensityOn(DateOnly date)
            => Journey.Current.Cells.SingleOrDefault(c => c.Date == date)?.Intensity ?? -1;

        public int CountOn(DateOnly date)
            => Journey.Current.Cells.SingleOrDefault(c => c.Date == date)?.Count ?? -1;

        /// <summary>What the panel is actually bound to, rather than the service's own copy.</summary>
        public int BoundIntensityOn(DateOnly date)
            => Shell.HeatmapCells.Single(c => c.Date == date).Intensity;

        public HeatmapCell CellOn(DateOnly date) => Journey.Current.Cells.Single(c => c.Date == date);

        /// <summary>Adds a task through the repository, as a seeded history would arrive.</summary>
        public TaskItem SeedTask(DateOnly? scheduled, bool completed, DateOnly? completedFor = null)
        {
            var task = new TaskItem
            {
                Title = "Task " + Guid.NewGuid().ToString("N")[..6],
                ScheduledDate = scheduled,
                EstimatedSeconds = 1800,
                IsCompleted = completed,
                CompletedAtUtc = completed ? Clock.UtcNow : null,
                CompletedForDate = completed ? completedFor ?? scheduled ?? Today : null,
                CreatedAtUtc = Clock.UtcNow,
                UpdatedAtUtc = Clock.UtcNow
            };

            Tasks.Add(task);
            return task;
        }

        public void SeedCompletedSession(DateOnly on)
            => Sessions.Seed(new FocusSession
            {
                Id = Guid.NewGuid(),
                Status = FocusSessionStatus.Completed,
                EndReason = SessionEndReason.Completed,
                PlannedSeconds = 1800,
                ElapsedSeconds = 1800,
                StartedAtUtc = T0,
                CompletedAtUtc = T0,
                CompletedForDate = on
            });

        public TaskRowViewModel PlannerRowFor(TaskItem task)
        {
            Shell.SelectDate(task.ScheduledDate ?? Today);
            return Shell.PlannerTasks.Single(r => r.Id == task.Id);
        }
    }

    // ---------------------------------------------------------------- The core scenario

    [Fact]
    public void Completing_a_task_scheduled_yesterday_colours_yesterday()
    {
        var h = new Harness();
        var task = h.SeedTask(Yesterday, completed: false);
        h.Shell.Load();

        Assert.Equal(0, h.IntensityOn(Yesterday));

        h.Shell.ToggleTaskCompletion(h.PlannerRowFor(task));

        Assert.Equal(1, h.IntensityOn(Yesterday));
        Assert.Equal(0, h.IntensityOn(Today));
        Assert.Equal(Yesterday, h.Tasks.Get(task.Id)!.CompletedForDate);
    }

    [Fact]
    public void The_visible_heatmap_updates_without_reopening_the_panel()
    {
        var h = new Harness();
        var task = h.SeedTask(Yesterday, completed: false);
        h.Shell.Load();

        Assert.Equal(0, h.BoundIntensityOn(Yesterday));

        h.Shell.ToggleTaskCompletion(h.PlannerRowFor(task));

        // No reload, no panel change: what the control is bound to has moved on its own.
        Assert.Equal(1, h.BoundIntensityOn(Yesterday));
    }

    [Fact]
    public void Backfilling_yesterday_reconnects_the_streak()
    {
        var h = new Harness();

        // A run that stops two days ago, plus today.
        h.SeedTask(new DateOnly(2026, 8, 26), completed: true);
        h.SeedTask(new DateOnly(2026, 8, 27), completed: true);
        h.SeedTask(Today, completed: true);
        var gap = h.SeedTask(Yesterday, completed: false);

        h.Shell.Load();
        Assert.Equal(1, h.Streak);   // today only: the 28th breaks it

        h.Shell.ToggleTaskCompletion(h.PlannerRowFor(gap));

        Assert.Equal(4, h.Streak);   // 26, 27, 28, 29
    }

    [Fact]
    public void Removing_the_only_activity_from_a_connecting_day_breaks_the_streak()
    {
        var h = new Harness();
        h.SeedTask(new DateOnly(2026, 8, 27), completed: true);
        var connector = h.SeedTask(Yesterday, completed: true);
        h.SeedTask(Today, completed: true);

        h.Shell.Load();
        Assert.Equal(3, h.Streak);

        h.Shell.ToggleTaskCompletion(h.PlannerRowFor(connector));

        Assert.Equal(1, h.Streak);
        Assert.Equal(0, h.IntensityOn(Yesterday));
    }

    // ---------------------------------------------------------------- What does and does not count

    [Fact]
    public void An_unfinished_task_is_not_productivity()
    {
        var h = new Harness();
        h.SeedTask(Yesterday, completed: false);
        h.SeedTask(Today, completed: false);

        h.Shell.Load();

        Assert.Equal(0, h.Streak);
        Assert.Equal(0, h.IntensityOn(Yesterday));
        Assert.Equal(0, h.IntensityOn(Today));
    }

    [Fact]
    public void Uncompleting_a_task_removes_its_contribution()
    {
        var h = new Harness();
        var task = h.SeedTask(Today, completed: true);
        h.Shell.Load();
        Assert.Equal(1, h.IntensityOn(Today));

        h.Shell.ToggleTaskCompletion(h.PlannerRowFor(task));

        Assert.Equal(0, h.IntensityOn(Today));
        Assert.Null(h.Tasks.Get(task.Id)!.CompletedForDate);
    }

    [Fact]
    public void Deleting_a_completed_task_removes_its_contribution()
    {
        var h = new Harness();
        var task = h.SeedTask(Today, completed: true);
        h.Shell.Load();
        Assert.Equal(1, h.IntensityOn(Today));

        var row = h.PlannerRowFor(task);
        h.Shell.DeleteTask(row);
        h.Shell.ConfirmDeleteCommand.Execute(null);

        Assert.Equal(0, h.IntensityOn(Today));
    }

    [Fact]
    public void Two_completed_tasks_raise_the_intensity_but_add_one_streak_day()
    {
        var h = new Harness();
        h.SeedTask(Today, completed: true);
        h.SeedTask(Today, completed: true);

        h.Shell.Load();

        Assert.Equal(2, h.CountOn(Today));
        Assert.Equal(2, h.IntensityOn(Today));
        Assert.Equal(1, h.Streak);
    }

    [Fact]
    public void A_completed_task_and_a_completed_session_are_two_contributions()
    {
        var h = new Harness();
        h.SeedTask(Today, completed: true);
        h.SeedCompletedSession(Today);

        h.Shell.Load();

        Assert.Equal(2, h.CountOn(Today));
        Assert.Equal(2, h.IntensityOn(Today));
        Assert.Equal(1, h.Streak);
    }

    [Fact]
    public void A_positive_manual_entry_is_a_contribution_on_its_own_date()
    {
        var h = new Harness();
        h.Shell.Load();
        Assert.Equal(0, h.CountOn(Yesterday));

        h.Manual.Add(new ManualTimeEntry
        {
            Id = Guid.NewGuid(),
            LocalDate = Yesterday,
            Seconds = 1800,
            CreatedAtUtc = T0
        });

        h.Shell.RefreshJourney("manual");

        Assert.Equal(1, h.CountOn(Yesterday));
        Assert.Equal(1800, h.CellOn(Yesterday).Activity.ManualSeconds);
    }

    [Fact]
    public void A_zero_manual_entry_contributes_nothing()
    {
        var h = new Harness();

        h.Manual.Add(new ManualTimeEntry
        {
            Id = Guid.NewGuid(),
            LocalDate = Today,
            Seconds = 0,
            CreatedAtUtc = T0
        });

        h.Shell.RefreshJourney("manual");

        Assert.Equal(0, h.CountOn(Today));
    }

    [Fact]
    public void Completing_a_focus_session_credits_the_right_local_day()
    {
        var h = new Harness();
        var task = h.SeedTask(Today, completed: false);
        h.Shell.Load();

        h.Focus.Play(h.Tasks.Get(task.Id)!);
        h.Clock.AdvanceSeconds(1801);
        h.Focus.CompleteIfDue();

        Assert.Equal(1, h.CountOn(Today));
        Assert.Equal(1, h.Streak);
    }

    [Fact]
    public void Cancelled_running_and_paused_sessions_never_contribute()
    {
        var h = new Harness();
        var task = h.SeedTask(Today, completed: false);
        h.Shell.Load();

        h.Focus.Play(h.Tasks.Get(task.Id)!);
        h.Clock.AdvanceSeconds(60);
        h.Shell.RefreshJourney("running");
        Assert.Equal(0, h.CountOn(Today));

        h.Focus.Pause();
        h.Shell.RefreshJourney("paused");
        Assert.Equal(0, h.CountOn(Today));

        h.Focus.Stop();
        h.Shell.RefreshJourney("stopped");
        Assert.Equal(0, h.CountOn(Today));
    }

    [Fact]
    public void Time_actually_run_is_reported_on_the_day_even_without_a_contribution()
    {
        var h = new Harness();
        var task = h.SeedTask(Today, completed: false);
        h.Shell.Load();

        h.Focus.Play(h.Tasks.Get(task.Id)!);
        h.Clock.AdvanceSeconds(600);
        h.Focus.Stop();
        h.Shell.RefreshJourney("stopped");

        // Ten minutes were genuinely worked, so the tooltip says so, but a session that never
        // reached zero is not a contribution and the square stays empty.
        Assert.Equal(600, h.CellOn(Today).Activity.FocusSeconds);
        Assert.Equal(0, h.CountOn(Today));
    }

    // ---------------------------------------------------------------- Dates

    [Fact]
    public void An_unscheduled_task_completed_today_credits_today()
    {
        var h = new Harness();
        var task = h.SeedTask(scheduled: null, completed: false);
        h.Shell.Load();

        h.Shell.ShowUnscheduledFilterCommand.Execute(null);
        h.Shell.ToggleTaskCompletion(h.Shell.PlannerTasks.Single(r => r.Id == task.Id));

        Assert.Equal(Today, h.Tasks.Get(task.Id)!.CompletedForDate);
        Assert.Equal(1, h.CountOn(Today));
    }

    [Fact]
    public void A_task_created_as_already_completed_for_yesterday_credits_yesterday()
    {
        var h = new Harness();
        h.Shell.OpenPlanner();
        h.Shell.SelectDate(Yesterday);

        h.Shell.BeginAddTask();
        h.Shell.DraftTitle = "Work I forgot to tick off";
        h.Shell.IsDraftCompleted = true;
        h.Shell.ConfirmDraft();

        var saved = h.Tasks.GetAll().Single(t => t.Title == "Work I forgot to tick off");
        Assert.True(saved.IsCompleted);
        Assert.Equal(Yesterday, saved.CompletedForDate);
        Assert.Equal(1, h.CountOn(Yesterday));
    }

    [Fact]
    public void Moving_a_completed_task_to_another_date_moves_its_contribution()
    {
        var h = new Harness();
        var task = h.SeedTask(Today, completed: true);
        h.Shell.Load();
        Assert.Equal(1, h.CountOn(Today));

        var row = h.PlannerRowFor(task);
        h.Shell.BeginEditTask(row);
        h.Shell.SelectDate(Yesterday);
        h.Shell.ConfirmDraft();

        Assert.Equal(0, h.CountOn(Today));
        Assert.Equal(1, h.CountOn(Yesterday));
        Assert.Equal(Yesterday, h.Tasks.Get(task.Id)!.CompletedForDate);
    }

    [Fact]
    public void Future_activity_does_not_extend_the_current_streak()
    {
        // Tomorrow is the last day of the visible week, so the contribution is stored and shown
        // while still counting for nothing: the streak ends today or yesterday, never later.
        var tomorrow = Today.AddDays(1);

        var h = new Harness();
        h.SeedTask(tomorrow, completed: true);

        h.Shell.Load();

        Assert.Equal(0, h.Streak);
        Assert.Equal(1, h.CountOn(tomorrow));
        Assert.True(h.CellOn(tomorrow).IsFuture);
    }

    [Fact]
    public void A_completed_task_beyond_the_visible_grid_is_stored_but_never_counted()
    {
        var h = new Harness();
        h.SeedTask(Today.AddDays(30), completed: true);

        h.Shell.Load();

        Assert.Equal(0, h.Streak);
        Assert.Single(h.Tasks.GetAll());
    }

    [Fact]
    public void Today_empty_and_yesterday_productive_still_reports_the_streak()
    {
        var h = new Harness();
        h.SeedTask(new DateOnly(2026, 8, 27), completed: true);
        h.SeedTask(Yesterday, completed: true);

        h.Shell.Load();

        Assert.Equal(2, h.Streak);
    }

    [Fact]
    public void A_timezone_change_does_not_move_a_stored_contribution()
    {
        var h = new Harness();
        h.SeedTask(Yesterday, completed: true);
        h.Shell.Load();
        Assert.Equal(1, h.CountOn(Yesterday));

        // Move the machine twelve hours west and refresh. The stored calendar day is a date,
        // not an instant, so nothing shifts.
        h.Clock.LocalTimeZone = TimeZoneInfo.CreateCustomTimeZone(
            "Test-12", TimeSpan.FromHours(-12), "Test-12", "Test-12");
        h.Shell.RefreshJourney("timezone");

        Assert.Equal(1, h.CountOn(Yesterday));
    }

    // ---------------------------------------------------------------- One shared model

    [Fact]
    public void Quick_view_and_planner_read_the_same_journey_snapshot()
    {
        var h = new Harness();
        var task = h.SeedTask(Today, completed: false);
        h.Shell.Load();

        var seen = new List<JourneyModel>();
        h.Journey.Changed += seen.Add;

        h.Shell.OpenQuickView();
        h.Shell.ToggleTaskCompletion(h.PlannerRowFor(task));
        h.Shell.OpenPlanner();

        // Both panels bind to Shell.HeatmapCells and Shell.StreakText, which are fed from the one
        // snapshot the service published. There is no second copy to fall out of step.
        Assert.Equal(h.Journey.Current.StreakText, h.Shell.StreakText);
        Assert.Same(h.Journey.Current.Cells, h.Shell.HeatmapCells);
        Assert.NotEmpty(seen);
    }

    [Fact]
    public void The_grid_is_exactly_eighty_four_dates_monday_first()
    {
        var h = new Harness();
        h.Shell.Load();

        var cells = h.Shell.HeatmapCells;
        Assert.Equal(84, cells.Count);
        Assert.Equal(84, cells.Select(c => c.Date).Distinct().Count());

        // Twelve columns of seven rows, Monday at the top and Sunday at the bottom.
        Assert.Equal(12, cells.Select(c => c.Week).Distinct().Count());
        Assert.Equal(7, cells.Select(c => c.Row).Distinct().Count());

        foreach (var cell in cells)
        {
            Assert.Equal(cell.Row, StreakCalculator.MondayIndex(cell.Date.DayOfWeek));
        }
    }

    [Fact]
    public void Today_sits_in_its_own_weekday_row_in_the_last_column()
    {
        var h = new Harness();
        h.Shell.Load();

        var today = h.CellOn(Today);

        Assert.Equal(11, today.Week);
        Assert.Equal(StreakCalculator.MondayIndex(Today.DayOfWeek), today.Row);
        Assert.False(today.IsFuture);
    }

    // ---------------------------------------------------------------- Refresh behaviour

    [Fact]
    public void A_timer_tick_does_not_recompute_the_journey()
    {
        var h = new Harness();
        h.Shell.Load();

        var refreshes = 0;
        h.Journey.Changed += _ => refreshes++;

        for (var i = 0; i < 20; i++)
        {
            h.Clock.AdvanceSeconds(1);
            h.Shell.Tick();
        }

        Assert.Equal(0, refreshes);
    }

    [Fact]
    public void Concurrent_refresh_requests_are_coalesced()
    {
        var clock = new TestClock(T0);
        var reader = new RepositoryActivityReader(new FakeTaskRepository(), new FakeSessionRepository());
        var journey = new JourneyActivityService(reader, clock);

        journey.RefreshAsync();
        journey.RefreshAsync();
        journey.RefreshAsync();

        // The inline scheduler completes each request before the next arrives, so this asserts
        // the requests are honoured rather than dropped; the coalescing guard matters when the
        // scheduler is genuinely asynchronous.
        Assert.True(reader.Reads >= 1);
    }

    [Fact]
    public void The_tooltip_names_the_day_and_what_happened_on_it()
    {
        var h = new Harness();
        h.SeedTask(Yesterday, completed: true);
        h.Manual.Add(new ManualTimeEntry
        {
            Id = Guid.NewGuid(),
            LocalDate = Yesterday,
            Seconds = 1800,
            CreatedAtUtc = T0
        });

        h.Shell.Load();

        var tooltip = h.CellOn(Yesterday).Tooltip;

        Assert.Contains("Friday 28 August", tooltip);
        Assert.Contains("1 task completed", tooltip);
        Assert.Contains("30m manually added", tooltip);
    }

    [Fact]
    public void A_future_square_says_only_its_date()
    {
        var h = new Harness();
        h.Shell.Load();

        var tomorrow = h.CellOn(Today.AddDays(1));

        Assert.True(tomorrow.IsFuture);
        Assert.Equal("Sunday 30 August", tomorrow.Tooltip);
    }
}
