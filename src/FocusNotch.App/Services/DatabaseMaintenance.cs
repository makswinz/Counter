using System.Globalization;
using System.IO;
using FocusNotch.App.Data;
using FocusNotch.Core.Abstractions;
using FocusNotch.Core.Models;

namespace FocusNotch.App.Services;

/// <summary>
/// Startup housekeeping for the data file: an integrity check and a rotating local backup.
///
/// Neither ever deletes or replaces the live database. A file that reports damage is left
/// exactly as it is and the user is told, because a corrupted file that can still be partly read
/// is worth far more to them than a clean empty one.
/// </summary>
public static class DatabaseMaintenance
{
    /// <summary>How many rotating copies are kept before the oldest is removed.</summary>
    public const int MaxBackups = 7;

    /// <summary>A backup is taken at most this often, so launching twice does not make two.</summary>
    public static readonly TimeSpan BackupInterval = TimeSpan.FromHours(24);

    /// <summary>
    /// Where the rotating copies live. It is a parameter on every method below rather than a
    /// hardcoded path so a test can point it somewhere disposable: writing test backups into
    /// somebody's real data folder is not a thing a test gets to do.
    /// </summary>
    public static string BackupDirectory => Path.Combine(AppPaths.RootDirectory, "backups");

    /// <summary>Returns null when the file is healthy, or SQLite's own message when it is not.</summary>
    public static string? CheckIntegrity(FocusDatabase database)
    {
        try
        {
            var result = database.CheckIntegrity();

            if (string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            Log.Error("The database reported an integrity problem: " + result);
            return result;
        }
        catch (Exception ex)
        {
            Log.Error("Could not run the database integrity check.", ex);
            return null;
        }
    }

    /// <summary>
    /// Takes a backup when the last one is old enough, then trims the folder to the newest
    /// <see cref="MaxBackups"/>. Any failure is logged and swallowed: not having a backup is a
    /// disappointment, but it must never stop the app from starting.
    /// </summary>
    public static bool BackupIfDue(
        FocusDatabase database, ISettingsStore settings, DateTime nowUtc, string? directory = null)
    {
        directory ??= BackupDirectory;

        try
        {
            var last = settings.Get(SettingKeys.LastBackupUtc);

            if (DateTime.TryParse(
                    last,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                    out var previous)
                && nowUtc - previous < BackupInterval)
            {
                return false;
            }

            var name = "focusnotch-" + nowUtc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".db";
            var path = Path.Combine(directory, name);

            database.BackupTo(path);
            settings.Set(
                SettingKeys.LastBackupUtc,
                nowUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));

            Trim(directory);
            Log.Info("Wrote a local backup to " + path + ".");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not write the local database backup.", ex);
            return false;
        }
    }

    /// <summary>Removes all but the newest <see cref="MaxBackups"/> copies.</summary>
    public static void Trim(string? directory = null)
    {
        directory ??= BackupDirectory;

        try
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            var stale = new DirectoryInfo(directory)
                .GetFiles("focusnotch-*.db")
                .OrderByDescending(file => file.Name, StringComparer.Ordinal)
                .Skip(MaxBackups)
                .ToList();

            foreach (var file in stale)
            {
                file.Delete();
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not trim the local database backups.", ex);
        }
    }
}
