using Counter.Core.Abstractions;
using Counter.Core.Models;
using Microsoft.Data.Sqlite;

namespace Counter.App.Data;

public sealed class SqliteFocusSessionRepository : IFocusSessionRepository
{
    private const string SelectColumns =
        "Id, TaskId, Status, PlannedSeconds, RemainingSecondsWhenPaused, StartedAtUtc, " +
        "CurrentRunStartedAtUtc, CompletedAtUtc, ElapsedSeconds, CompletedForDate, " +
        "TaskTitle, EndReason";

    private const string UpsertSql = """
        INSERT INTO FocusSessions
            (Id, TaskId, Status, PlannedSeconds, RemainingSecondsWhenPaused,
             StartedAtUtc, CurrentRunStartedAtUtc, CompletedAtUtc, ElapsedSeconds,
             CompletedForDate, TaskTitle, EndReason)
        VALUES
            ($id, $taskId, $status, $planned, $remaining,
             $started, $runStarted, $completed, $elapsed, $completedFor, $taskTitle, $endReason)
        ON CONFLICT (Id) DO UPDATE SET
            TaskId                     = excluded.TaskId,
            Status                     = excluded.Status,
            PlannedSeconds             = excluded.PlannedSeconds,
            RemainingSecondsWhenPaused = excluded.RemainingSecondsWhenPaused,
            StartedAtUtc               = excluded.StartedAtUtc,
            CurrentRunStartedAtUtc     = excluded.CurrentRunStartedAtUtc,
            CompletedAtUtc             = excluded.CompletedAtUtc,
            ElapsedSeconds             = excluded.ElapsedSeconds,
            CompletedForDate           = excluded.CompletedForDate,
            TaskTitle                  = excluded.TaskTitle,
            EndReason                  = excluded.EndReason;
        """;

    private const string SegmentUpsertSql = """
        INSERT INTO FocusSegments (Id, SessionId, TaskId, StartedAtUtc, EndedAtUtc)
        VALUES ($id, $sessionId, $taskId, $started, $ended)
        ON CONFLICT (Id) DO UPDATE SET
            SessionId    = excluded.SessionId,
            TaskId       = excluded.TaskId,
            StartedAtUtc = excluded.StartedAtUtc,
            EndedAtUtc   = excluded.EndedAtUtc;
        """;

    private const string SegmentColumns = "Id, SessionId, TaskId, StartedAtUtc, EndedAtUtc";

    private readonly FocusDatabase _database;

    public SqliteFocusSessionRepository(FocusDatabase database) => _database = database;

    public void Save(FocusSession session) => SaveAll(new[] { session }, Array.Empty<FocusSegment>());

    /// <summary>
    /// Writes sessions and runs as one transaction. A focus switch ends one session, closes its
    /// run, starts another and opens a new run; committing all four together is what guarantees
    /// the database is never caught holding two live sessions, none at all when the user asked
    /// for one, or a run that was never closed.
    /// </summary>
    public void SaveAll(IReadOnlyList<FocusSession> sessions, IReadOnlyList<FocusSegment> segments)
    {
        if (sessions.Count == 0 && segments.Count == 0)
        {
            return;
        }

        _database.WriteTransaction((connection, transaction) =>
        {
            foreach (var session in sessions)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = UpsertSql;
                Bind(command, session);
                command.ExecuteNonQuery();
            }

            foreach (var segment in segments)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = SegmentUpsertSql;
                Bind(command, segment);
                command.ExecuteNonQuery();
            }
        });
    }

    public FocusSession? GetActive() => GetActiveSessions().FirstOrDefault();

    /// <summary>
    /// Every live session, newest first. There should only ever be one; this is what lets
    /// startup notice and repair a file that somehow holds more.
    /// </summary>
    public IReadOnlyList<FocusSession> GetActiveSessions() => _database.Read(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT " + SelectColumns + " FROM FocusSessions " +
            "WHERE Status IN ($running, $paused) " +
            "ORDER BY StartedAtUtc DESC;";
        command.Parameters.AddWithValue("$running", (int)FocusSessionStatus.Running);
        command.Parameters.AddWithValue("$paused", (int)FocusSessionStatus.Paused);

        using var reader = command.ExecuteReader();
        var results = new List<FocusSession>();
        while (reader.Read())
        {
            results.Add(Map(reader));
        }

        return (IReadOnlyList<FocusSession>)results;
    });

    public IReadOnlyList<FocusSegment> GetSegments(Guid sessionId) => _database.Read(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT " + SegmentColumns + " FROM FocusSegments WHERE SessionId = $id ORDER BY StartedAtUtc ASC;";
        command.Parameters.AddWithValue("$id", sessionId.ToString("D"));

        using var reader = command.ExecuteReader();
        var results = new List<FocusSegment>();
        while (reader.Read())
        {
            results.Add(MapSegment(reader));
        }

        return (IReadOnlyList<FocusSegment>)results;
    });

    public IReadOnlyList<FocusSegment> GetOpenSegments() => _database.Read(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT " + SegmentColumns + " FROM FocusSegments WHERE EndedAtUtc IS NULL " +
            "ORDER BY StartedAtUtc ASC;";

        using var reader = command.ExecuteReader();
        var results = new List<FocusSegment>();
        while (reader.Read())
        {
            results.Add(MapSegment(reader));
        }

        return (IReadOnlyList<FocusSegment>)results;
    });

    /// <summary>One row per successfully completed session inside the window.</summary>
    public IReadOnlyList<DateOnly> GetCompletionDates(DateOnly fromInclusive, DateOnly toInclusive)
        => _database.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT CompletedForDate FROM FocusSessions
                WHERE Status = $completed
                  AND CompletedForDate IS NOT NULL
                  AND CompletedForDate >= $from
                  AND CompletedForDate <= $to;
                """;
            command.Parameters.AddWithValue("$completed", (int)FocusSessionStatus.Completed);
            command.Parameters.AddWithValue("$from", SqlValues.ToText(fromInclusive));
            command.Parameters.AddWithValue("$to", SqlValues.ToText(toInclusive));

            using var reader = command.ExecuteReader();
            var results = new List<DateOnly>();
            while (reader.Read())
            {
                var day = SqlValues.ReadDayOrNull(reader, 0);
                if (day.HasValue)
                {
                    results.Add(day.Value);
                }
            }

            return (IReadOnlyList<DateOnly>)results;
        });

    public FocusSession? Get(Guid id) => _database.Read(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + SelectColumns + " FROM FocusSessions WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));

        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    });

    public IReadOnlyList<DateTime> GetCompletionsUtc(DateTime sinceUtc) => _database.Read(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CompletedAtUtc FROM FocusSessions
            WHERE Status = $completed AND CompletedAtUtc IS NOT NULL AND CompletedAtUtc >= $since
            ORDER BY CompletedAtUtc ASC;
            """;
        command.Parameters.AddWithValue("$completed", (int)FocusSessionStatus.Completed);
        command.Parameters.AddWithValue("$since", SqlValues.ToText(sinceUtc));

        using var reader = command.ExecuteReader();
        var results = new List<DateTime>();
        while (reader.Read())
        {
            results.Add(SqlValues.ReadInstant(reader, 0));
        }

        return (IReadOnlyList<DateTime>)results;
    });

    private static void Bind(SqliteCommand command, FocusSession session)
    {
        command.Parameters.AddWithValue("$id", session.Id.ToString("D"));
        command.Parameters.AddWithValue("$taskId", SqlValues.ToTextOrNull(session.TaskId));
        command.Parameters.AddWithValue("$status", (int)session.Status);
        command.Parameters.AddWithValue("$planned", session.PlannedSeconds);
        command.Parameters.AddWithValue("$remaining", SqlValues.ToLongOrNull(session.RemainingSecondsWhenPaused));
        command.Parameters.AddWithValue("$started", SqlValues.ToText(session.StartedAtUtc));
        command.Parameters.AddWithValue("$runStarted", SqlValues.ToTextOrNull(session.CurrentRunStartedAtUtc));
        command.Parameters.AddWithValue("$completed", SqlValues.ToTextOrNull(session.CompletedAtUtc));
        command.Parameters.AddWithValue("$elapsed", session.ElapsedSeconds);
        command.Parameters.AddWithValue("$completedFor", SqlValues.ToTextOrNull(session.CompletedForDate));
        command.Parameters.AddWithValue("$taskTitle", SqlValues.ToTextOrNull(session.TaskTitle));
        command.Parameters.AddWithValue("$endReason", (int)session.EndReason);
    }

    private static void Bind(SqliteCommand command, FocusSegment segment)
    {
        command.Parameters.AddWithValue("$id", segment.Id.ToString("D"));
        command.Parameters.AddWithValue("$sessionId", segment.SessionId.ToString("D"));
        command.Parameters.AddWithValue("$taskId", SqlValues.ToTextOrNull(segment.TaskId));
        command.Parameters.AddWithValue("$started", SqlValues.ToText(segment.StartedAtUtc));
        command.Parameters.AddWithValue("$ended", SqlValues.ToTextOrNull(segment.EndedAtUtc));
    }

    private static FocusSession Map(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        TaskId = SqlValues.ReadGuidOrNull(reader, 1),
        Status = (FocusSessionStatus)reader.GetInt32(2),
        PlannedSeconds = reader.GetInt64(3),
        RemainingSecondsWhenPaused = SqlValues.ReadLongOrNull(reader, 4),
        StartedAtUtc = SqlValues.ReadInstant(reader, 5),
        CurrentRunStartedAtUtc = SqlValues.ReadInstantOrNull(reader, 6),
        CompletedAtUtc = SqlValues.ReadInstantOrNull(reader, 7),
        ElapsedSeconds = reader.GetInt64(8),
        CompletedForDate = SqlValues.ReadDayOrNull(reader, 9),
        TaskTitle = SqlValues.ReadStringOrNull(reader, 10),
        EndReason = (SessionEndReason)reader.GetInt32(11)
    };

    internal static FocusSegment MapSegment(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        SessionId = Guid.Parse(reader.GetString(1)),
        TaskId = SqlValues.ReadGuidOrNull(reader, 2),
        StartedAtUtc = SqlValues.ReadInstant(reader, 3),
        EndedAtUtc = SqlValues.ReadInstantOrNull(reader, 4)
    };
}
