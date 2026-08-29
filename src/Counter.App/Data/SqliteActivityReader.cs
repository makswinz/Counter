using Counter.Core.Journey;
using Counter.Core.Models;
using Counter.Core.Statistics;
using Microsoft.Data.Sqlite;

namespace Counter.App.Data;

/// <summary>
/// Reads history on its own short-lived read-only connection.
///
/// This is the only database work in the app that runs off the UI thread, so it deliberately
/// does not touch the shared connection: WAL lets an independent reader run while the writer is
/// busy, and a private connection is the only safe way to read from another thread. Nothing
/// here writes, so a reader can never contend for the write lock and stall an animation.
///
/// It returns raw rows and aggregates nothing. Every total, split and streak is computed in
/// Core, where it is testable without a database.
/// </summary>
public sealed class SqliteActivityReader : IActivityReader, ITaskTimeReader
{
    private const string TaskSql = """
        SELECT Id, Title, ScheduledDate, IsCompleted, CompletedForDate, EstimatedSeconds, DeletedAtUtc
          FROM Tasks
         WHERE (ScheduledDate    IS NOT NULL AND ScheduledDate    BETWEEN $from AND $to)
            OR (CompletedForDate IS NOT NULL AND CompletedForDate BETWEEN $from AND $to)
            OR Id IN (SELECT DISTINCT TaskId FROM FocusSegments
                       WHERE TaskId IS NOT NULL AND StartedAtUtc <= $toUtc AND
                             COALESCE(EndedAtUtc, $toUtc) >= $fromUtc)
            OR Id IN (SELECT DISTINCT TaskId FROM ManualTimeEntries
                       WHERE TaskId IS NOT NULL AND LocalDate BETWEEN $from AND $to);
        """;

    private const string SessionSql = """
        SELECT Id, TaskId, TaskTitle, Status, EndReason, PlannedSeconds,
               StartedAtUtc, CompletedAtUtc, CompletedForDate
          FROM FocusSessions
         WHERE StartedAtUtc <= $toUtc
           AND (CompletedAtUtc IS NULL OR CompletedAtUtc >= $fromUtc OR Status IN (0, 1));
        """;

    private const string SegmentSql = """
        SELECT Id, SessionId, TaskId, StartedAtUtc, EndedAtUtc
          FROM FocusSegments
         WHERE StartedAtUtc <= $toUtc AND COALESCE(EndedAtUtc, $toUtc) >= $fromUtc
         ORDER BY StartedAtUtc ASC;
        """;

    private const string ManualSql = """
        SELECT Id, TaskId, TaskTitle, LocalDate, Seconds, Note, CreatedAtUtc
          FROM ManualTimeEntries
         WHERE LocalDate BETWEEN $from AND $to;
        """;

    /// <summary>
    /// Per-task totals from closed runs only. The run in progress is added by the caller from
    /// the segment it already holds, so a running row can tick every second without a query.
    /// </summary>
    private const string TotalsSql = """
        SELECT t.Id,
               COALESCE(SUM(CAST((julianday(g.EndedAtUtc) - julianday(g.StartedAtUtc)) * 86400.0 AS INTEGER)), 0),
               COUNT(DISTINCT g.SessionId),
               MAX(g.EndedAtUtc)
          FROM Tasks t
          LEFT JOIN FocusSegments g ON g.TaskId = t.Id AND g.EndedAtUtc IS NOT NULL
         GROUP BY t.Id;
        """;

    private const string ManualTotalsSql = """
        SELECT TaskId, COALESCE(SUM(Seconds), 0)
          FROM ManualTimeEntries
         WHERE TaskId IS NOT NULL AND Seconds > 0
         GROUP BY TaskId;
        """;

    private readonly FocusDatabase _database;

    public SqliteActivityReader(FocusDatabase database) => _database = database;

    public ActivitySnapshot Read(DateOnly fromInclusive, DateOnly toInclusive, TimeZoneInfo zone)
    {
        // Runs are instants, so the local window has to be converted before it can bound them.
        // A day of slack on each side means a run that crosses the boundary is still read whole
        // and can then be split correctly.
        var fromUtc = ToUtc(fromInclusive.AddDays(-1).ToDateTime(TimeOnly.MinValue), zone);
        var toUtc = ToUtc(toInclusive.AddDays(2).ToDateTime(TimeOnly.MinValue), zone);

        return _database.ReadDetached(connection =>
        {
            var tasks = new List<TaskRecord>();
            var sessions = new List<SessionRecord>();
            var segments = new List<FocusSegment>();
            var manual = new List<ManualTimeEntry>();

            using (var command = connection.CreateCommand())
            {
                command.CommandText = TaskSql;
                BindWindow(command, fromInclusive, toInclusive, fromUtc, toUtc);

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    tasks.Add(new TaskRecord(
                        Guid.Parse(reader.GetString(0)),
                        reader.GetString(1),
                        SqlValues.ReadDayOrNull(reader, 2),
                        reader.GetInt32(3) != 0,
                        SqlValues.ReadDayOrNull(reader, 4),
                        reader.GetInt64(5),
                        !reader.IsDBNull(6)));
                }
            }

            using (var command = connection.CreateCommand())
            {
                command.CommandText = SessionSql;
                BindWindow(command, fromInclusive, toInclusive, fromUtc, toUtc);

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    sessions.Add(new SessionRecord(
                        Guid.Parse(reader.GetString(0)),
                        SqlValues.ReadGuidOrNull(reader, 1),
                        SqlValues.ReadStringOrNull(reader, 2),
                        (FocusSessionStatus)reader.GetInt32(3),
                        (SessionEndReason)reader.GetInt32(4),
                        reader.GetInt64(5),
                        SqlValues.ReadInstant(reader, 6),
                        SqlValues.ReadInstantOrNull(reader, 7),
                        SqlValues.ReadDayOrNull(reader, 8)));
                }
            }

            using (var command = connection.CreateCommand())
            {
                command.CommandText = SegmentSql;
                BindWindow(command, fromInclusive, toInclusive, fromUtc, toUtc);

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    segments.Add(SqliteFocusSessionRepository.MapSegment(reader));
                }
            }

            using (var command = connection.CreateCommand())
            {
                command.CommandText = ManualSql;
                BindWindow(command, fromInclusive, toInclusive, fromUtc, toUtc);

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    manual.Add(SqliteManualTimeRepository.Map(reader));
                }
            }

            return new ActivitySnapshot(tasks, sessions, segments, manual);
        });
    }

    public IReadOnlyList<TaskTimeTotals> ReadTotals() => _database.ReadDetached(connection =>
    {
        var focus = new Dictionary<Guid, (long Seconds, int Sessions, DateTime? Last)>();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = TotalsSql;

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = Guid.Parse(reader.GetString(0));
                var seconds = reader.IsDBNull(1) ? 0L : reader.GetInt64(1);
                var sessions = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
                var last = SqlValues.ReadInstantOrNull(reader, 3);
                focus[id] = (seconds, sessions, last);
            }
        }

        var manual = new Dictionary<Guid, long>();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = ManualTotalsSql;

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                manual[Guid.Parse(reader.GetString(0))] = reader.GetInt64(1);
            }
        }

        var ids = new HashSet<Guid>(focus.Keys);
        ids.UnionWith(manual.Keys);

        var results = new List<TaskTimeTotals>(ids.Count);
        foreach (var id in ids)
        {
            focus.TryGetValue(id, out var f);
            results.Add(new TaskTimeTotals(id, f.Seconds, manual.GetValueOrDefault(id), f.Sessions, f.Last));
        }

        return (IReadOnlyList<TaskTimeTotals>)results;
    });

    private static void BindWindow(
        SqliteCommand command, DateOnly from, DateOnly to, DateTime fromUtc, DateTime toUtc)
    {
        command.Parameters.AddWithValue("$from", SqlValues.ToText(from));
        command.Parameters.AddWithValue("$to", SqlValues.ToText(to));
        command.Parameters.AddWithValue("$fromUtc", SqlValues.ToText(fromUtc));
        command.Parameters.AddWithValue("$toUtc", SqlValues.ToText(toUtc));
    }

    private static DateTime ToUtc(DateTime local, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone);
}
