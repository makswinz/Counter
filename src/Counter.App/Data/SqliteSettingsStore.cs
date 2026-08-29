using System.Globalization;
using Counter.Core.Abstractions;

namespace Counter.App.Data;

public sealed class SqliteSettingsStore : ISettingsStore
{
    private readonly FocusDatabase _database;

    public SqliteSettingsStore(FocusDatabase database) => _database = database;

    public string? Get(string key) => _database.Read(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM Settings WHERE Key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    });

    public void Set(string key, string value) => _database.Write(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Settings (Key, Value) VALUES ($key, $value)
            ON CONFLICT (Key) DO UPDATE SET Value = excluded.Value;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    });

    public bool GetBool(string key, bool fallback)
    {
        var raw = Get(key);
        if (raw is null)
        {
            return fallback;
        }

        return raw == "1" || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
    }

    public void SetBool(string key, bool value) => Set(key, value ? "1" : "0");

    public int GetInt(string key, int fallback)
    {
        var raw = Get(key);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    public void SetInt(string key, int value) =>
        Set(key, value.ToString(CultureInfo.InvariantCulture));
}
