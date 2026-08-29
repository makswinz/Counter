using Counter.Core.Focus;
using Counter.Core.Models;
using Xunit;

namespace Counter.Tests;

/// <summary>
/// The play-button contract, asserted against the one service that every caller goes through.
/// </summary>
public class FocusSessionServiceTests
{
    private static readonly DateTime T0 = new(2026, 8, 29, 9, 0, 0, DateTimeKind.Utc);

    private static (FocusSessionService Service, FakeSessionRepository Repo, TestClock Clock) Build()
    {
        var clock = new TestClock(T0);
        var repo = new FakeSessionRepository();
        var service = new FocusSessionService(new FocusEngine(clock), repo, clock);
        return (service, repo, clock);
    }

    private static TaskItem Task(long seconds = 1800, string title = "Task") => new()
    {
        Title = title,
        EstimatedSeconds = seconds,
        CreatedAtUtc = T0,
        UpdatedAtUtc = T0
    };

    private static void AssertOneActive(FakeSessionRepository repo)
        => Assert.True(repo.GetActiveSessions().Count <= 1,
            "More than one session was left running or paused.");

    // ---------------------------------------------------------------- The contract table

    [Fact]
    public void An_idle_task_starts_exactly_one_session()
    {
        var (service, repo, _) = Build();
        var task = Task();

        Assert.Equal(PlayOutcome.Started, service.Play(task));

        Assert.True(service.IsRunning);
        Assert.Equal(task.Id, service.Current!.TaskId);
        Assert.Single(repo.All);
        AssertOneActive(repo);
    }

    [Fact]
    public void The_running_task_pauses()
    {
        var (service, repo, clock) = Build();
        var task = Task();

        service.Play(task);
        clock.AdvanceSeconds(60);

        Assert.Equal(PlayOutcome.Paused, service.Play(task));
        Assert.True(service.IsPaused);
        Assert.Equal(1740, service.Current!.RemainingSecondsWhenPaused);
        AssertOneActive(repo);
    }

    [Fact]
    public void The_paused_task_resumes()
    {
        var (service, repo, clock) = Build();
        var task = Task();

        service.Play(task);
        clock.AdvanceSeconds(60);
        service.Play(task);

        clock.AdvanceSeconds(600);
        Assert.Equal(PlayOutcome.Resumed, service.Play(task));

        Assert.True(service.IsRunning);

        // The pause held the remaining time still while the clock moved on.
        Assert.Equal(1740, (int)Math.Round(service.Remaining.TotalSeconds));
        AssertOneActive(repo);
    }

    [Fact]
    public void Another_task_asks_for_confirmation_instead_of_interrupting()
    {
        var (service, repo, clock) = Build();
        var first = Task(title: "First");
        var second = Task(title: "Second");

        service.Play(first);
        clock.AdvanceSeconds(30);

        Assert.Equal(PlayOutcome.NeedsSwitchConfirmation, service.Play(second));

        // Nothing at all changed.
        Assert.Equal(first.Id, service.Current!.TaskId);
        Assert.True(service.IsRunning);
        AssertOneActive(repo);
    }

    [Fact]
    public void Cancelling_the_switch_preserves_the_current_session()
    {
        var (service, repo, clock) = Build();
        var first = Task(title: "First");
        var second = Task(title: "Second");

        service.Play(first);
        var id = service.Current!.Id;
        clock.AdvanceSeconds(30);

        service.Play(second);           // asks
        // The caller simply does not confirm.

        Assert.Equal(id, service.Current!.Id);
        Assert.True(service.IsRunning);
        Assert.Single(repo.All);
    }

    [Fact]
    public void Confirming_the_switch_cancels_the_old_session_and_starts_the_new_one()
    {
        var (service, repo, clock) = Build();
        var first = Task(title: "First");
        var second = Task(title: "Second");

        service.Play(first);
        var firstId = service.Current!.Id;
        clock.AdvanceSeconds(120);

        Assert.True(service.ConfirmSwitch(second));

        Assert.Equal(second.Id, service.Current!.TaskId);
        Assert.True(service.IsRunning);

        var stored = repo.Get(firstId)!;
        Assert.Equal(FocusSessionStatus.Cancelled, stored.Status);

        // The two minutes that were actually spent are kept.
        Assert.Equal(120, stored.ElapsedSeconds);
        AssertOneActive(repo);
    }

    [Fact]
    public void A_switch_is_written_as_one_transaction()
    {
        var (service, _, clock) = Build();
        var repo = new FakeSessionRepository();
        var svc = new FocusSessionService(new FocusEngine(clock), repo, clock);

        svc.Play(Task(title: "First"));
        clock.AdvanceSeconds(10);

        var writesBefore = repo.WriteCalls;
        svc.ConfirmSwitch(Task(title: "Second"));

        // Ending the old session, closing its run, starting the new one and opening its run
        // all land in a single write. There is no instant at which storage holds two live
        // sessions, none at all, or a run that was never closed.
        Assert.Equal(writesBefore + 1, repo.WriteCalls);
        Assert.Equal(4, repo.LastBatchSize);
        Assert.Single(repo.GetActiveSessions());
        Assert.Single(repo.GetOpenSegments());
    }

    [Fact]
    public void An_invalid_duration_asks_for_the_picker_and_starts_nothing()
    {
        var (service, repo, _) = Build();
        var task = Task(seconds: 5);   // below the minimum

        Assert.Equal(PlayOutcome.NeedsDuration, service.Play(task));
        Assert.False(service.HasActiveSession);
        Assert.Empty(repo.All);
    }

    [Fact]
    public void A_valid_duration_starts_immediately_without_the_picker()
    {
        var (service, _, _) = Build();

        Assert.Equal(PlayOutcome.Started, service.Play(Task(seconds: FocusDefaults.MinimumSeconds)));
        Assert.True(service.IsRunning);
    }

    // ---------------------------------------------------------------- Duplicate presses

    [Fact]
    public void A_double_click_creates_only_one_session_and_does_not_pause_it()
    {
        var (service, repo, clock) = Build();
        var task = Task();

        Assert.Equal(PlayOutcome.Started, service.Play(task));

        // A second press 90 ms later is a stutter, not an instruction.
        clock.Advance(TimeSpan.FromMilliseconds(90));
        Assert.Equal(PlayOutcome.Ignored, service.Play(task));

        Assert.True(service.IsRunning);
        Assert.Single(repo.All);
        AssertOneActive(repo);
    }

    [Fact]
    public void A_deliberate_second_press_after_the_debounce_still_pauses()
    {
        var (service, _, clock) = Build();
        var task = Task();

        service.Play(task);
        clock.Advance(service.PlayDebounce + TimeSpan.FromMilliseconds(1));

        Assert.Equal(PlayOutcome.Paused, service.Play(task));
        Assert.True(service.IsPaused);
    }

    [Fact]
    public void The_debounce_is_per_task_not_global()
    {
        var (service, _, clock) = Build();
        var first = Task(title: "First");
        var second = Task(title: "Second");

        service.Play(first);
        clock.Advance(TimeSpan.FromMilliseconds(50));

        // A press on a different task is never a stutter.
        Assert.Equal(PlayOutcome.NeedsSwitchConfirmation, service.Play(second));
    }

    // ---------------------------------------------------------------- Preview agrees with Play

    [Fact]
    public void Preview_reports_what_a_press_would_do_without_changing_anything()
    {
        var (service, repo, clock) = Build();
        var task = Task();

        Assert.Equal(PlayOutcome.Started, service.Preview(task));
        Assert.Empty(repo.All);

        service.Play(task);
        Assert.Equal(PlayOutcome.Paused, service.Preview(task));

        clock.Advance(service.PlayDebounce + TimeSpan.FromMilliseconds(1));
        service.Play(task);
        Assert.Equal(PlayOutcome.Resumed, service.Preview(task));
    }

    // ---------------------------------------------------------------- One session, always

    [Fact]
    public void Only_one_session_is_active_after_every_operation()
    {
        var (service, repo, clock) = Build();
        var a = Task(title: "A");
        var b = Task(title: "B");
        var c = Task(title: "C");

        foreach (var step in new Action[]
                 {
                     () => service.Play(a),
                     () => service.Pause(),
                     () => service.Resume(),
                     () => service.ConfirmSwitch(b),
                     () => service.Pause(),
                     () => service.ConfirmSwitch(c),
                     () => service.Cancel(),
                     () => service.Play(a)
                 })
        {
            step();
            clock.Advance(TimeSpan.FromSeconds(1));
            AssertOneActive(repo);
        }
    }

    [Fact]
    public void Starting_while_a_session_is_active_is_refused_outright()
    {
        var (service, repo, _) = Build();
        var a = Task(title: "A");
        var b = Task(title: "B");

        service.Play(a);

        Assert.False(service.Start(b));
        Assert.Equal(a.Id, service.Current!.TaskId);
        Assert.Single(repo.All);
    }

    // ---------------------------------------------------------------- Completion

    [Fact]
    public void Completion_happens_exactly_once()
    {
        var (service, _, clock) = Build();
        var completions = 0;
        service.SessionCompleted += _ => completions++;

        service.Play(Task(seconds: 60));
        clock.AdvanceSeconds(59);
        Assert.False(service.CompleteIfDue());

        clock.AdvanceSeconds(2);
        Assert.True(service.CompleteIfDue());
        Assert.False(service.CompleteIfDue());
        Assert.False(service.CompleteIfDue());

        Assert.Equal(1, completions);
    }

    [Fact]
    public void A_completion_is_announced_only_after_it_has_been_written()
    {
        var (service, repo, clock) = Build();
        FocusSession? storedWhenAnnounced = null;

        service.Play(Task(seconds: 60));
        var id = service.Current!.Id;

        service.SessionCompleted += _ => storedWhenAnnounced = repo.Get(id);

        clock.AdvanceSeconds(61);
        service.CompleteIfDue();

        // A listener that reads storage back must already see the finished row.
        Assert.NotNull(storedWhenAnnounced);
        Assert.Equal(FocusSessionStatus.Completed, storedWhenAnnounced!.Status);
        Assert.NotNull(storedWhenAnnounced.CompletedForDate);
    }

    [Fact]
    public void A_completed_session_records_the_local_day_it_finished_on()
    {
        var clock = new TestClock(
            new DateTime(2026, 8, 29, 23, 30, 0, DateTimeKind.Utc),
            TimeZoneInfo.CreateCustomTimeZone("Test+2", TimeSpan.FromHours(2), "Test+2", "Test+2"));

        var repo = new FakeSessionRepository();
        var service = new FocusSessionService(new FocusEngine(clock), repo, clock);

        service.Play(Task(seconds: 3600));
        clock.AdvanceSeconds(3601);
        service.CompleteIfDue();

        // 23:30 UTC + one hour is 00:30 UTC on the 30th, which is 02:30 local on the 30th.
        Assert.Equal(new DateOnly(2026, 8, 30), service.Current is null
            ? repo.All.Single().CompletedForDate
            : repo.All.Single().CompletedForDate);
    }

    // ---------------------------------------------------------------- Persistence failure

    [Fact]
    public void A_failed_write_does_not_leave_a_false_running_state()
    {
        var (service, repo, _) = Build();
        var failures = new List<string>();
        service.PersistenceFailed += (message, _) => failures.Add(message);

        repo.FailWrites = true;
        Assert.Equal(PlayOutcome.Failed, service.Play(Task()));

        Assert.False(service.HasActiveSession);
        Assert.False(service.IsRunning);
        Assert.Empty(repo.All);
        Assert.Single(failures);
    }

    [Fact]
    public void A_failed_pause_leaves_the_session_running_exactly_as_it_was()
    {
        var (service, repo, clock) = Build();
        service.Play(Task());
        var id = service.Current!.Id;

        clock.Advance(service.PlayDebounce + TimeSpan.FromMilliseconds(1));
        repo.FailWrites = true;

        Assert.False(service.Pause());
        Assert.True(service.IsRunning);
        Assert.Equal(id, service.Current!.Id);
    }

    // ---------------------------------------------------------------- Startup repair

    [Fact]
    public void Startup_keeps_the_newest_live_session_and_cancels_the_rest()
    {
        var clock = new TestClock(T0);
        var repo = new FakeSessionRepository();

        var old = new FocusSession
        {
            Id = Guid.NewGuid(),
            Status = FocusSessionStatus.Running,
            PlannedSeconds = 1800,
            StartedAtUtc = T0.AddHours(-3),
            CurrentRunStartedAtUtc = T0.AddHours(-3)
        };

        var newer = new FocusSession
        {
            Id = Guid.NewGuid(),
            Status = FocusSessionStatus.Paused,
            PlannedSeconds = 1800,
            RemainingSecondsWhenPaused = 900,
            ElapsedSeconds = 900,
            StartedAtUtc = T0.AddMinutes(-10)
        };

        repo.Seed(old);
        repo.Seed(newer);

        var service = new FocusSessionService(new FocusEngine(clock), repo, clock);
        var restored = service.Restore();

        Assert.Equal(newer.Id, restored!.Id);
        Assert.Equal(1, service.RepairsApplied);
        Assert.Equal(FocusSessionStatus.Cancelled, repo.Get(old.Id)!.Status);
        AssertOneActive(repo);
    }

    [Fact]
    public void A_repaired_session_keeps_the_time_it_had_accumulated_and_is_never_deleted()
    {
        var clock = new TestClock(T0);
        var repo = new FakeSessionRepository();

        var stale = new FocusSession
        {
            Id = Guid.NewGuid(),
            Status = FocusSessionStatus.Running,
            PlannedSeconds = 1800,
            StartedAtUtc = T0.AddMinutes(-20),
            CurrentRunStartedAtUtc = T0.AddMinutes(-5)
        };

        var newer = new FocusSession
        {
            Id = Guid.NewGuid(),
            Status = FocusSessionStatus.Running,
            PlannedSeconds = 1800,
            StartedAtUtc = T0.AddMinutes(-1),
            CurrentRunStartedAtUtc = T0.AddMinutes(-1)
        };

        repo.Seed(stale);
        repo.Seed(newer);

        var service = new FocusSessionService(new FocusEngine(clock), repo, clock);
        service.Restore();

        var repaired = repo.Get(stale.Id)!;
        Assert.Equal(FocusSessionStatus.Cancelled, repaired.Status);
        Assert.Equal(300, repaired.ElapsedSeconds);
        Assert.Equal(2, repo.All.Count);
    }

    [Fact]
    public void A_clean_database_needs_no_repair()
    {
        var (service, _, _) = Build();

        Assert.Null(service.Restore());
        Assert.Equal(0, service.RepairsApplied);
    }

    // ---------------------------------------------------------------- Shared authority

    [Fact]
    public void Every_caller_sees_the_same_session_state()
    {
        // Quick view, planner, notch, tray and hotkey all hold the same service instance, so
        // this is the assertion that they cannot diverge.
        var (service, _, clock) = Build();
        var task = Task();

        service.Play(task);                                  // as if from the quick view
        Assert.Equal(PlayOutcome.Paused, service.Preview(task));

        clock.Advance(service.PlayDebounce + TimeSpan.FromMilliseconds(1));
        service.Toggle();                                    // as if from the global shortcut

        Assert.True(service.IsPaused);
        Assert.Equal(PlayOutcome.Resumed, service.Preview(task));  // as the planner would draw it
    }

    [Fact]
    public void Toggle_does_nothing_without_a_session()
    {
        var (service, repo, _) = Build();

        Assert.False(service.Toggle());
        Assert.Empty(repo.All);
    }

    [Fact]
    public void Cancelling_for_a_task_only_ends_that_task_s_session()
    {
        var (service, _, _) = Build();
        var a = Task(title: "A");
        var b = Task(title: "B");

        service.Play(a);

        Assert.False(service.CancelFor(b.Id));
        Assert.True(service.HasActiveSession);

        Assert.True(service.CancelFor(a.Id));
        Assert.False(service.HasActiveSession);
    }
}
