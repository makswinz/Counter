using System.IO;

namespace Counter.App.Services;

/// <summary>
/// All Counter data lives under %LocalAppData%\Counter and never leaves the machine.
///
/// The application was called Focus Notch before it was called Counter, and somebody upgrading
/// has their entire history sitting in a folder under the old name. Moving it is handled here,
/// once, at start-up, and the whole design of this class is that a failed migration is never
/// allowed to look like lost data: whatever happens, the paths point at the folder and the file
/// that actually exist.
/// </summary>
public static class AppPaths
{
    private const string CurrentName = "Counter";
    private const string LegacyName = "FocusNotch";
    private const string CurrentDatabase = "counter.db";
    private const string LegacyDatabase = "focusnotch.db";

    /// <summary>What happened during the move, reported once the log path is known.</summary>
    private static string? _migrationNote;

    public static string RootDirectory { get; } =
        Resolve(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    /// <summary>
    /// The database, preferring the current name but falling back to the old one when it is
    /// still there. Belt and braces: if the rename below ever failed, the alternative is opening
    /// a brand new empty database beside a perfectly good one, which reads as total data loss.
    /// </summary>
    public static string DatabasePath
    {
        get
        {
            var current = Path.Combine(RootDirectory, CurrentDatabase);

            if (File.Exists(current))
            {
                return current;
            }

            var legacy = Path.Combine(RootDirectory, LegacyDatabase);
            return File.Exists(legacy) ? legacy : current;
        }
    }

    public static string LogDirectory => Path.Combine(RootDirectory, "logs");

    public static string LogFilePath => Path.Combine(
        LogDirectory,
        "counter-" + DateTime.Now.ToString("yyyy-MM-dd") + ".log");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(LogDirectory);

        if (_migrationNote is { } note)
        {
            // Logged here rather than where it happened: resolving the log path needs the root
            // directory, so nothing can be written until after this class has finished deciding
            // what that is.
            Log.Info(note);
            _migrationNote = null;
        }
    }

    /// <summary>
    /// Settles on a data directory, moving the old one across if that is what is there.
    ///
    /// The order is deliberate. A move is attempted only when the new folder does not exist, so
    /// this can never merge two histories or overwrite a newer one. If the move fails - the
    /// folder is open in Explorer, an antivirus has a file locked, anything - the old folder is
    /// used exactly as it is, and the application runs from it as though nothing had changed.
    /// </summary>
    /// <param name="local">
    /// Where per-user data lives. A parameter rather than a lookup so the migration can be
    /// tested against a scratch folder: it is the one piece of code here that can lose somebody
    /// their entire history, and it would otherwise be the one piece that never runs in a test.
    /// </param>
    public static string Resolve(string local)
    {
        var current = Path.Combine(local, CurrentName);
        var legacy = Path.Combine(local, LegacyName);

        if (!Directory.Exists(current) && Directory.Exists(legacy))
        {
            try
            {
                Directory.Move(legacy, current);
                _migrationNote = "Moved the data folder from " + legacy + " to " + current + ".";
            }
            catch (Exception ex)
            {
                _migrationNote = "Could not move the data folder from " + legacy
                    + ", so it is being used where it is: " + ex.Message;

                return legacy;
            }
        }

        var root = Directory.Exists(current) || !Directory.Exists(legacy) ? current : legacy;
        RenameLegacyFiles(root);

        return root;
    }

    /// <summary>
    /// Gives the database and the rotating backups their current names.
    ///
    /// Failing is survivable: <see cref="DatabasePath"/> falls back to the old name, and a backup
    /// left under the old one is simply not rotated. Neither loses anything.
    /// </summary>
    private static void RenameLegacyFiles(string root)
    {
        try
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var from = Path.Combine(root, LegacyDatabase + suffix);
                var to = Path.Combine(root, CurrentDatabase + suffix);

                if (File.Exists(from) && !File.Exists(to))
                {
                    File.Move(from, to);
                }
            }

            var backups = Path.Combine(root, "backups");

            if (!Directory.Exists(backups))
            {
                return;
            }

            foreach (var file in Directory.GetFiles(backups, LegacyDatabase[..^3] + "-*.db"))
            {
                var name = Path.GetFileName(file);
                var renamed = Path.Combine(backups, "counter-" + name["focusnotch-".Length..]);

                if (!File.Exists(renamed))
                {
                    File.Move(file, renamed);
                }
            }
        }
        catch (Exception)
        {
            // Deliberately silent. This runs before logging exists, and every caller already
            // copes with the old names still being in place.
        }
    }
}
