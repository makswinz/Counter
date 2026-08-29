using System.Globalization;
using System.IO;
using System.Text;
using Counter.App.Data;
using Microsoft.Data.Sqlite;

namespace Counter.App.Services;

/// <summary>
/// Getting data out of Counter, and getting a backup back in.
///
/// Both directions are deliberately conservative. Export never touches the live database except
/// to read it, and restore never overwrites anything while the application is running: it stages
/// the chosen file and swaps it in at the next start, after keeping a copy of what was there.
/// A restore that half-succeeded against an open connection is the one way this application
/// could lose somebody's history, so it is simply never attempted.
/// </summary>
public static class DataTransfer
{
    /// <summary>A backup waiting to be swapped in at the next start.</summary>
    public static string PendingRestorePath => Path.Combine(AppPaths.RootDirectory, "pending-restore.db");

    /// <summary>Where exports are written. One timestamped folder per export.</summary>
    public static string ExportDirectory => Path.Combine(AppPaths.RootDirectory, "exports");

    // =================================================================================
    // Restore
    // =================================================================================

    /// <summary>
    /// Checks a candidate backup and stages it. Returns null on success, or the reason it was
    /// refused: a file that fails its own integrity check, or one written by a newer version of
    /// the application, is never staged.
    /// </summary>
    public static string? StageRestore(string sourcePath)
    {
        try
        {
            if (!File.Exists(sourcePath))
            {
                return "That file no longer exists.";
            }

            var problem = Inspect(sourcePath);
            if (problem is not null)
            {
                return problem;
            }

            File.Copy(sourcePath, PendingRestorePath, overwrite: true);
            Log.Info("Staged " + sourcePath + " for restore at the next start.");
            return null;
        }
        catch (Exception ex)
        {
            Log.Error("Could not stage the backup for restore.", ex);
            return "Could not read that backup.";
        }
    }

    /// <summary>Cancels a staged restore. Used when the user changes their mind before quitting.</summary>
    public static void CancelRestore()
    {
        try
        {
            if (File.Exists(PendingRestorePath))
            {
                File.Delete(PendingRestorePath);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not cancel the staged restore.", ex);
        }
    }

    /// <summary>
    /// Applies a staged restore. Called once at start-up, before anything opens the database.
    ///
    /// The database being replaced is copied into the backup folder first, so a restore is
    /// itself undoable: choosing the wrong backup is a mistake, not a loss.
    /// </summary>
    public static bool ApplyPendingRestore()
    {
        try
        {
            if (!File.Exists(PendingRestorePath))
            {
                return false;
            }

            var problem = Inspect(PendingRestorePath);
            if (problem is not null)
            {
                Log.Error("The staged backup was rejected at start-up: " + problem);
                File.Delete(PendingRestorePath);
                return false;
            }

            var live = AppPaths.DatabasePath;

            if (File.Exists(live))
            {
                var backups = DatabaseMaintenance.BackupDirectory;
                Directory.CreateDirectory(backups);

                var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                File.Copy(live, Path.Combine(backups, "counter-before-restore-" + stamp + ".db"), overwrite: true);
            }

            // The write-ahead log and its shared-memory file belong to the database being
            // replaced. Leaving them behind would graft one file's uncommitted tail onto
            // another file's pages, which is how a restore turns into corruption.
            foreach (var sidecar in new[] { live + "-wal", live + "-shm" })
            {
                if (File.Exists(sidecar))
                {
                    File.Delete(sidecar);
                }
            }

            File.Copy(PendingRestorePath, live, overwrite: true);
            File.Delete(PendingRestorePath);

            Log.Info("Restored the database from a backup.");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Could not apply the staged restore. The existing data was left untouched.", ex);
            return false;
        }
    }

    /// <summary>Returns null when a file is a healthy database this build can open.</summary>
    private static string? Inspect(string path)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly
        };

        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();

        using (var check = connection.CreateCommand())
        {
            check.CommandText = "PRAGMA integrity_check;";
            var result = check.ExecuteScalar() as string;

            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            {
                return "That backup is damaged and was not restored.";
            }
        }

        using (var version = connection.CreateCommand())
        {
            version.CommandText = "PRAGMA user_version;";
            var schema = Convert.ToInt32(version.ExecuteScalar(), CultureInfo.InvariantCulture);

            if (schema > FocusDatabase.TargetSchemaVersion)
            {
                return "That backup was written by a newer version of Counter.";
            }
        }

        return null;
    }

    // =================================================================================
    // Export
    // =================================================================================

    private static readonly (string File, string Query)[] Exports =
    {
        ("tasks.csv",
            "SELECT Id, Title, Note, ScheduledDate, EstimatedSeconds, IsCompleted, CompletedAtUtc, " +
            "CreatedAtUtc, UpdatedAtUtc, DeletedAtUtc FROM Tasks ORDER BY CreatedAtUtc;"),

        ("sessions.csv",
            "SELECT Id, TaskId, TaskTitle, PlannedSeconds, ElapsedSeconds, Status, EndReason, " +
            "StartedAtUtc, CompletedAtUtc FROM FocusSessions ORDER BY StartedAtUtc;"),

        ("focus-runs.csv",
            "SELECT Id, SessionId, TaskId, StartedAtUtc, EndedAtUtc FROM FocusSegments ORDER BY StartedAtUtc;"),

        ("manual-time.csv",
            "SELECT Id, TaskId, TaskTitle, LocalDate, Seconds, Note, CreatedAtUtc " +
            "FROM ManualTimeEntries ORDER BY LocalDate;")
    };

    /// <summary>
    /// Writes every table to CSV in a timestamped folder and returns it.
    ///
    /// Read through the database's own detached read-only connection, so an export can be taken
    /// while a session is running without blocking a single write.
    /// </summary>
    public static string Export(FocusDatabase database)
    {
        var folder = Path.Combine(
            ExportDirectory,
            DateTime.Now.ToString("yyyy-MM-dd-HHmmss", CultureInfo.InvariantCulture));

        Directory.CreateDirectory(folder);

        foreach (var (file, query) in Exports)
        {
            var csv = database.ReadDetached(connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = query;

                using var reader = command.ExecuteReader();
                var builder = new StringBuilder();

                for (var i = 0; i < reader.FieldCount; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(',');
                    }

                    builder.Append(Escape(reader.GetName(i)));
                }

                builder.Append("\r\n");

                while (reader.Read())
                {
                    for (var i = 0; i < reader.FieldCount; i++)
                    {
                        if (i > 0)
                        {
                            builder.Append(',');
                        }

                        builder.Append(reader.IsDBNull(i)
                            ? string.Empty
                            : Escape(Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)));
                    }

                    builder.Append("\r\n");
                }

                return builder.ToString();
            });

            // A byte-order mark, so a double-click into Excel reads accented task titles as
            // text rather than as mojibake.
            File.WriteAllText(Path.Combine(folder, file), csv, new UTF8Encoding(true));
        }

        Log.Info("Exported the database to " + folder + ".");
        return folder;
    }

    private static string Escape(string? value)
    {
        value ??= string.Empty;

        if (value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0)
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>Opens a folder in Explorer. Any failure is logged rather than shown.</summary>
    public static void Reveal(string path)
    {
        try
        {
            Directory.CreateDirectory(path);

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log.Warn("Could not open " + path + ".", ex);
        }
    }
}
