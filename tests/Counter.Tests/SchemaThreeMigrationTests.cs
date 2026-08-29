using System.IO;
using Counter.App.Data;
using Counter.Core.Focus;
using Counter.Core.Models;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Counter.Tests;

/// <summary>
/// Migrating a schema 2 file to schema 3.
///
/// The whole point of this migration is that it adds and never rewrites. Every assertion here
/// is about something that was already in the file still being in it afterwards, plus the one
/// thing the migration is allowed to create: a reconstructed run for each session that had
/// recorded time, so somebody's existing hours do not silently become zero.
/// </summary>
public class SchemaThreeMigrationTests
{
    /// <summary>The schema exactly as version 2 left it.</summary>
    private const string Schema2Sql = """
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
            SortOrder        INTEGER NOT NULL DEFAULT 0,
            CompletedForDate TEXT    NULL
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
            ElapsedSeconds             INTEGER NOT NULL DEFAULT 0,
            CompletedForDate           TEXT    NULL
        );

        CREATE TABLE Settings (
            Key   TEXT NOT NULL PRIMARY KEY,
            Value TEXT NOT NULL
        );
        """;

    [Fact]
    public void Every_task_and_session_survives_and_historical_time_is_reconstructed()
    {
        var path = TempPath();

        var task = Guid.NewGuid();
        var completedSession = Guid.NewGuid();
        var stoppedSession = Guid.NewGuid();
        var emptySession = Guid.NewGuid();
        var runningSession = Guid.NewGuid();

        BuildSchemaTwoFile(path, connection =>
        {
            InsertTask(connection, task, "Long-running work", 7200, "2026-08-28", 1, "2026-08-28");

            // Thirty minutes recorded, completed.
            InsertSession(connection, completedSession, task, status: 2, planned: 1800,
                started: "2026-08-26T08:00:00.0000000Z", elapsed: 1800,
                completedAt: "2026-08-26T08:30:00.0000000Z", completedFor: "2026-08-26");

            // Ten minutes recorded, then cancelled.
            InsertSession(connection, stoppedSession, task, status: 3, planned: 1800,
                started: "2026-08-27T08:00:00.0000000Z", elapsed: 600);

            // Cancelled immediately: no time at all, so nothing to reconstruct.
            InsertSession(connection, emptySession, task, status: 3, planned: 1800,
                started: "2026-08-27T09:00:00.0000000Z", elapsed: 0);

            // Still running when the app last closed.
            InsertSession(connection, runningSession, task, status: 0, planned: 3600,
                started: "2026-08-29T07:00:00.0000000Z", elapsed: 0,
                runStarted: "2026-08-29T07:00:00.0000000Z");
        });

        try
        {
            using var database = new FocusDatabase(path);
            database.Migrate();

            Assert.Equal(3, database.GetSchemaVersion());

            var tasks = new SqliteTaskRepository(database);
            var sessions = new SqliteFocusSessionRepository(database);

            // Nothing was lost or altered.
            var loadedTask = tasks.Get(task);
            Assert.NotNull(loadedTask);
            Assert.Equal("Long-running work", loadedTask!.Title);
            Assert.Equal(7200, loadedTask.EstimatedSeconds);
            Assert.Equal(new DateOnly(2026, 8, 28), loadedTask.CompletedForDate);
            Assert.False(loadedTask.IsDeleted);

            // The completed session keeps everything and gains the reason its status implies.
            var completed = sessions.Get(completedSession)!;
            Assert.Equal(FocusSessionStatus.Completed, completed.Status);
            Assert.Equal(SessionEndReason.Completed, completed.EndReason);
            Assert.Equal("Long-running work", completed.TaskTitle);

            // A cancelled session from before the column genuinely does not say why it ended,
            // and the migration does not invent a reason for it.
            Assert.Equal(SessionEndReason.None, sessions.Get(stoppedSession)!.EndReason);

            // The time that was recorded is now a run, so it still shows as time spent.
            AssertRun(sessions, completedSession, 1800);
            AssertRun(sessions, stoppedSession, 600);
            Assert.Empty(sessions.GetSegments(emptySession));

            // The session that was live gets an open run from where it was running.
            var live = Assert.Single(sessions.GetSegments(runningSession));
            Assert.True(live.IsOpen);
            Assert.Equal(
                DateTime.Parse("2026-08-29T07:00:00.0000000Z").ToUniversalTime(),
                live.StartedAtUtc);

            // And the new tables exist and are usable.
            var manual = new SqliteManualTimeRepository(database);
            Assert.Empty(manual.GetInRange(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31)));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void A_duration_beyond_the_old_limit_round_trips_through_the_migrated_schema()
    {
        var path = TempPath();
        var task = Guid.NewGuid();

        BuildSchemaTwoFile(path, connection =>
            // 99:59:59, far past the old two-hundred-and-forty-minute ceiling. SQLite INTEGER is
            // already 64-bit, so this needs no column change - only code that stops narrowing.
            InsertTask(connection, task, "Marathon", 359999, null, 0, null));

        try
        {
            using var database = new FocusDatabase(path);
            database.Migrate();

            Assert.Equal(359999L, new SqliteTaskRepository(database).Get(task)!.EstimatedSeconds);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Running_the_migration_twice_changes_nothing()
    {
        var path = TempPath();
        var task = Guid.NewGuid();
        var session = Guid.NewGuid();

        BuildSchemaTwoFile(path, connection =>
        {
            InsertTask(connection, task, "Work", 1800, "2026-08-28", 0, null);
            InsertSession(connection, session, task, status: 3, planned: 1800,
                started: "2026-08-27T08:00:00.0000000Z", elapsed: 600);
        });

        try
        {
            using var database = new FocusDatabase(path);
            database.Migrate();
            database.Migrate();

            var sessions = new SqliteFocusSessionRepository(database);

            // One reconstructed run, not two.
            Assert.Single(sessions.GetSegments(session));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void A_failed_migration_leaves_the_file_exactly_as_it_was()
    {
        var path = TempPath();

        // A file that claims to be schema 2 but is missing the table the migration alters.
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate
        };

        using (var connection = new SqliteConnection(builder.ToString()))
        {
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText =
                "CREATE TABLE Settings (Key TEXT NOT NULL PRIMARY KEY, Value TEXT NOT NULL); " +
                "INSERT INTO Settings (Key, Value) VALUES ('irreplaceable', 'still here'); " +
                "PRAGMA user_version = 2;";
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();

        try
        {
            using var database = new FocusDatabase(path);

            Assert.Throws<DatabaseUnavailableException>(() => database.Migrate());

            // The file was neither deleted nor recreated, and its contents are untouched.
            Assert.Equal(2, database.GetSchemaVersion());
            Assert.Equal("still here", new SqliteSettingsStore(database).Get("irreplaceable"));
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ---------------------------------------------------------------------------------

    private static void AssertRun(SqliteFocusSessionRepository sessions, Guid sessionId, long seconds)
    {
        var run = Assert.Single(sessions.GetSegments(sessionId));
        Assert.False(run.IsOpen);

        // A second either way: the reconstruction goes through SQLite's julianday arithmetic.
        Assert.InRange(run.SecondsAt(DateTime.UtcNow), seconds - 1, seconds + 1);
    }

    private static string TempPath()
    {
        var path = Path.Combine(
            Path.GetTempPath(), "counter-tests", Guid.NewGuid().ToString("N") + ".db");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    private static void BuildSchemaTwoFile(string path, Action<SqliteConnection> seed)
    {
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
                create.CommandText = Schema2Sql;
                create.ExecuteNonQuery();
            }

            seed(connection);

            using var version = connection.CreateCommand();
            version.CommandText = "PRAGMA user_version = 2;";
            version.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
    }

    private static void InsertTask(
        SqliteConnection connection, Guid id, string title, long estimated,
        string? scheduled, int completed, string? completedFor)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Tasks
                (Id, Title, Note, ScheduledDate, EstimatedSeconds, IsCompleted,
                 CompletedAtUtc, CreatedAtUtc, UpdatedAtUtc, SortOrder, CompletedForDate)
            VALUES ($id, $title, NULL, $scheduled, $estimated, $completed,
                    NULL, $now, $now, 0, $completedFor);
            """;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$scheduled", (object?)scheduled ?? DBNull.Value);
        command.Parameters.AddWithValue("$estimated", estimated);
        command.Parameters.AddWithValue("$completed", completed);
        command.Parameters.AddWithValue("$completedFor", (object?)completedFor ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", "2026-08-20T08:00:00.0000000Z");
        command.ExecuteNonQuery();
    }

    private static void InsertSession(
        SqliteConnection connection, Guid id, Guid taskId, int status, long planned,
        string started, long elapsed, string? completedAt = null, string? completedFor = null,
        string? runStarted = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO FocusSessions
                (Id, TaskId, Status, PlannedSeconds, RemainingSecondsWhenPaused,
                 StartedAtUtc, CurrentRunStartedAtUtc, CompletedAtUtc, ElapsedSeconds, CompletedForDate)
            VALUES ($id, $taskId, $status, $planned, NULL,
                    $started, $runStarted, $completedAt, $elapsed, $completedFor);
            """;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$taskId", taskId.ToString("D"));
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$planned", planned);
        command.Parameters.AddWithValue("$started", started);
        command.Parameters.AddWithValue("$runStarted", (object?)runStarted ?? DBNull.Value);
        command.Parameters.AddWithValue("$completedAt", (object?)completedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("$elapsed", elapsed);
        command.Parameters.AddWithValue("$completedFor", (object?)completedFor ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static void Cleanup(string path)
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
