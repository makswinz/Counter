using System.IO;
using FocusNotch.App.Data;
using FocusNotch.Core.Models;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FocusNotch.Tests;

/// <summary>Creates a throwaway database file per test and deletes it afterwards.</summary>
public sealed class TempDatabase : IDisposable
{
    public TempDatabase()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "focusnotch-tests",
            Guid.NewGuid().ToString("N") + ".db");

        Database = new FocusDatabase(Path);
        Database.Migrate();

        Tasks = new SqliteTaskRepository(Database);
        Sessions = new SqliteFocusSessionRepository(Database);
        Settings = new SqliteSettingsStore(Database);
    }

    public string Path { get; }

    public FocusDatabase Database { get; }

    public SqliteTaskRepository Tasks { get; }

    public SqliteFocusSessionRepository Sessions { get; }

    public SqliteSettingsStore Settings { get; }

    public void Dispose()
    {
        Database.Dispose();

        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            try
            {
                if (File.Exists(Path + suffix))
                {
                    File.Delete(Path + suffix);
                }
            }
            catch (IOException)
            {
                // A file still held by the OS is harmless: it lives in the temp directory.
            }
        }
    }
}

public class SqliteSchemaTests
{
    [Fact]
    public void Migrating_an_empty_file_creates_the_schema_at_the_target_version()
    {
        using var db = new TempDatabase();

        Assert.Equal(FocusDatabase.TargetSchemaVersion, db.Database.GetSchemaVersion());
        Assert.True(db.Database.WasCreatedEmpty);

        var tables = ReadTableNames(db.Database);
        Assert.Contains("Tasks", tables);
        Assert.Contains("FocusSessions", tables);
        Assert.Contains("Settings", tables);
    }

    [Fact]
    public void Migrating_again_is_a_no_op_and_keeps_existing_rows()
    {
        using var db = new TempDatabase();

        db.Tasks.Add(NewTask("Keep me"));
        db.Database.Migrate();
        db.Database.Migrate();

        Assert.Single(db.Tasks.GetAll());
        Assert.Equal(FocusDatabase.TargetSchemaVersion, db.Database.GetSchemaVersion());
    }

    [Fact]
    public void A_database_from_a_newer_version_is_refused_without_being_touched()
    {
        var path = Path.Combine(
            Path.GetTempPath(), "focusnotch-tests", Guid.NewGuid().ToString("N") + ".db");

        try
        {
            using (var first = new FocusDatabase(path))
            {
                first.Migrate();
                new SqliteTaskRepository(first).Add(NewTask("Precious"));

                first.Write(connection =>
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = "PRAGMA user_version = 999;";
                    command.ExecuteNonQuery();
                });
            }

            using var second = new FocusDatabase(path);
            Assert.Throws<DatabaseUnavailableException>(() => second.Migrate());

            // The refusal must leave the user's rows exactly where they were.
            Assert.Single(new SqliteTaskRepository(second).GetAll());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDelete(path);
        }
    }

    [Fact]
    public void The_database_file_is_created_along_with_any_missing_directories()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "focusnotch-tests", Guid.NewGuid().ToString("N"), "nested");
        var path = Path.Combine(directory, "focusnotch.db");

        try
        {
            using (var database = new FocusDatabase(path))
            {
                database.Migrate();
            }

            Assert.True(File.Exists(path));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDelete(path);
        }
    }

    [Fact]
    public void Foreign_keys_are_enforced_on_the_connection()
    {
        using var db = new TempDatabase();

        var enabled = db.Database.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys;";
            return Convert.ToInt32(command.ExecuteScalar() ?? 0);
        });

        Assert.Equal(1, enabled);
    }

    private static List<string> ReadTableNames(FocusDatabase database) => database.Read(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
        using var reader = command.ExecuteReader();

        var names = new List<string>();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    });

    internal static TaskItem NewTask(string title) => new()
    {
        Title = title,
        EstimatedSeconds = FocusDefaults.DefaultSeconds,
        CreatedAtUtc = new DateTime(2026, 8, 28, 7, 0, 0, DateTimeKind.Utc),
        UpdatedAtUtc = new DateTime(2026, 8, 28, 7, 0, 0, DateTimeKind.Utc)
    };

    private static void TryDelete(string path)
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            try
            {
                if (File.Exists(path + suffix))
                {
                    File.Delete(path + suffix);
                }
            }
            catch (IOException)
            {
            }
        }
    }
}

public class SqliteTaskRepositoryTests
{
    private static readonly DateTime Now = new(2026, 8, 28, 7, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_created_task_reads_back_with_every_field_intact()
    {
        using var db = new TempDatabase();

        var task = new TaskItem
        {
            Title = "Design daily",
            Note = "Design and brainstorming",
            ScheduledDate = new DateOnly(2026, 8, 28),
            EstimatedSeconds = 1500,
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
            SortOrder = 3
        };

        db.Tasks.Add(task);
        var loaded = db.Tasks.Get(task.Id);

        Assert.NotNull(loaded);
        Assert.Equal(task.Id, loaded!.Id);
        Assert.Equal("Design daily", loaded.Title);
        Assert.Equal("Design and brainstorming", loaded.Note);
        Assert.Equal(new DateOnly(2026, 8, 28), loaded.ScheduledDate);
        Assert.Equal(1500, loaded.EstimatedSeconds);
        Assert.False(loaded.IsCompleted);
        Assert.Null(loaded.CompletedAtUtc);
        Assert.Equal(Now, loaded.CreatedAtUtc);
        Assert.Equal(DateTimeKind.Utc, loaded.CreatedAtUtc.Kind);
        Assert.Equal(3, loaded.SortOrder);
    }

    [Fact]
    public void An_unscheduled_task_keeps_a_null_date()
    {
        using var db = new TempDatabase();

        var task = SqliteSchemaTests.NewTask("Read the WPF notes");
        task.ScheduledDate = null;
        task.Note = null;
        db.Tasks.Add(task);

        var loaded = db.Tasks.Get(task.Id);

        Assert.NotNull(loaded);
        Assert.Null(loaded!.ScheduledDate);
        Assert.Null(loaded.Note);
    }

    [Fact]
    public void Updating_a_task_overwrites_only_that_row()
    {
        using var db = new TempDatabase();

        var first = SqliteSchemaTests.NewTask("First");
        var second = SqliteSchemaTests.NewTask("Second");
        db.Tasks.Add(first);
        db.Tasks.Add(second);

        first.Title = "First, edited";
        first.Note = "With a note now";
        first.EstimatedSeconds = 900;
        first.UpdatedAtUtc = Now.AddMinutes(5);
        db.Tasks.Update(first);

        Assert.Equal("First, edited", db.Tasks.Get(first.Id)!.Title);
        Assert.Equal(900, db.Tasks.Get(first.Id)!.EstimatedSeconds);
        Assert.Equal("Second", db.Tasks.Get(second.Id)!.Title);
    }

    [Fact]
    public void Completing_a_task_records_the_completion_instant()
    {
        using var db = new TempDatabase();

        var task = SqliteSchemaTests.NewTask("Fix bug for Screen");
        db.Tasks.Add(task);

        task.IsCompleted = true;
        task.CompletedAtUtc = Now.AddHours(2);
        task.UpdatedAtUtc = Now.AddHours(2);
        db.Tasks.Update(task);

        var loaded = db.Tasks.Get(task.Id)!;
        Assert.True(loaded.IsCompleted);
        Assert.Equal(Now.AddHours(2), loaded.CompletedAtUtc);

        // Completed tasks are kept, never swept away.
        Assert.Single(db.Tasks.GetAll());
    }

    [Fact]
    public void Marking_a_task_incomplete_again_clears_the_completion_instant()
    {
        using var db = new TempDatabase();

        var task = SqliteSchemaTests.NewTask("Undo me");
        task.IsCompleted = true;
        task.CompletedAtUtc = Now;
        db.Tasks.Add(task);

        task.IsCompleted = false;
        task.CompletedAtUtc = null;
        db.Tasks.Update(task);

        var loaded = db.Tasks.Get(task.Id)!;
        Assert.False(loaded.IsCompleted);
        Assert.Null(loaded.CompletedAtUtc);
    }

    [Fact]
    public void Deleting_a_task_removes_it_and_leaves_the_rest()
    {
        using var db = new TempDatabase();

        var keep = SqliteSchemaTests.NewTask("Keep");
        var drop = SqliteSchemaTests.NewTask("Drop");
        db.Tasks.Add(keep);
        db.Tasks.Add(drop);

        db.Tasks.Delete(drop.Id);

        // Deletion is a soft delete: the row survives so the time recorded against it survives
        // with it, but it leaves every list the interface builds.
        Assert.Single(db.Tasks.GetAll());
        Assert.Equal("Keep", db.Tasks.GetAll()[0].Title);

        var deleted = db.Tasks.Get(drop.Id);
        Assert.NotNull(deleted);
        Assert.True(deleted!.IsDeleted);

        db.Tasks.Restore(drop.Id);
        Assert.Equal(2, db.Tasks.GetAll().Count);
    }

    [Fact]
    public void Tasks_come_back_ordered_by_sort_order()
    {
        using var db = new TempDatabase();

        foreach (var (title, order) in new[] { ("third", 2), ("first", 0), ("second", 1) })
        {
            var task = SqliteSchemaTests.NewTask(title);
            task.SortOrder = order;
            db.Tasks.Add(task);
        }

        Assert.Equal(new[] { "first", "second", "third" }, db.Tasks.GetAll().Select(t => t.Title));
    }

    [Fact]
    public void NextSortOrder_starts_at_zero_and_then_follows_the_highest_row()
    {
        using var db = new TempDatabase();

        Assert.Equal(0, db.Tasks.NextSortOrder());

        var task = SqliteSchemaTests.NewTask("First");
        task.SortOrder = db.Tasks.NextSortOrder();
        db.Tasks.Add(task);

        Assert.Equal(1, db.Tasks.NextSortOrder());
    }

    [Fact]
    public void A_calendar_date_survives_a_timezone_change()
    {
        using var db = new TempDatabase();

        var task = SqliteSchemaTests.NewTask("Scheduled");
        task.ScheduledDate = new DateOnly(2026, 8, 28);
        db.Tasks.Add(task);

        // The stored value is a plain 'yyyy-MM-dd' string with no instant attached to it.
        var raw = db.Database.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT ScheduledDate FROM Tasks WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", task.Id.ToString("D"));
            return command.ExecuteScalar() as string;
        });

        Assert.Equal("2026-08-28", raw);
        Assert.Equal(new DateOnly(2026, 8, 28), db.Tasks.Get(task.Id)!.ScheduledDate);
    }

    [Fact]
    public void A_title_containing_a_quote_is_stored_verbatim()
    {
        using var db = new TempDatabase();

        var task = SqliteSchemaTests.NewTask("Robert'); DROP TABLE Tasks; --");
        db.Tasks.Add(task);

        Assert.Equal("Robert'); DROP TABLE Tasks; --", db.Tasks.Get(task.Id)!.Title);
        Assert.Single(db.Tasks.GetAll());
    }
}

public class SqliteSessionRepositoryTests
{
    private static readonly DateTime Now = new(2026, 8, 28, 7, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Saving_the_same_session_twice_updates_it_rather_than_duplicating_it()
    {
        using var db = new TempDatabase();

        var session = new FocusSession
        {
            Status = FocusSessionStatus.Running,
            PlannedSeconds = 1800,
            StartedAtUtc = Now,
            CurrentRunStartedAtUtc = Now
        };

        db.Sessions.Save(session);

        session.Status = FocusSessionStatus.Paused;
        session.RemainingSecondsWhenPaused = 1200;
        session.ElapsedSeconds = 600;
        session.CurrentRunStartedAtUtc = null;
        db.Sessions.Save(session);

        var loaded = db.Sessions.Get(session.Id)!;
        Assert.Equal(FocusSessionStatus.Paused, loaded.Status);
        Assert.Equal(1200, loaded.RemainingSecondsWhenPaused);
        Assert.Equal(600, loaded.ElapsedSeconds);
        Assert.Null(loaded.CurrentRunStartedAtUtc);
    }

    [Fact]
    public void GetActive_returns_the_running_or_paused_session_and_nothing_else()
    {
        using var db = new TempDatabase();

        db.Sessions.Save(new FocusSession
        {
            Status = FocusSessionStatus.Completed,
            PlannedSeconds = 600,
            StartedAtUtc = Now.AddHours(-3),
            CompletedAtUtc = Now.AddHours(-2)
        });

        db.Sessions.Save(new FocusSession
        {
            Status = FocusSessionStatus.Cancelled,
            PlannedSeconds = 600,
            StartedAtUtc = Now.AddHours(-1)
        });

        var active = new FocusSession
        {
            Status = FocusSessionStatus.Paused,
            PlannedSeconds = 1800,
            RemainingSecondsWhenPaused = 900,
            ElapsedSeconds = 900,
            StartedAtUtc = Now
        };
        db.Sessions.Save(active);

        var loaded = db.Sessions.GetActive();

        Assert.NotNull(loaded);
        Assert.Equal(active.Id, loaded!.Id);
        Assert.Equal(900, loaded.RemainingSecondsWhenPaused);
    }

    [Fact]
    public void GetActive_returns_null_when_nothing_survived_the_last_run()
    {
        using var db = new TempDatabase();
        Assert.Null(db.Sessions.GetActive());
    }

    [Fact]
    public void Completions_come_back_in_order_and_respect_the_since_bound()
    {
        using var db = new TempDatabase();

        foreach (var days in new[] { 1, 3, 20 })
        {
            db.Sessions.Save(new FocusSession
            {
                Status = FocusSessionStatus.Completed,
                PlannedSeconds = 600,
                StartedAtUtc = Now.AddDays(-days),
                CompletedAtUtc = Now.AddDays(-days).AddMinutes(10)
            });
        }

        var recent = db.Sessions.GetCompletionsUtc(Now.AddDays(-7));

        Assert.Equal(2, recent.Count);
        Assert.True(recent[0] < recent[1]);
        Assert.All(recent, c => Assert.Equal(DateTimeKind.Utc, c.Kind));
    }

    [Fact]
    public void Deleting_a_task_keeps_its_sessions_and_the_time_they_recorded()
    {
        using var db = new TempDatabase();

        var task = SqliteSchemaTests.NewTask("Tracked work");
        db.Tasks.Add(task);

        var session = new FocusSession
        {
            TaskId = task.Id,
            Status = FocusSessionStatus.Completed,
            PlannedSeconds = 600,
            StartedAtUtc = Now,
            CompletedAtUtc = Now.AddMinutes(10)
        };
        db.Sessions.Save(session);

        db.Tasks.Delete(task.Id);

        // The task leaves the lists, but its session keeps pointing at it, so the hours spent
        // on it are still attributable in the statistics rather than becoming anonymous.
        var loaded = db.Sessions.Get(session.Id);
        Assert.NotNull(loaded);
        Assert.Equal(task.Id, loaded!.TaskId);
        Assert.Single(db.Sessions.GetCompletionsUtc(Now.AddDays(-1)));
    }
}

public class SqliteSettingsStoreTests
{
    [Fact]
    public void Settings_round_trip_and_fall_back_when_absent()
    {
        using var db = new TempDatabase();

        Assert.True(db.Settings.GetBool(SettingKeys.SoundEnabled, true));
        Assert.Equal(1800, db.Settings.GetInt(SettingKeys.DefaultDurationSeconds, 1800));
        Assert.Null(db.Settings.Get(SettingKeys.MonitorDeviceName));

        db.Settings.SetBool(SettingKeys.SoundEnabled, false);
        db.Settings.SetInt(SettingKeys.DefaultDurationSeconds, 1200);
        db.Settings.Set(SettingKeys.MonitorDeviceName, @"\\.\DISPLAY2");

        Assert.False(db.Settings.GetBool(SettingKeys.SoundEnabled, true));
        Assert.Equal(1200, db.Settings.GetInt(SettingKeys.DefaultDurationSeconds, 1800));
        Assert.Equal(@"\\.\DISPLAY2", db.Settings.Get(SettingKeys.MonitorDeviceName));
    }

    [Fact]
    public void Writing_the_same_key_twice_replaces_the_value()
    {
        using var db = new TempDatabase();

        db.Settings.Set(SettingKeys.MonitorDeviceName, "first");
        db.Settings.Set(SettingKeys.MonitorDeviceName, "second");

        Assert.Equal("second", db.Settings.Get(SettingKeys.MonitorDeviceName));
    }
}

public class DemoDataTests
{
    [Fact]
    public void Demo_content_is_written_into_an_empty_database_only()
    {
        using var db = new TempDatabase();
        var clock = new TestClock(new DateTime(2026, 8, 28, 7, 0, 0, DateTimeKind.Utc));

        Assert.True(DemoData.SeedIfEmpty(db.Tasks, clock));
        var count = db.Tasks.GetAll().Count;
        Assert.True(count > 0);

        // A second call, as on a normal launch, must not add anything.
        Assert.False(DemoData.SeedIfEmpty(db.Tasks, clock));
        Assert.Equal(count, db.Tasks.GetAll().Count);
    }
}
