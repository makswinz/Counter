using Counter.Core.Focus;
using Counter.Core.Models;
using Xunit;

namespace Counter.Tests;

public class FocusEngineTests
{
    private static readonly DateTime Origin = new(2026, 8, 28, 9, 0, 0, DateTimeKind.Utc);

    private static (FocusEngine Engine, TestClock Clock) Build()
    {
        var clock = new TestClock(Origin);
        return (new FocusEngine(clock), clock);
    }

    // ---------------------------------------------------------------- Countdown

    [Fact]
    public void Remaining_counts_down_from_the_planned_duration()
    {
        var (engine, clock) = Build();
        engine.Start(null, 1800);

        Assert.Equal(TimeSpan.FromSeconds(1800), engine.Remaining);

        clock.AdvanceSeconds(90);
        Assert.Equal(TimeSpan.FromSeconds(1710), engine.Remaining);

        clock.AdvanceSeconds(1709);
        Assert.Equal(TimeSpan.FromSeconds(1), engine.Remaining);
    }

    [Fact]
    public void Remaining_never_goes_negative()
    {
        var (engine, clock) = Build();
        engine.Start(null, 60);

        clock.AdvanceSeconds(5000);

        Assert.True(engine.Remaining >= TimeSpan.Zero);
        Assert.Equal(TimeSpan.Zero, engine.Remaining);
    }

    [Fact]
    public void Running_session_derives_from_an_absolute_target_instant()
    {
        var (engine, clock) = Build();
        var session = engine.Start(null, 600);

        Assert.Equal(Origin.AddSeconds(600), session.TargetUtc);

        // A large jump forward, as after sleep or hibernation, is reflected immediately.
        clock.Advance(TimeSpan.FromMinutes(7));
        Assert.Equal(TimeSpan.FromSeconds(180), engine.Remaining);
    }

    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(59, "00:59")]
    [InlineData(1800, "30:00")]
    [InlineData(3599, "59:59")]
    [InlineData(3600, "1:00:00")]
    [InlineData(7325, "2:02:05")]
    public void Countdown_formats_below_and_above_one_hour(int seconds, string expected)
        => Assert.Equal(expected, TimeFormat.Countdown(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Countdown_clamps_negative_input_to_zero()
        => Assert.Equal("00:00", TimeFormat.Countdown(TimeSpan.FromSeconds(-30)));

    // ---------------------------------------------------------------- Pause and resume

    [Fact]
    public void Pause_captures_the_exact_remaining_time()
    {
        var (engine, clock) = Build();
        engine.Start(null, 1800);

        clock.AdvanceSeconds(300);
        engine.Pause();

        Assert.Equal(FocusSessionStatus.Paused, engine.Current!.Status);
        Assert.Equal(1500, engine.Current.RemainingSecondsWhenPaused);
        Assert.Equal(TimeSpan.FromSeconds(1500), engine.Remaining);
    }

    [Fact]
    public void Paused_time_does_not_drain_while_the_clock_moves()
    {
        var (engine, clock) = Build();
        engine.Start(null, 1800);

        clock.AdvanceSeconds(300);
        engine.Pause();

        clock.Advance(TimeSpan.FromHours(3));

        Assert.Equal(TimeSpan.FromSeconds(1500), engine.Remaining);
    }

    [Fact]
    public void Resume_builds_a_new_target_from_the_saved_remainder()
    {
        var (engine, clock) = Build();
        engine.Start(null, 1800);

        clock.AdvanceSeconds(300);
        engine.Pause();
        clock.Advance(TimeSpan.FromHours(2));
        engine.Resume();

        Assert.Equal(clock.UtcNow.AddSeconds(1500), engine.Current!.TargetUtc);

        clock.AdvanceSeconds(500);
        Assert.Equal(TimeSpan.FromSeconds(1000), engine.Remaining);
    }

    [Fact]
    public void Repeated_pause_and_resume_does_not_drift()
    {
        var (engine, clock) = Build();
        engine.Start(null, 600);

        for (var i = 0; i < 10; i++)
        {
            clock.AdvanceSeconds(30);
            engine.Pause();
            clock.Advance(TimeSpan.FromMinutes(5));
            engine.Resume();
        }

        Assert.Equal(TimeSpan.FromSeconds(300), engine.Remaining);
    }

    // ---------------------------------------------------------------- Restart and expiry

    [Fact]
    public void A_running_session_survives_an_application_restart()
    {
        var (engine, clock) = Build();
        var stored = engine.Start(null, 1800);

        clock.AdvanceSeconds(420);

        // A new process: same clock, a fresh engine, the row that was written to storage.
        var restarted = new FocusEngine(clock);
        restarted.Restore(Clone(stored));

        Assert.Equal(FocusSessionStatus.Running, restarted.Current!.Status);
        Assert.Equal(TimeSpan.FromSeconds(1380), restarted.Remaining);
    }

    [Fact]
    public void A_paused_session_restores_its_remaining_time_exactly()
    {
        var (engine, clock) = Build();
        engine.Start(null, 1800);
        clock.AdvanceSeconds(437);
        engine.Pause();

        var stored = Clone(engine.Current!);

        clock.Advance(TimeSpan.FromDays(2));

        var restarted = new FocusEngine(clock);
        restarted.Restore(stored);

        Assert.Equal(FocusSessionStatus.Paused, restarted.Current!.Status);
        Assert.Equal(TimeSpan.FromSeconds(1363), restarted.Remaining);
    }

    [Fact]
    public void A_session_that_expired_while_the_app_was_closed_is_completed_at_its_target()
    {
        var (engine, clock) = Build();
        var stored = engine.Start(null, 600);
        var target = stored.TargetUtc!.Value;

        // The process is closed and reopened well after the session should have landed.
        clock.Advance(TimeSpan.FromHours(5));

        var restarted = new FocusEngine(clock);
        var completions = new List<FocusSession>();
        restarted.SessionCompleted += completions.Add;
        restarted.Restore(Clone(stored));

        Assert.Single(completions);
        Assert.Equal(FocusSessionStatus.Completed, completions[0].Status);
        Assert.Equal(target, completions[0].CompletedAtUtc);
        Assert.Equal(TimeSpan.Zero, restarted.Remaining);
    }

    // ---------------------------------------------------------------- Completion

    [Fact]
    public void Completion_is_raised_exactly_once_however_often_it_is_polled()
    {
        var (engine, clock) = Build();
        var completions = 0;
        engine.SessionCompleted += _ => completions++;

        engine.Start(null, 60);

        clock.AdvanceSeconds(59);
        Assert.False(engine.Poll());
        Assert.Equal(0, completions);

        clock.AdvanceSeconds(1);
        Assert.True(engine.Poll());

        for (var i = 0; i < 25; i++)
        {
            clock.AdvanceSeconds(1);
            Assert.False(engine.Poll());
        }

        Assert.Equal(1, completions);
    }

    [Fact]
    public void A_completed_session_records_the_full_planned_duration()
    {
        var (engine, clock) = Build();
        engine.Start(null, 300);

        clock.AdvanceSeconds(300);
        engine.Poll();

        Assert.Equal(300, engine.Current!.ElapsedSeconds);
        Assert.Equal(FocusSessionStatus.Completed, engine.Current.Status);
        Assert.Null(engine.Current.CurrentRunStartedAtUtc);
    }

    // ---------------------------------------------------------------- Single active session

    [Fact]
    public void Only_one_focus_session_can_be_active_at_a_time()
    {
        var (engine, _) = Build();
        engine.Start(Guid.NewGuid(), 600);

        Assert.Throws<InvalidOperationException>(() => engine.Start(Guid.NewGuid(), 600));
    }

    [Fact]
    public void Starting_while_paused_is_also_refused()
    {
        var (engine, clock) = Build();
        engine.Start(Guid.NewGuid(), 600);
        clock.AdvanceSeconds(10);
        engine.Pause();

        Assert.Throws<InvalidOperationException>(() => engine.Start(Guid.NewGuid(), 600));
    }

    [Fact]
    public void Switching_cancels_the_previous_session_and_preserves_its_elapsed_time()
    {
        var (engine, clock) = Build();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        var cancelled = new List<FocusSession>();
        engine.SessionPersisted += s =>
        {
            if (s.Status == FocusSessionStatus.Cancelled)
            {
                cancelled.Add(s);
            }
        };

        engine.Start(first, 1800);
        clock.AdvanceSeconds(240);
        engine.Switch(second, 600);

        Assert.Single(cancelled);
        Assert.Equal(240, cancelled[0].ElapsedSeconds);
        Assert.Equal(second, engine.Current!.TaskId);
        Assert.Equal(TimeSpan.FromSeconds(600), engine.Remaining);
    }

    [Fact]
    public void Cancel_clears_the_active_session()
    {
        var (engine, clock) = Build();
        engine.Start(null, 600);
        clock.AdvanceSeconds(30);

        var cancelled = engine.Cancel();

        Assert.NotNull(cancelled);
        Assert.Equal(FocusSessionStatus.Cancelled, cancelled!.Status);
        Assert.Null(engine.Current);
        Assert.False(engine.HasActiveSession);
    }

    [Fact]
    public void A_session_shorter_than_the_minimum_is_refused()
    {
        var (engine, _) = Build();
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.Start(null, 5));
    }

    [Fact]
    public void Restoring_a_completed_session_leaves_nothing_active()
    {
        var (engine, clock) = Build();
        engine.Start(null, 60);
        clock.AdvanceSeconds(60);
        engine.Poll();

        var stored = Clone(engine.Current!);

        var restarted = new FocusEngine(clock);
        restarted.Restore(stored);

        Assert.Null(restarted.Current);
        Assert.False(restarted.HasActiveSession);
    }

    private static FocusSession Clone(FocusSession session) => session.Clone();
}
