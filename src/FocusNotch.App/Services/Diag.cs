using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Diagnostics;

namespace FocusNotch.App.Services;

/// <summary>
/// Structured diagnostic tracing for the interaction layer.
///
/// This is deliberately separate from <see cref="Log"/>: <see cref="Log"/> records things a user
/// might have to act on, this records the event sequence behind a transition so a twitch or a
/// dropped click can be replayed after the fact. It is compiled out of Release builds unless the
/// FOCUSNOTCH_DIAG environment variable is set, so production writes nothing.
/// </summary>
public static class Diag
{
    private static readonly object Gate = new();
    private static readonly Stopwatch Uptime = Stopwatch.StartNew();
    private static readonly bool ForcedOn =
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FOCUSNOTCH_DIAG"));

    private static bool _failed;

    /// <summary>True when the trace is actually being written.</summary>
    public static bool IsEnabled
    {
        get
        {
#if DEBUG
            return !_failed;
#else
            return ForcedOn && !_failed;
#endif
        }
    }

    public static string FilePath => Path.Combine(AppPaths.LogDirectory, "diag.log");

    /// <summary>
    /// Routes WPF's own data-binding failures into the trace.
    ///
    /// A broken binding is silent at runtime: the control simply shows nothing and carries on.
    /// Sending them here makes "there are no binding errors" a thing that can be checked rather
    /// than assumed. Like the rest of this class it is DEBUG-only unless FOCUSNOTCH_DIAG is set.
    /// </summary>
    public static void CaptureBindingFailures()
    {
        if (!IsEnabled)
        {
            return;
        }

        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(new BindingListener());
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
    }

    private sealed class BindingListener : TraceListener
    {
        public override void Write(string? message) => Record(message);

        public override void WriteLine(string? message) => Record(message);

        private static void Record(string? message)
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                Diag.Write("binding", "failure", ("detail", message.Trim()));
            }
        }
    }

    /// <summary>
    /// Writes one channel-tagged record. <paramref name="fields"/> are name=value pairs so the
    /// trace stays greppable: Diag.Write("panel", "request", ("from", a), ("to", b)).
    /// </summary>
    public static void Write(string channel, string what, params (string Key, object? Value)[] fields)
    {
        if (!IsEnabled)
        {
            return;
        }

        try
        {
            var sb = new StringBuilder(128);
            sb.Append(Uptime.Elapsed.TotalMilliseconds.ToString("F1").PadLeft(10))
              .Append("ms ")
              .Append(channel.PadRight(9))
              .Append(' ')
              .Append(what);

            foreach (var (key, value) in fields)
            {
                sb.Append(' ').Append(key).Append('=').Append(value ?? "null");
            }

            sb.AppendLine();

            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.LogDirectory);
                File.AppendAllText(FilePath, sb.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // Tracing must never be able to affect the running app.
            _failed = true;
        }
    }
}
