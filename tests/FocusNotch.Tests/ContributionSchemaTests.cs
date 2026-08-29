using System.IO;
using FocusNotch.App.Data;
using FocusNotch.Core.Models;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FocusNotch.Tests;

/// <summary>
/// Schema 2: the contribution date column, its migration from a schema 1 file, and the queries
/// the journey surface reads.
/// </summary>
public class ContributionSchemaTests
{
    private static readonly DateTime T0 = new(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_fresh_database_is_at_the_current_schema_version()
    {
        using var db = new TempDatabase();
        Assert.Equal(FocusDatabase.TargetSchemaVersion, db.Database.GetSchemaVersion());
        Assert.Equal(3, FocusDatabase.TargetSchemaVersion);
    }

    [Fact]
    public void The_task_contribution_date_round_trips()
    {
        using var db = new TempDatabase();

        var task = new TaskItem
        {
            Title = "Backdated",
            ScheduledDate = new DateOnly(2026, 8, 28),
            EstimatedSeconds = 1800,
            IsCompleted = true,
            CompletedAtUtc = T0,
            CompletedForDate = new DateOnly(2026, 8, 28),
            CreatedAtUtc = T0,
            UpdatedAtUtc = T0
        };

        db.Tasks.Add(task);
        Assert.Equal(new DateOnly(2026, 8, 28), db.Tasks.Get(task.Id)!.CompletedForDate);

        task.CompletedForDate = null;
        task.IsCompleted = false;
        db.Tasks.Update(task);
        Assert.Null(db.Tasks.Get(task.Id)!.CompletedForDate);
    }

    [Fact]
    public void The_session_contribution_date_round_trips()
    {
        using var db = new TempDatabase();

        var session = new FocusSession
        {
            Id = Guid.NewGuid(),
            Status = FocusSessionStatus.Completed,
            PlannedSeconds = 1800,
            ElapsedSeconds = 1800,
            StartedAtUtc = T0,
            CompletedAtUtc = T0,
            CompletedForDate = new DateOnly(2026, 8, 29)
        };

        db.Sessions.Save(session);
        Assert.Equal(new DateOnly(2026, 8, 29), db.Sessions.Get(session.Id)!.CompletedForDate);
    }

    // ---------------------------------------------------------------- Journey queries

    [Fact]
    public void The_task_contribution_query_returns_one_row_per_completed_task()
    {
        using var db = new TempDatabase();
        var day = new DateOnly(2026, 8, 28);

        AddTask(db, day, completed: true, forDate: day);
        AddTask(db, day, completed: true, forDate: day);
        AddTask(db, day, completed: false, forDate: null);
        AddTask(db, new DateOnly(2026, 1, 1), completed: true, forDate: new DateOnly(2026, 1, 1));

        var inWindow = db.Tasks.GetCompletionDates(new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31));

        Assert.Equal(2, inWindow.Count);
        Assert.All(inWindow, d => Assert.Equal(day, d));
    }

    [Fact]
    public void The_session_contribution_query_only_returns_completed_sessions()
    {
        using var db = new TempDatabase();
        var day = new DateOnly(2026, 8, 29);

        AddSession(db, FocusSessionStatus.Completed, day);
        AddSession(db, FocusSessionStatus.Completed, day);
        AddSession(db, FocusSessionStatus.Cancelled, day);
        AddSession(db, FocusSessionStatus.Running, null);
        AddSession(db, FocusSessionStatus.Paused, null);

        var dates = db.Sessions.GetCompletionDates(new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31));

        Assert.Equal(2, dates.Count);
    }

    [Fact]
    public void The_detached_reader_sees_the_same_contributions()
    {
        using var db = new TempDatabase();
        var day = new DateOnly(2026, 8, 28);

        AddTask(db, day, completed: true, forDate: day);
        AddSession(db, FocusSessionStatus.Completed, day);

        var reader = new SqliteActivityReader(db.Database);
        var snapshot = reader.Read(day, day, TimeZoneInfo.Utc);

        Assert.Single(snapshot.Tasks, t => t.IsCompleted && t.CompletedForDate == day);
        Assert.Single(snapshot.Sessions, s => s.CompletedForDate == day);
    }

    // ---------------------------------------------------------------- Transactional writes

    [Fact]
    public void A_batch_of_sessions_is_written_atomically()
    {
        using var db = new TempDatabase();

        var cancelled = NewSession(FocusSessionStatus.Cancelled, null);
        var started = NewSession(FocusSessionStatus.Running, null);

        db.Sessions.SaveAll(new[] { cancelled, started }, Array.Empty<FocusSegment>());

        Assert.Equal(FocusSessionStatus.Cancelled, db.Sessions.Get(cancelled.Id)!.Status);
        Assert.Equal(FocusSessionStatus.Running, db.Sessions.Get(started.Id)!.Status);
        Assert.Single(db.Sessions.GetActiveSessions());
    }

    [Fact]
    public void A_failing_batch_leaves_the_database_untouched()
    {
        using var db = new TempDatabase();

        var good = NewSession(FocusSessionStatus.Running, null);

        // The second row points at a task that does not exist, so the foreign key rejects it.
        var bad = NewSession(FocusSessionStatus.Running, null);
        bad.TaskId = Guid.NewGuid();

        Assert.ThrowsAny<SqliteException>(() => db.Sessions.SaveAll(new[] { good, bad }, Array.Empty<FocusSegment>()));

        Assert.Null(db.Sessions.Get(good.Id));
        Assert.Null(db.Sessions.Get(bad.Id));
        Assert.Empty(db.Sessions.GetActiveSessions());
    }

    [Fact]
    public void Every_live_session_is_returned_newest_first()
    {
        using var db = new TempDatabase();

        var older = NewSession(FocusSessionStatus.Running, null);
        older.StartedAtUtc = T0.AddHours(-2);

        var newer = NewSession(FocusSessionStatus.Paused, null);
        newer.StartedAtUtc = T0;

        db.Sessions.Save(older);
        db.Sessions.Save(newer);

        var active = db.Sessions.GetActiveSessions();

        Assert.Equal(2, active.Count);
        Assert.Equal(newer.Id, active[0].Id);
    }

    // ---------------------------------------------------------------- Migration from schema 1

    [Fact]
    public void Migrating_a_schema_one_file_preserves_the_data_and_backfills_the_dates()
    {
        var path = Path.Combine(
            Path.GetTempPath(), "focusnotch-tests", Guid.NewGuid().ToString("N") + ".db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var taskWithSchedule = Guid.NewGuid();
        var taskWithoutSchedule = Guid.NewGuid();
        var openTask = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        // Build a schema 1 file by hand, exactly as an older build would have left it.
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true
        };

        using (var connection = new SqliteConnection(builder.ToString()))
        {
            connection.Open();

            using (var create = connection.CreateCommand())
            {
                create.CommandText = Schema1Sql;
                create.ExecuteNonQuery();
            }

            InsertLegacyTask(connection, taskWithSchedule, "Scheduled and done", "2026-08-28", 1,
                "2026-08-29T09:00:00.0000000Z");
            InsertLegacyTask(connection, taskWithoutSchedule, "Done, never scheduled", null, 1,
                "2026-08-27T15:00:00.0000000Z");
            InsertLegacyTask(connection, openTask, "Still open", "2026-08-29", 0, null);

            using (var session = connection.CreateCommand())
            {
                session.CommandText = """
                    INSERT INTO FocusSessions
                        (Id, TaskId, Status, PlannedSeconds, RemainingSecondsWhenPaused,
                         StartedAtUtc, CurrentRunStartedAtUtc, CompletedAtUtc, ElapsedSeconds)
                    VALUES ($id, NULL, 2, 1800, NULL, $started, NULL, $completed, 1800);
                    """;
                session.Parameters.AddWithValue("$id", sessionId.ToString("D"));
                session.Parameters.AddWithValue("$started", "2026-08-26T08:00:00.0000000Z");
                session.Parameters.AddWithValue("$completed", "2026-08-26T08:30:00.0000000Z");
                session.ExecuteNonQuery();
            }

            using (var version = connection.CreateCommand())
            {
                version.CommandText = "PRAGMA user_version = 1;";
                version.ExecuteNonQuery();
            }
        }

        SqliteConnection.ClearAllPools();

        try
        {
            using var database = new FocusDatabase(path);
            database.Migrate();

            Assert.Equal(3, database.GetSchemaVersion());

            var tasks = new SqliteTaskRepository(database);
            var sessions = new SqliteFocusSessionRepository(database);

            // Nothing was lost.
            Assert.Equal(3, tasks.GetAll().Count);

            // A completed task with a scheduled day keeps that day.
            Assert.Equal(new DateOnly(2026, 8, 28), tasks.Get(taskWithSchedule)!.CompletedForDate);

            // A completed task with no scheduled day falls back to its local completion day.
            var expectedLocal = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(
                new DateTime(2026, 8, 27, 15, 0, 0, DateTimeKind.Utc), TimeZoneInfo.Local));
            Assert.Equal(expectedLocal, tasks.Get(taskWithoutSchedule)!.CompletedForDate);

            // An unfinished task gets nothing and therefore contributes nothing.
            Assert.Null(tasks.Get(openTask)!.CompletedForDate);

            var expectedSessionLocal = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(
                new DateTime(2026, 8, 26, 8, 30, 0, DateTimeKind.Utc), TimeZoneInfo.Local));
            Assert.Equal(expectedSessionLocal, sessions.Get(sessionId)!.CompletedForDate);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
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
                    // Harmless: the file lives in the temp directory.
                }
            }
        }
    }

    [Fact]
    public void Migrating_an_already_current_database_is_a_no_op()
    {
        using var db = new TempDatabase();
        var task = AddTask(db, new DateOnly(2026, 8, 28), completed: true, forDate: new DateOnly(2026, 8, 28));

        db.Database.Migrate();
        db.Database.Migrate();

        Assert.Equal(3, db.Database.GetSchemaVersion());
        Assert.Equal(new DateOnly(2026, 8, 28), db.Tasks.Get(task)!.CompletedForDate);
    }

    // ---------------------------------------------------------------- Helpers

    private static Guid AddTask(TempDatabase db, DateOnly? scheduled, bool completed, DateOnly? forDate)
    {
        var task = new TaskItem
        {
            Title = "Task",
            ScheduledDate = scheduled,
            EstimatedSeconds = 1800,
            IsCompleted = completed,
            CompletedAtUtc = completed ? T0 : null,
            CompletedForDate = forDate,
            CreatedAtUtc = T0,
            UpdatedAtUtc = T0
        };

        db.Tasks.Add(task);
        return task.Id;
    }

    private static FocusSession NewSession(FocusSessionStatus status, DateOnly? forDate) => new()
    {
        Id = Guid.NewGuid(),
        Status = status,
        PlannedSeconds = 1800,
        StartedAtUtc = T0,
        CurrentRunStartedAtUtc = status == FocusSessionStatus.Running ? T0 : null,
        CompletedAtUtc = status == FocusSessionStatus.Completed ? T0 : null,
        CompletedForDate = forDate
    };

    private static void AddSession(TempDatabase db, FocusSessionStatus status, DateOnly? forDate)
        => db.Sessions.Save(NewSession(status, forDate));

    private static void InsertLegacyTask(
        SqliteConnection connection, Guid id, string title, string? scheduled, int completed, string? completedAt)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Tasks
                (Id, Title, Note, ScheduledDate, EstimatedSeconds, IsCompleted,
                 CompletedAtUtc, CreatedAtUtc, UpdatedAtUtc, SortOrder)
            VALUES ($id, $title, NULL, $scheduled, 1800, $completed,
                    $completedAt, $created, $created, 0);
            """;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$scheduled", (object?)scheduled ?? DBNull.Value);
        command.Parameters.AddWithValue("$completed", completed);
        command.Parameters.AddWithValue("$completedAt", (object?)completedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", "2026-08-01T00:00:00.0000000Z");
        command.ExecuteNonQuery();
    }

    private const string Schema1Sql = """
        CREATE TABLE Tasks (
            Id               TEXT    NOT NULL PRIMARY KEY,
            Title            TEXT    NOT NULL,
            Note             TEXT    NULL,
            ScheduledDate    TEXT    NULL,
            EstimatedSeconds INTEGER NOT NULL,
            IsCompleted      INTEGER NOT NULL DEFAULT 0,
            CompletedAtUtc   TEXT    NULL,
            CreatedAtUtc     TEXT    NOT NULL,
            UpdatedAtUtc     TEXT    NOT NULL,
            SortOrder        INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE FocusSessions (
            Id                         TEXT    NOT NULL PRIMARY KEY,
            TaskId                     TEXT    NULL REFERENCES Tasks (Id) ON DELETE SET NULL,
            Status                     INTEGER NOT NULL,
            PlannedSeconds             INTEGER NOT NULL,
            RemainingSecondsWhenPaused INTEGER NULL,
            StartedAtUtc               TEXT    NOT NULL,
            CurrentRunStartedAtUtc     TEXT    NULL,
            CompletedAtUtc             TEXT    NULL,
            ElapsedSeconds             INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE Settings (
            Key   TEXT NOT NULL PRIMARY KEY,
            Value TEXT NOT NULL
        );
        """;
}
