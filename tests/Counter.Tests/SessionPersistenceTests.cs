using Counter.Core.Focus;
using Counter.Core.Models;
using Xunit;

namespace Counter.Tests;

/// <summary>
/// End-to-end checks that a session survives the round trip through SQLite, which is what the
/// app actually does on every start, pause, resume and restart.
/// </summary>
public class SessionPersistenceTests
{
    private static readonly DateTime Origin = new(2026, 8, 28, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_running_timer_is_restored_accurately_after_a_restart()
    {
        using var db = new TempDatabase();
        var clock = new TestClock(Origin);

        var first = new FocusEngine(clock);
        first.SessionPersisted += db.Sessions.Save;
        first.Start(null, 1800);

        clock.AdvanceSeconds(654);

        // Process ends here. A new process reads the row back and re-attaches to it.
        var second = new FocusEngine(clock);
        second.SessionPersisted += db.Sessions.Save;
        second.Restore(db.Sessions.GetActive());

        Assert.Equal(FocusSessionStatus.Running, second.Current!.Status);
        Assert.Equal(TimeSpan.FromSeconds(1146), second.Remaining);
    }

    [Fact]
    public void A_paused_timer_is_restored_to_the_exact_same_remainder()
    {
        using var db = new TempDatabase();
        var clock = new TestClock(Origin);

        var first = new FocusEngine(clock);
        first.SessionPersisted += db.Sessions.Save;
        first.Start(null, 1800);
        clock.AdvanceSeconds(321);
        first.Pause();

        clock.Advance(TimeSpan.FromDays(3));

        var second = new FocusEngine(clock);
        second.SessionPersisted += db.Sessions.Save;
        second.Restore(db.Sessions.GetActive());

        Assert.Equal(FocusSessionStatus.Paused, second.Current!.Status);
        Assert.Equal(TimeSpan.FromSeconds(1479), second.Remaining);

        second.Resume();
        clock.AdvanceSeconds(479);
        Assert.Equal(TimeSpan.FromSeconds(1000), second.Remaining);
    }

    [Fact]
    public void A_timer_that_expired_while_the_app_was_closed_is_completed_once_and_recorded()
    {
        using var db = new TempDatabase();
        var clock = new TestClock(Origin);

        var first = new FocusEngine(clock);
        first.SessionPersisted += db.Sessions.Save;
        var started = first.Start(null, 600);
        var target = started.TargetUtc!.Value;

        clock.Advance(TimeSpan.FromHours(9));

        var completions = 0;
        var second = new FocusEngine(clock);
        second.SessionPersisted += db.Sessions.Save;
        second.SessionCompleted += _ => completions++;
        second.Restore(db.Sessions.GetActive());

        Assert.Equal(1, completions);

        // Nothing is left active, and the completion is on record at its true target instant.
        Assert.Null(db.Sessions.GetActive());
        var recorded = db.Sessions.GetCompletionsUtc(Origin.AddDays(-1));
        Assert.Single(recorded);
        Assert.Equal(target, recorded[0]);

        // Polling the restored engine again must not produce a second completion.
        clock.Advance(TimeSpan.FromHours(1));
        Assert.False(second.Poll());
        Assert.Equal(1, completions);
    }

    [Fact]
    public void A_completed_session_feeds_the_streak_that_the_panel_reads_back()
    {
        using var db = new TempDatabase();
        var clock = new TestClock(Origin);

        var engine = new FocusEngine(clock);
        engine.SessionPersisted += db.Sessions.Save;

        engine.Start(null, 60);
        clock.AdvanceSeconds(60);
        engine.Poll();

        var counts = Core.Streaks.StreakCalculator.CountByLocalDay(
            db.Sessions.GetCompletionsUtc(Origin.AddDays(-30)), TimeZoneInfo.Utc);

        Assert.Equal(1, Core.Streaks.StreakCalculator.CurrentStreak(counts, new DateOnly(2026, 8, 28)));
    }

    [Fact]
    public void Switching_tasks_leaves_exactly_one_active_session_in_storage()
    {
        using var db = new TempDatabase();
        var clock = new TestClock(Origin);

        var engine = new FocusEngine(clock);
        engine.SessionPersisted += db.Sessions.Save;

        var first = SqliteSchemaTests.NewTask("First");
        var second = SqliteSchemaTests.NewTask("Second");
        db.Tasks.Add(first);
        db.Tasks.Add(second);

        var firstTask = first.Id;
        var secondTask = second.Id;

        engine.Start(firstTask, 1800);
        clock.AdvanceSeconds(120);
        engine.Switch(secondTask, 900);

        var active = db.Sessions.GetActive();
        Assert.NotNull(active);
        Assert.Equal(secondTask, active!.TaskId);
        Assert.Equal(900, active.PlannedSeconds);
    }
}
