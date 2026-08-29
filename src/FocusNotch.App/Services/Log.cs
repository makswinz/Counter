using System.IO;
using System.Text;

namespace FocusNotch.App.Services;

/// <summary>
/// Minimal append-only file log. Logging must never be able to take the app down, so every
/// write is best-effort and failures are swallowed after a single attempt.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static bool _disabled;

    public static void Info(string message) => Write("INFO ", message, null);

    public static void Warn(string message, Exception? ex = null) => Write("WARN ", message, ex);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        if (_disabled)
        {
            return;
        }

        try
        {
            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
              .Append(' ')
              .Append(level)
              .Append(' ')
              .Append(message);

            if (ex is not null)
            {
                sb.AppendLine().Append(ex);
            }

            sb.AppendLine();

            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.LogDirectory);
                File.AppendAllText(AppPaths.LogFilePath, sb.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // A log that cannot be written must not escalate into a user-visible failure.
            _disabled = true;
        }
    }
}
