using System.IO;
using Microsoft.Data.Sqlite;

namespace Counter.App.Data;

/// <summary>Thrown when the database cannot be opened or migrated. Never destructive.</summary>
public sealed class DatabaseUnavailableException : Exception
{
    public DatabaseUnavailableException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Owns the single SQLite connection for the process. Counter is a single-user desktop
/// app, so one long-lived connection behind a lock is both simpler and faster than pooling.
/// </summary>
public sealed class FocusDatabase : IDisposable
{
    /// <summary>Bump this and add a case to <see cref="ApplyMigration"/> when the schema changes.</summary>
    public const int TargetSchemaVersion = 3;

    private readonly object _gate = new();
    private readonly SqliteConnection _connection;
    private bool _disposed;

    public FocusDatabase(string databasePath)
    {
        DatabasePath = databasePath;

        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            ForeignKeys = true
        };

        try
        {
            _connection = new SqliteConnection(builder.ToString());
            _connection.Open();

            using var pragma = _connection.CreateCommand();
            pragma.CommandText =
                "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000; " +
                "PRAGMA synchronous=NORMAL;";
            pragma.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            throw new DatabaseUnavailableException(
                "Could not open the Counter database at " + databasePath + ".", ex);
        }
    }

    public string DatabasePath { get; }

    public bool WasCreatedEmpty { get; private set; }

    /// <summary>Runs every outstanding migration in a single transaction.</summary>
    public void Migrate()
    {
        lock (_gate)
        {
            int version;
            try
            {
                version = ReadUserVersion();
            }
            catch (Exception ex)
            {
                throw new DatabaseUnavailableException("Could not read the database schema version.", ex);
            }

            WasCreatedEmpty = version == 0;

            if (version > TargetSchemaVersion)
            {
                throw new DatabaseUnavailableException(
                    "This database was written by a newer version of Counter (schema " + version +
                    "). The existing file has been left untouched.");
            }

            if (version == TargetSchemaVersion)
            {
                return;
            }

            using var transaction = _connection.BeginTransaction();
            try
            {
                for (var next = version + 1; next <= TargetSchemaVersion; next++)
                {
                    ApplyMigration(next, transaction);
                }

                using var setVersion = _connection.CreateCommand();
                setVersion.Transaction = transaction;
                // PRAGMA does not accept parameters; the value is a validated internal constant.
                setVersion.CommandText = "PRAGMA user_version = " + TargetSchemaVersion + ";";
                setVersion.ExecuteNonQuery();

                transaction.Commit();
            }
            catch (Exception ex)
            {
                // Roll back and leave the file exactly as it was. Never recreate it.
                try
                {
                    transaction.Rollback();
                }
                catch (Exception rollbackEx)
                {
                    throw new DatabaseUnavailableException(
                        "A schema migration failed and could not be rolled back. " +
                        "Your data file has not been modified or deleted.", rollbackEx);
                }

                throw new DatabaseUnavailableException(
                    "A schema migration failed. Your existing data has been left untouched.", ex);
            }
        }
    }

    private void ApplyMigration(int version, SqliteTransaction transaction)
    {
        var sql = version switch
        {
            1 => Migrations.V1CreateSchema,
            2 => Migrations.V2AddContributionDates,
            3 => Migrations.V3AddTimeTracking,
            _ => throw new DatabaseUnavailableException("Unknown migration version " + version + ".")
        };

        using (var command = _connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        if (version == 2)
        {
            BackfillContributionDates(transaction);
        }

        if (version == 3)
        {
            // Every statement here only fills columns that are null or inserts rows that do not
            // exist yet, so re-running it could not damage anything, and it shares the migration
            // transaction so it either lands whole or not at all.
            foreach (var backfill in new[]
                     {
                         Migrations.V3BackfillEndReasons,
                         Migrations.V3BackfillSessionTitles,
                         Migrations.V3BackfillSegments,
                         Migrations.V3BackfillLiveSegments
                     })
            {
                using var command = _connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = backfill;
                command.ExecuteNonQuery();
            }
        }
    }

    /// <summary>
    /// Asks SQLite whether the file is internally consistent. A damaged file is reported, never
    /// repaired by force and never replaced: the user's only copy of their history is not
    /// something to overwrite on a hunch.
    /// </summary>
    public string CheckIntegrity()
    {
        lock (_gate)
        {
            ThrowIfDisposed();

            using var command = _connection.CreateCommand();
            command.CommandText = "PRAGMA integrity_check;";
            return command.ExecuteScalar() as string ?? "unknown";
        }
    }

    /// <summary>
    /// Copies the live database to <paramref name="destinationPath"/> through SQLite's own
    /// backup API, which takes a consistent copy while the connection stays open and in use.
    /// Copying the file by hand would be able to catch it mid-write.
    /// </summary>
    public void BackupTo(string destinationPath)
    {
        lock (_gate)
        {
            ThrowIfDisposed();

            var directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = destinationPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private,

                // Pooling off, and this matters more than it looks.
                //
                // Microsoft.Data.Sqlite pools connections by default, so disposing one returns it
                // to the pool with the file still open. A backup is written once and never
                // reopened, so pooling buys nothing here and costs the ability to delete the file
                // afterwards: the rotation that keeps the newest seven copies would find every
                // file locked, swallow the error and quietly let the folder grow forever. It is
                // exactly the kind of failure that depends on timing, which is why it passed on
                // one machine and failed on a clean one.
                Pooling = false
            };

            using var destination = new SqliteConnection(builder.ToString());
            destination.Open();
            _connection.BackupDatabase(destination);
        }
    }

    /// <summary>
    /// Gives every row that was already completed before schema 2 the date it should count for.
    /// Runs inside the migration transaction, so it either lands whole or not at all, and only
    /// ever fills a column that is null: no existing value is overwritten and no row is removed.
    /// </summary>
    private void BackfillContributionDates(SqliteTransaction transaction)
    {
        var zone = TimeZoneInfo.Local;

        var taskUpdates = new List<(string Id, string? Local)>();
        using (var read = _connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = Migrations.V2SelectCompletedTasks;

            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetString(0);
                var scheduled = reader.IsDBNull(1) ? null : reader.GetString(1);
                var completedAt = reader.IsDBNull(2) ? null : reader.GetString(2);

                // A task with a scheduled day keeps that day; otherwise fall back to the local
                // day it was completed on. With neither, it stays null and does not contribute.
                var local = scheduled ?? ToLocalDay(completedAt, zone);
                taskUpdates.Add((id, local));
            }
        }

        foreach (var (id, local) in taskUpdates)
        {
            if (local is null)
            {
                continue;
            }

            using var write = _connection.CreateCommand();
            write.Transaction = transaction;
            write.CommandText = "UPDATE Tasks SET CompletedForDate = $day WHERE Id = $id;";
            write.Parameters.AddWithValue("$day", local);
            write.Parameters.AddWithValue("$id", id);
            write.ExecuteNonQuery();
        }

        var sessionUpdates = new List<(string Id, string? Local)>();
        using (var read = _connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = Migrations.V2SelectCompletedSessions;

            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                sessionUpdates.Add((reader.GetString(0), ToLocalDay(reader.GetString(1), zone)));
            }
        }

        foreach (var (id, local) in sessionUpdates)
        {
            if (local is null)
            {
                continue;
            }

            using var write = _connection.CreateCommand();
            write.Transaction = transaction;
            write.CommandText = "UPDATE FocusSessions SET CompletedForDate = $day WHERE Id = $id;";
            write.Parameters.AddWithValue("$day", local);
            write.Parameters.AddWithValue("$id", id);
            write.ExecuteNonQuery();
        }
    }

    private static string? ToLocalDay(string? utcText, TimeZoneInfo zone)
    {
        if (string.IsNullOrEmpty(utcText))
        {
            return null;
        }

        if (!DateTime.TryParse(
                utcText,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal |
                System.Globalization.DateTimeStyles.AssumeUniversal,
                out var utc))
        {
            return null;
        }

        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
        return local.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Runs several writes as one transaction, so a caller that has to change more than one row
    /// can never be observed - or crash - halfway through.
    /// </summary>
    public void WriteTransaction(Action<SqliteConnection, SqliteTransaction> write)
    {
        lock (_gate)
        {
            ThrowIfDisposed();

            using var transaction = _connection.BeginTransaction();
            try
            {
                write(_connection, transaction);
                transaction.Commit();
            }
            catch
            {
                try
                {
                    transaction.Rollback();
                }
                catch
                {
                    // The original failure is the one worth reporting.
                }

                throw;
            }
        }
    }

    /// <summary>
    /// Opens a private read-only connection to the same file for work that must not block the
    /// UI thread. WAL lets a reader run while the shared connection is writing, and a separate
    /// connection is the only safe way to read from another thread.
    /// </summary>
    public T ReadDetached<T>(Func<SqliteConnection, T> read)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            ForeignKeys = true
        };

        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();

        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA busy_timeout=4000;";
            pragma.ExecuteNonQuery();
        }

        return read(connection);
    }

    private int ReadUserVersion()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar() ?? 0);
    }

    public int GetSchemaVersion()
    {
        lock (_gate)
        {
            return ReadUserVersion();
        }
    }

    /// <summary>Runs a read against the shared connection under the connection lock.</summary>
    public T Read<T>(Func<SqliteConnection, T> read)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return read(_connection);
        }
    }

    /// <summary>Runs a write against the shared connection under the connection lock.</summary>
    public void Write(Action<SqliteConnection> write)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            write(_connection);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(FocusDatabase));
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _connection.Close();
            _connection.Dispose();
        }

        SqliteConnection.ClearAllPools();
    }
}
