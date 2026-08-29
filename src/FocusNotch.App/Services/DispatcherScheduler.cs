using System.Windows.Threading;
using FocusNotch.Core.Threading;

namespace FocusNotch.App.Services;

/// <summary>
/// Runs work on the thread pool and hands the result back on the WPF dispatcher.
///
/// The continuation is posted at <see cref="DispatcherPriority.Background"/> so a result that
/// arrives mid-animation waits for the frame to finish rather than competing with it, and every
/// property change it causes is raised on the UI thread as WPF requires.
/// </summary>
public sealed class DispatcherScheduler : IBackgroundScheduler
{
    private readonly Dispatcher _dispatcher;

    public DispatcherScheduler(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public void Run<T>(Func<T> work, Action<T> onCompleted, Action<Exception>? onFailed = null)
    {
        Task.Run(work).ContinueWith(task =>
        {
            if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
            {
                return;
            }

            if (task.IsFaulted)
            {
                var failure = task.Exception?.GetBaseException() ?? new Exception("Background work failed.");

                _dispatcher.BeginInvoke(
                    new Action(() => onFailed?.Invoke(failure)), DispatcherPriority.Background);

                return;
            }

            // The result is taken here, on the pool thread, rather than inside the dispatcher
            // callback. The task is complete either way, so neither could block - but the UI
            // thread should not be seen touching a Task's result at all.
            var value = task.Result;

            _dispatcher.BeginInvoke(
                new Action(() => onCompleted(value)), DispatcherPriority.Background);
        }, TaskScheduler.Default);
    }
}
