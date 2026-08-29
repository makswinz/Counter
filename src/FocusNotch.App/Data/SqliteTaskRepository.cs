using FocusNotch.Core.Abstractions;
using FocusNotch.Core.Models;
using Microsoft.Data.Sqlite;

namespace FocusNotch.App.Data;

public sealed class SqliteTaskRepository : ITaskRepository
{
    private const string SelectColumns =
        "Id, Title, Note, ScheduledDate, EstimatedSeconds, IsCompleted, CompletedAtUtc, " +
        "CreatedAtUtc, UpdatedAtUtc, SortOrder, CompletedForDate, DeletedAtUtc";

    private readonly FocusDatabase _database;

    public SqliteTaskRepository(FocusDatabase database) => _database = database;

    /// <summary>Live tasks only. A deleted task keeps its row so its history survives.</summary>
    public IReadOnlyList<TaskItem> GetAll() => _database.Read(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT " + SelectColumns + " FROM Tasks WHERE DeletedAtUtc IS NULL " +
            "ORDER BY SortOrder ASC, CreatedAtUtc ASC;";

        using var reader = command.ExecuteReader();
        var results = new List<TaskItem>();
        while (reader.Read())
        {
            results.Add(Map(reader));
        }

        return (IReadOnlyList<TaskItem>)results;
    });

    public TaskItem? Get(Guid id) => _database.Read(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + SelectColumns + " FROM Tasks WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));

        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    });

    public void Add(TaskItem task) => _database.Write(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Tasks
                (Id, Title, Note, ScheduledDate, EstimatedSeconds, IsCompleted,
                 CompletedAtUtc, CreatedAtUtc, UpdatedAtUtc, SortOrder, CompletedForDate, DeletedAtUtc)
            VALUES
                ($id, $title, $note, $scheduled, $estimated, $completed,
                 $completedAt, $created, $updated, $sort, $completedFor, $deletedAt)
            ON CONFLICT (Id) DO UPDATE SET
                Title            = excluded.Title,
                Note             = excluded.Note,
                ScheduledDate    = excluded.ScheduledDate,
                EstimatedSeconds = excluded.EstimatedSeconds,
                IsCompleted      = excluded.IsCompleted,
                CompletedAtUtc   = excluded.CompletedAtUtc,
                UpdatedAtUtc     = excluded.UpdatedAtUtc,
                SortOrder        = excluded.SortOrder,
                CompletedForDate = excluded.CompletedForDate,
                DeletedAtUtc     = excluded.DeletedAtUtc;
            """;
        Bind(command, task);
        command.ExecuteNonQuery();
    });

    public void Update(TaskItem task) => _database.Write(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Tasks SET
                Title            = $title,
                Note             = $note,
                ScheduledDate    = $scheduled,
                EstimatedSeconds = $estimated,
                IsCompleted      = $completed,
                CompletedAtUtc   = $completedAt,
                CreatedAtUtc     = $created,
                UpdatedAtUtc     = $updated,
                SortOrder        = $sort,
                CompletedForDate = $completedFor,
                DeletedAtUtc     = $deletedAt
            WHERE Id = $id;
            """;
        Bind(command, task);
        command.ExecuteNonQuery();
    });

    /// <summary>
    /// Marks the row deleted instead of removing it. Its sessions, runs and manual entries stay
    /// attached, so the statistics still answer for the time that was actually spent.
    /// </summary>
    public void Delete(Guid id) => _database.Write(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Tasks SET DeletedAtUtc = $now WHERE Id = $id AND DeletedAtUtc IS NULL;";
        command.Parameters.AddWithValue("$now", SqlValues.ToText(DateTime.UtcNow));
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.ExecuteNonQuery();
    });

    public void Restore(Guid id) => _database.Write(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Tasks SET DeletedAtUtc = NULL WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.ExecuteNonQuery();
    });

    public int NextSortOrder() => _database.Read(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(SortOrder), -1) + 1 FROM Tasks;";
        return Convert.ToInt32(command.ExecuteScalar() ?? 0);
    });

    /// <summary>
    /// One row per completed task inside the window. Tasks that are completed but carry no
    /// contribution date - only possible for rows written before schema 2 that had neither a
    /// scheduled day nor a completion instant - are correctly left out, and so are deleted ones.
    /// </summary>
    public IReadOnlyList<DateOnly> GetCompletionDates(DateOnly fromInclusive, DateOnly toInclusive)
        => _database.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT CompletedForDate FROM Tasks
                WHERE IsCompleted = 1
                  AND DeletedAtUtc IS NULL
                  AND CompletedForDate IS NOT NULL
                  AND CompletedForDate >= $from
                  AND CompletedForDate <= $to;
                """;
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

    private static void Bind(SqliteCommand command, TaskItem task)
    {
        command.Parameters.AddWithValue("$id", task.Id.ToString("D"));
        command.Parameters.AddWithValue("$title", task.Title);
        command.Parameters.AddWithValue("$note", SqlValues.ToTextOrNull(task.Note));
        command.Parameters.AddWithValue("$scheduled", SqlValues.ToTextOrNull(task.ScheduledDate));
        command.Parameters.AddWithValue("$estimated", task.EstimatedSeconds);
        command.Parameters.AddWithValue("$completed", task.IsCompleted ? 1 : 0);
        command.Parameters.AddWithValue("$completedAt", SqlValues.ToTextOrNull(task.CompletedAtUtc));
        command.Parameters.AddWithValue("$created", SqlValues.ToText(task.CreatedAtUtc));
        command.Parameters.AddWithValue("$updated", SqlValues.ToText(task.UpdatedAtUtc));
        command.Parameters.AddWithValue("$sort", task.SortOrder);
        command.Parameters.AddWithValue("$completedFor", SqlValues.ToTextOrNull(task.CompletedForDate));
        command.Parameters.AddWithValue("$deletedAt", SqlValues.ToTextOrNull(task.DeletedAtUtc));
    }

    private static TaskItem Map(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        Title = reader.GetString(1),
        Note = SqlValues.ReadStringOrNull(reader, 2),
        ScheduledDate = SqlValues.ReadDayOrNull(reader, 3),
        // INTEGER is already 64-bit in SQLite, so reading the full width needs no schema change.
        EstimatedSeconds = reader.GetInt64(4),
        IsCompleted = reader.GetInt32(5) != 0,
        CompletedAtUtc = SqlValues.ReadInstantOrNull(reader, 6),
        CreatedAtUtc = SqlValues.ReadInstant(reader, 7),
        UpdatedAtUtc = SqlValues.ReadInstant(reader, 8),
        SortOrder = reader.GetInt32(9),
        CompletedForDate = SqlValues.ReadDayOrNull(reader, 10),
        DeletedAtUtc = SqlValues.ReadInstantOrNull(reader, 11)
    };
}
