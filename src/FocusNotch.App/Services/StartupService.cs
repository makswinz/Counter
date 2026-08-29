using System.Diagnostics;
using Microsoft.Win32;

namespace FocusNotch.App.Services;

/// <summary>
/// Current-user "Start with Windows" registration. It writes only to HKCU\...\Run, needs no
/// elevation, and removing the value fully reverses it.
/// </summary>
public static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "FocusNotch";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read the Windows startup entry.", ex);
            return false;
        }
    }

    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                return false;
            }

            if (enabled)
            {
                var path = GetExecutablePath();
                if (path is null)
                {
                    Log.Warn("Could not resolve the executable path for the startup entry.");
                    return false;
                }

                key.SetValue(ValueName, "\"" + path + "\"", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not update the Windows startup entry.", ex);
            return false;
        }
    }

    /// <summary>
    /// Repoints an existing startup entry at wherever this copy actually lives.
    ///
    /// "Start with Windows" means start this application, not start whichever executable happened
    /// to be running when the box was ticked. Those differ more often than it sounds: somebody
    /// runs the portable build once and enables it, then installs properly, and the entry still
    /// names a file in their Downloads folder. When that file is later deleted the setting reads
    /// as on and silently does nothing.
    ///
    /// Called at start-up. It only ever rewrites an entry that already exists, so it can never
    /// turn the setting on by itself, and it does nothing at all when the path already agrees.
    /// </summary>
    public static void RefreshPath()
    {
        try
        {
            var path = GetExecutablePath();

            if (path is null)
            {
                return;
            }

            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);

            if (key?.GetValue(ValueName) is not string current || current.Length == 0)
            {
                return;
            }

            var wanted = "\"" + path + "\"";

            if (string.Equals(current, wanted, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            key.SetValue(ValueName, wanted, RegistryValueKind.String);
            Log.Info("Repointed the Windows startup entry at " + path + ".");
        }
        catch (Exception ex)
        {
            Log.Warn("Could not repoint the Windows startup entry.", ex);
        }
    }

    private static string? GetExecutablePath()
    {
        var path = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(path) && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        return Process.GetCurrentProcess().MainModule?.FileName;
    }
}
