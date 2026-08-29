using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Counter.App.Data;

/// <summary>Shared conversions between CLR values and the text formats used in the schema.</summary>
internal static class SqlValues
{
    private const string InstantFormat = "yyyy-MM-ddTHH:mm:ss.fffffffZ";
    private const string DayFormat = "yyyy-MM-dd";

    public static string ToText(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString(InstantFormat, CultureInfo.InvariantCulture);

    public static object ToTextOrNull(DateTime? utc) =>
        utc.HasValue ? ToText(utc.Value) : DBNull.Value;

    public static DateTime ReadInstant(SqliteDataReader reader, int ordinal)
    {
        var text = reader.GetString(ordinal);
        var parsed = DateTime.Parse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
    }

    public static DateTime? ReadInstantOrNull(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ReadInstant(reader, ordinal);

    public static string ToText(DateOnly day) => day.ToString(DayFormat, CultureInfo.InvariantCulture);

    public static object ToTextOrNull(DateOnly? day) =>
        day.HasValue ? ToText(day.Value) : DBNull.Value;

    public static DateOnly? ReadDayOrNull(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return DateOnly.ParseExact(reader.GetString(ordinal), DayFormat, CultureInfo.InvariantCulture);
    }

    public static object ToTextOrNull(string? value) =>
        string.IsNullOrEmpty(value) ? DBNull.Value : value;

    public static string? ReadStringOrNull(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    public static object ToTextOrNull(Guid? value) =>
        value.HasValue ? value.Value.ToString("D") : (object)DBNull.Value;

    public static Guid? ReadGuidOrNull(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Guid.Parse(reader.GetString(ordinal));

    public static int? ReadIntOrNull(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    public static object ToIntOrNull(int? value) => value.HasValue ? value.Value : (object)DBNull.Value;

    // Durations and aggregates are 64-bit. SQLite INTEGER already is, so these read the full
    // width of columns that have existed since schema 1 without any change to the schema.
    public static long? ReadLongOrNull(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    public static object ToLongOrNull(long? value) => value.HasValue ? value.Value : (object)DBNull.Value;
}
