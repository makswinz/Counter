using System.Threading;

namespace Counter.App.Services;

/// <summary>
/// Enforces one running copy. A second launch signals the first one to reveal itself and then
/// exits, so double-clicking the shortcut behaves like focusing the app.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\Counter.SingleInstance.Mutex";
    private const string SignalName = @"Local\Counter.SingleInstance.Signal";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _signal;
    private CancellationTokenSource? _listenerCancellation;
    private Thread? _listener;
    private bool _disposed;

    private SingleInstance(Mutex mutex, EventWaitHandle signal, bool isFirstInstance)
    {
        _mutex = mutex;
        _signal = signal;
        IsFirstInstance = isFirstInstance;
    }

    public bool IsFirstInstance { get; }

    public static SingleInstance Acquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, SignalName);
        return new SingleInstance(mutex, signal, createdNew);
    }

    /// <summary>Tells the already-running instance to show itself.</summary>
    public void SignalExistingInstance()
    {
        try
        {
            _signal.Set();
        }
        catch (Exception ex)
        {
            Log.Warn("Could not signal the running Counter instance.", ex);
        }
    }

    /// <summary>Starts a background listener that invokes <paramref name="onSignal"/> on each request.</summary>
    public void ListenForSecondInstance(Action onSignal)
    {
        _listenerCancellation = new CancellationTokenSource();
        var token = _listenerCancellation.Token;

        _listener = new Thread(() =>
        {
            var handles = new WaitHandle[] { _signal, token.WaitHandle };
            while (!token.IsCancellationRequested)
            {
                var index = WaitHandle.WaitAny(handles);
                if (index != 0 || token.IsCancellationRequested)
                {
                    return;
                }

                try
                {
                    onSignal();
                }
                catch (Exception ex)
                {
                    Log.Warn("Failed to handle a second-instance signal.", ex);
                }
            }
        })
        {
            IsBackground = true,
            Name = "Counter.SingleInstanceListener"
        };

        _listener.Start();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _listenerCancellation?.Cancel();
            _listener?.Join(TimeSpan.FromMilliseconds(500));
        }
        catch (Exception ex)
        {
            Log.Warn("Could not stop the single-instance listener cleanly.", ex);
        }

        _listenerCancellation?.Dispose();
        _signal.Dispose();

        if (IsFirstInstance)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // The mutex was never owned by this thread; nothing to release.
            }
        }

        _mutex.Dispose();
    }
}
