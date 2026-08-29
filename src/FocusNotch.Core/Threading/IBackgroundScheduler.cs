namespace FocusNotch.Core.Threading;

/// <summary>
/// Runs work off the render path and hands the result back on the thread the caller cares
/// about. Injected rather than assumed so tests can run the same code synchronously, and so
/// that nothing in Core has to know a WPF dispatcher exists.
/// </summary>
public interface IBackgroundScheduler
{
    void Run<T>(Func<T> work, Action<T> onCompleted, Action<Exception>? onFailed = null);
}

/// <summary>Runs everything inline. Used by tests and by any non-interactive host.</summary>
public sealed class InlineScheduler : IBackgroundScheduler
{
    public static readonly InlineScheduler Instance = new();

    public void Run<T>(Func<T> work, Action<T> onCompleted, Action<Exception>? onFailed = null)
    {
        try
        {
            onCompleted(work());
        }
        catch (Exception ex)
        {
            if (onFailed is null)
            {
                throw;
            }

            onFailed(ex);
        }
    }
}
