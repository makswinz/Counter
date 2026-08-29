using System.IO;
using FocusNotch.App.Data;
using FocusNotch.App.Services;
using FocusNotch.App.Theme;
using FocusNotch.App.ViewModels;
using FocusNotch.Core.Drafts;
using FocusNotch.Core.Focus;
using FocusNotch.Core.Journey;
using FocusNotch.Core.Models;
using FocusNotch.Core.Statistics;
using Xunit;

namespace FocusNotch.Tests;

/// <summary>
/// What the application remembers when it is not running.
///
/// A closed process cannot count down, so the whole design rests on storing an absolute target
/// instant and reconciling against it at the next launch. These tests close and reopen the
/// service around a clock the test moves by hand, which is exactly what "the app was shut for
/// forty minutes" means from the data's point of view.
/// </summary>
public class MemoryTests
{
    private static readonly DateTime T0 = new(2026, 8, 29, 9, 0, 0, DateTimeKind.Utc);

    private static TaskItem Task(long seconds = 1800) => new()
    {
        Title = "Deep work",
        EstimatedSeconds = seconds,
        CreatedAtUtc = T0,
        UpdatedAtUtc = T0
    };

    /// <summary>Rebuilds the service over the same storage, exactly as a relaunch would.</summary>
    private static FocusSessionService Relaunch(FakeSessionRepository repo, TestClock clock)
        => new(new FocusEngine(clock), repo, clock);

    // ================================================================ The timer

    [Fact]
    public void A_running_timer_survives_exit_and_comes_back_with_the_right_remaining_time()
    {
        var clock = new TestClock(T0);
        var repo = new FakeSessionRepository();
        var first = new FocusSessionService(new FocusEngine(clock), repo, clock);

        first.Play(Task(1800));
        clock.AdvanceSeconds(600);

        // The process ends here. Nothing else is written.
        var second = Relaunch(repo, clock);
        clock.AdvanceSeconds(300);
        second.Restore();

        Assert.True(second.IsRunning);
        Assert.Equal(900, (long)Math.Round(second.Remaining.TotalSeconds));
        Assert.Null(second.CompletedWhileClosed);
    }

    [Fact]
    public void A_timer_that_expired_while_the_app_was_closed_is_finished_at_its_target()
    {
        var clock = new TestClock(T0);
        var repo = new FakeSessionRepository();
        var first = new FocusSessionService(new FocusEngine(clock), repo, clock);

        first.Play(Task(600));

        // Two hours pass with the app shut.
        clock.AdvanceSeconds(7200);

        var second = Relaunch(repo, clock);
        second.Restore();

        var session = Assert.Single(repo.All);
        Assert.Equal(FocusSessionStatus.Completed, session.Status);
        Assert.Equal(SessionEndReason.Completed, session.EndReason);

        // Completed at the target instant, not at the moment the app noticed.
        Assert.Equal(T0.AddSeconds(600), session.CompletedAtUtc);
        Assert.Equal(new DateOnly(2026, 8, 29), session.CompletedForDate);

        // Only the planned time was credited.
        var spans = TimeLedger.ToSpans(repo.AllSegments, clock.UtcNow);
        Assert.Equal(600, TimeLedger.TotalSeconds(spans));

        Assert.NotNull(second.CompletedWhileClosed);
    }

    [Fact]
    public void An_expired_offline_timer_is_reconciled_exactly_once()
    {
        var clock = new TestClock(T0);
        var repo = new FakeSessionRepository();
        new FocusSessionService(new FocusEngine(clock), repo, clock).Play(Task(600));
        clock.AdvanceSeconds(7200);

        var completions = 0;

        var second = Relaunch(repo, clock);
        second.SessionCompleted += _ => completions++;
        second.Restore();

        Assert.Equal(1, completions);

        // And a third launch must not announce it again.
        var third = Relaunch(repo, clock);
        var again = 0;
        third.SessionCompleted += _ => again++;
        third.Restore();

        Assert.Equal(0, again);
        Assert.Null(third.CompletedWhileClosed);
    }

    [Fact]
    public void A_paused_timer_restores_with_exactly_the_saved_remaining_time()
    {
        var clock = new TestClock(T0);
        var repo = new FakeSessionRepository();
        var first = new FocusSessionService(new FocusEngine(clock), repo, clock);

        first.Play(Task(1800));
        clock.AdvanceSeconds(500);
        first.Pause();

        // Days go by with the app closed. A paused timer does not drain.
        clock.Advance(TimeSpan.FromDays(3));

        var second = Relaunch(repo, clock);
        second.Restore();

        Assert.True(second.IsPaused);
        Assert.Equal(1300, (long)Math.Round(second.Remaining.TotalSeconds));
    }

    [Fact]
    public void A_stopped_session_is_never_restarted()
    {
        var clock = new TestClock(T0);
        var repo = new FakeSessionRepository();
        var first = new FocusSessionService(new FocusEngine(clock), repo, clock);

        first.Play(Task(1800));
        clock.AdvanceSeconds(120);
        first.Stop(SessionEndReason.StoppedByUser);

        clock.AdvanceSeconds(3600);

        var second = Relaunch(repo, clock);
        second.Restore();

        Assert.False(second.HasActiveSession);
        Assert.Null(second.Current);
    }

    [Fact]
    public void A_run_left_open_by_a_crash_is_closed_rather_than_left_growing()
    {
        var clock = new TestClock(T0);
        var repo = new FakeSessionRepository();

        // A session that ended without its run being closed: the process died in between.
        var session = new FocusSession
        {
            Id = Guid.NewGuid(),
            Status = FocusSessionStatus.Cancelled,
            EndReason = SessionEndReason.StoppedByUser,
            PlannedSeconds = 1800,
            ElapsedSeconds = 300,
            StartedAtUtc = T0,
            CurrentRunStartedAtUtc = null
        };

        repo.Seed(session);
        repo.SeedSegment(new FocusSegment
        {
            Id = Guid.NewGuid(),
            SessionId = session.Id,
            StartedAtUtc = T0,
            EndedAtUtc = null
        });

        clock.AdvanceSeconds(86400);

        var service = Relaunch(repo, clock);
        service.Restore();

        Assert.Equal(1, service.SegmentsRepaired);
        var repaired = Assert.Single(repo.AllSegments);
        Assert.False(repaired.IsOpen);

        // Closed at the planned end, not a day later.
        Assert.Equal(1800, repaired.SecondsAt(clock.UtcNow));
    }

    [Fact]
    public void More_than_one_live_session_is_repaired_and_the_newest_kept()
    {
        var clock = new TestClock(T0);
        var repo = new FakeSessionRepository();

        var older = new FocusSession
        {
            Id = Guid.NewGuid(),
            Status = FocusSessionStatus.Running,
            PlannedSeconds = 1800,
            StartedAtUtc = T0.AddHours(-2),
            CurrentRunStartedAtUtc = T0.AddHours(-2)
        };

        var newer = new FocusSession
        {
            Id = Guid.NewGuid(),
            Status = FocusSessionStatus.Paused,
            PlannedSeconds = 1800,
            RemainingSecondsWhenPaused = 900,
            ElapsedSeconds = 900,
            StartedAtUtc = T0
        };

        repo.Seed(older);
        repo.Seed(newer);

        var service = Relaunch(repo, clock);
        service.Restore();

        Assert.Equal(1, service.RepairsApplied);
        Assert.Equal(newer.Id, service.Current!.Id);
        Assert.Equal(SessionEndReason.RepairedDuplicate, repo.Get(older.Id)!.EndReason);

        // Nothing was deleted: the repaired session is still there, with its time.
        Assert.Equal(2, repo.All.Count);
    }

    // ================================================================ Settings and drafts

    [Fact]
    public void A_draft_is_written_after_a_pause_in_typing_and_restored_after_a_crash()
    {
        var h = new ShellHarness();

        h.Shell.BeginAddTask();
        h.Shell.DraftTitle = "Half-written";
        h.Shell.DraftNote = "and a note";

        // Still typing: nothing has been written yet.
        h.Shell.Tick();
        Assert.Null(h.Settings.Get(SettingKeys.DraftTitle));

        h.Clock.Advance(TimeSpan.FromSeconds(1));
        h.Shell.Tick();
        Assert.Equal("Half-written", h.Settings.Get(SettingKeys.DraftTitle));

        // The process dies. A new shell over the same settings brings it back.
        var next = new ShellHarness(h.Settings);
        Assert.True(next.Shell.RestoreDraft());
        Assert.Equal("Half-written", next.Shell.DraftTitle);
        Assert.Equal("and a note", next.Shell.DraftNote);
        Assert.True(next.Shell.IsAddingTask);
        Assert.Equal(PanelLevel.Planner, next.Shell.Panel);
    }

    [Fact]
    public void Saving_the_draft_clears_the_recovery_copy()
    {
        var h = new ShellHarness();

        h.Shell.BeginAddTask();
        h.Shell.DraftTitle = "Real task";
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        h.Shell.Tick();
        Assert.Equal("Real task", h.Settings.Get(SettingKeys.DraftTitle));

        h.Shell.ConfirmDraft();

        Assert.Equal(string.Empty, h.Settings.Get(SettingKeys.DraftTitle));
        Assert.False(new ShellHarness(h.Settings).Shell.RestoreDraft());
    }

    [Fact]
    public void Cancelling_the_draft_clears_the_recovery_copy_too()
    {
        var h = new ShellHarness();

        h.Shell.BeginAddTask();
        h.Shell.DraftTitle = "Never mind";
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        h.Shell.Tick();

        h.Shell.CancelDraft();

        Assert.False(new ShellHarness(h.Settings).Shell.RestoreDraft());
    }

    [Fact]
    public void An_empty_draft_is_never_offered_for_recovery()
    {
        var settings = new FakeSettingsStore();
        new DraftStore(settings).Save(new TaskDraft("   ", "", false, null, null));

        Assert.False(new ShellHarness(settings).Shell.RestoreDraft());
    }

    [Fact]
    public void The_selected_day_the_filter_and_the_statistics_range_all_survive_a_restart()
    {
        var h = new ShellHarness();
        var day = new DateOnly(2026, 7, 14);

        h.Shell.SelectDate(day);
        h.Shell.ShowUnscheduledFilterCommand.Execute(null);
        h.Shell.Statistics.SelectLast30Command.Execute(null);

        var next = new ShellHarness(h.Settings);

        Assert.Equal(PlannerFilter.Unscheduled, next.Shell.Filter);
        Assert.Equal(StatisticsRange.Last30Days, next.Shell.Statistics.Range);

        // The filter is unscheduled, so the day itself is what was stored before it changed.
        Assert.Equal(day, next.Shell.SelectedDate);
    }

    [Fact]
    public void The_theme_choice_survives_a_restart()
    {
        var settings = new FakeSettingsStore();
        settings.Set(SettingKeys.Theme, "Light");

        Assert.Equal(ThemePreference.Light, ThemePalette.Parse(settings.Get(SettingKeys.Theme)));
    }

    // ================================================================ Backups

    [Fact]
    public void A_backup_is_taken_at_most_once_a_day()
    {
        var settings = new FakeSettingsStore();
        using var backups = new TempDirectory();
        using var db = new TempDatabase();

        Assert.True(DatabaseMaintenance.BackupIfDue(db.Database, settings, T0, backups.Path));

        // An hour later, nothing is due.
        Assert.False(DatabaseMaintenance.BackupIfDue(db.Database, settings, T0.AddHours(1), backups.Path));

        // The next day, one is.
        Assert.True(DatabaseMaintenance.BackupIfDue(db.Database, settings, T0.AddHours(25), backups.Path));

        Assert.Equal(2, Directory.GetFiles(backups.Path, "focusnotch-*.db").Length);
    }

    [Fact]
    public void Only_the_seven_most_recent_backups_are_kept()
    {
        var settings = new FakeSettingsStore();
        using var backups = new TempDirectory();
        using var db = new TempDatabase();

        for (var day = 0; day < 12; day++)
        {
            DatabaseMaintenance.BackupIfDue(db.Database, settings, T0.AddDays(day), backups.Path);
        }

        Assert.Equal(
            DatabaseMaintenance.MaxBackups,
            Directory.GetFiles(backups.Path, "focusnotch-*.db").Length);
    }

    [Fact]
    public void A_backup_does_not_disturb_the_live_database()
    {
        var settings = new FakeSettingsStore();
        using var backups = new TempDirectory();
        using var db = new TempDatabase();

        var task = SqliteSchemaTests.NewTask("Still here");
        db.Tasks.Add(task);

        Assert.True(DatabaseMaintenance.BackupIfDue(db.Database, settings, T0, backups.Path));

        Assert.Single(db.Tasks.GetAll());
        Assert.Equal("Still here", db.Tasks.GetAll()[0].Title);
        Assert.Null(DatabaseMaintenance.CheckIntegrity(db.Database));

        // And the copy is a real, readable database holding the same row.
        var copy = Directory.GetFiles(backups.Path, "focusnotch-*.db").Single();
        using var restored = new FocusDatabase(copy);
        Assert.Equal("Still here", new SqliteTaskRepository(restored).GetAll().Single().Title);
    }

    /// <summary>A disposable folder, so no test ever writes into the real data directory.</summary>
    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "focusnotch-tests", Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // Harmless: the folder lives in the temp directory.
            }
        }
    }

    [Fact]
    public void A_healthy_database_reports_no_integrity_problem()
    {
        using var db = new TempDatabase();
        Assert.Null(DatabaseMaintenance.CheckIntegrity(db.Database));
    }

    /// <summary>A shell over in-memory storage, so persistence can be asserted without a file.</summary>
    internal sealed class ShellHarness
    {
        public ShellHarness(FakeSettingsStore? settings = null)
        {
            Clock = new TestClock(T0);
            Settings = settings ?? new FakeSettingsStore();
            Tasks = new FakeTaskRepository();
            Sessions = new FakeSessionRepository();
            Manual = new FakeManualTimeRepository();

            var reader = new RepositoryActivityReader(Tasks, Sessions, Manual);
            var focus = new FocusSessionService(new FocusEngine(Clock), Sessions, Clock);

            Shell = new ShellViewModel(
                Tasks,
                Manual,
                Settings,
                focus,
                new JourneyActivityService(reader, Clock),
                new StatisticsService(reader, Clock),
                reader,
                Clock);

            Shell.Load();
        }

        public TestClock Clock { get; }

        public FakeSettingsStore Settings { get; }

        public FakeTaskRepository Tasks { get; }

        public FakeSessionRepository Sessions { get; }

        public FakeManualTimeRepository Manual { get; }

        public ShellViewModel Shell { get; }
    }
}
