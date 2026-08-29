using System.IO;

namespace FocusNotch.App.Services;

/// <summary>All Focus Notch data lives under %LocalAppData%\FocusNotch and never leaves the machine.</summary>
public static class AppPaths
{
    public static string RootDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FocusNotch");

    public static string DatabasePath => Path.Combine(RootDirectory, "focusnotch.db");

    public static string LogDirectory => Path.Combine(RootDirectory, "logs");

    public static string LogFilePath => Path.Combine(
        LogDirectory,
        "focusnotch-" + DateTime.Now.ToString("yyyy-MM-dd") + ".log");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(LogDirectory);
    }
}
