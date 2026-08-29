using Counter.Core.Abstractions;
using Counter.Core.Models;
using Microsoft.Data.Sqlite;

namespace Counter.App.Data;

/// <summary>
/// Work recorded by hand. Stored in its own table, never merged into the timer's segments, so
/// the two totals stay separable and a manual entry can never be counted twice.
/// </summary>
public sealed class SqliteManualTimeRepository : IManualTimeRepository
{
    private const string Columns = "Id, TaskId, TaskTitle, LocalDate, Seconds, Note, CreatedAtUtc";

    private readonly FocusDatabase _database;

    public SqliteManualTimeRepository(FocusDatabase database) => _database = database;

    public void Add(ManualTimeEntry entry) => _database.Write(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ManualTimeEntries (Id, TaskId, TaskTitle, LocalDate, Seconds, Note, CreatedAtUtc)
            VALUES ($id, $taskId, $taskTitle, $date, $seconds, $note, $created)
            ON CONFLICT (Id) DO UPDATE SET
                TaskId    = excluded.TaskId,
                TaskTitle = excluded.TaskTitle,
                LocalDate = excluded.LocalDate,
                Seconds   = excluded.Seconds,
                Note      = excluded.Note;
            """;
        command.Parameters.AddWithValue("$id", entry.Id.ToString("D"));
        command.Parameters.AddWithValue("$taskId", SqlValues.ToTextOrNull(entry.TaskId));
        command.Parameters.AddWithValue("$taskTitle", SqlValues.ToTextOrNull(entry.TaskTitle));
        command.Parameters.AddWithValue("$date", SqlValues.ToText(entry.LocalDate));
        command.Parameters.AddWithValue("$seconds", entry.Seconds);
        command.Parameters.AddWithValue("$note", SqlValues.ToTextOrNull(entry.Note));
        command.Parameters.AddWithValue("$created", SqlValues.ToText(entry.CreatedAtUtc));
        command.ExecuteNonQuery();
    });

    public void Delete(Guid id) => _database.Write(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ManualTimeEntries WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.ExecuteNonQuery();
    });

    public IReadOnlyList<ManualTimeEntry> GetForTask(Guid taskId) => _database.Read(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT " + Columns + " FROM ManualTimeEntries WHERE TaskId = $id ORDER BY LocalDate DESC;";
        command.Parameters.AddWithValue("$id", taskId.ToString("D"));
        return ReadAll(command);
    });

    public IReadOnlyList<ManualTimeEntry> GetInRange(DateOnly fromInclusive, DateOnly toInclusive)
        => _database.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT " + Columns + " FROM ManualTimeEntries " +
                "WHERE LocalDate >= $from AND LocalDate <= $to ORDER BY LocalDate ASC;";
            command.Parameters.AddWithValue("$from", SqlValues.ToText(fromInclusive));
            command.Parameters.AddWithValue("$to", SqlValues.ToText(toInclusive));
            return ReadAll(command);
        });

    private static IReadOnlyList<ManualTimeEntry> ReadAll(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var results = new List<ManualTimeEntry>();
        while (reader.Read())
        {
            results.Add(Map(reader));
        }

        return results;
    }

    internal static ManualTimeEntry Map(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        TaskId = SqlValues.ReadGuidOrNull(reader, 1),
        TaskTitle = SqlValues.ReadStringOrNull(reader, 2),
        LocalDate = SqlValues.ReadDayOrNull(reader, 3) ?? default,
        Seconds = reader.GetInt64(4),
        Note = SqlValues.ReadStringOrNull(reader, 5),
        CreatedAtUtc = SqlValues.ReadInstant(reader, 6)
    };
}
