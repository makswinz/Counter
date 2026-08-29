using System.Diagnostics;

namespace Counter.App.Services;

/// <summary>
/// Watches how a transition actually rendered.
///
/// It records the interval between composed frames while a transition is in flight and reports
/// the count, the median, the 95th percentile and how many frames ran long, together with
/// whether the transition finished or was superseded. That is the only honest way to say the
/// interface is smooth: sixty frames a second is a claim about intervals, not about code.
///
/// The whole thing is inert unless <see cref="Diag.IsEnabled"/>, which is DEBUG builds and any
/// build started with COUNTER_DIAG set. A Release build allocates nothing and measures
/// nothing.
/// </summary>
public sealed class FrameMonitor
{
    /// <summary>A frame longer than this is worth naming: it is two missed frames at 60 Hz.</summary>
    public const double SlowFrameMs = 33;

    private readonly List<double> _intervals = new(256);
    private readonly Stopwatch _clock = new();

    private long _transitionId;
    private double _lastFrameMs;
    private bool _running;

    public int InterruptedTransitions { get; private set; }

    public int StaleCallbacks { get; private set; }

    public int SlowFrames { get; private set; }

    /// <summary>The last transition's 95th-percentile frame interval, in milliseconds.</summary>
    public double LastP95Ms { get; private set; }

    public void Begin(long transitionId, string reason)
    {
        if (!Diag.IsEnabled)
        {
            return;
        }

        if (_running)
        {
            // The previous transition never settled, so it was superseded mid-flight. That is
            // legitimate - reversing a half-open panel is exactly that - but it is worth
            // counting, because a burst of them means requests are fighting each other.
            InterruptedTransitions++;
            Report("interrupted");
        }

        _transitionId = transitionId;
        _intervals.Clear();
        _lastFrameMs = 0;
        _running = true;
        _clock.Restart();

        Diag.Write("frame", "begin", ("id", transitionId), ("reason", reason));
    }

    public void Frame()
    {
        if (!_running)
        {
            return;
        }

        var now = _clock.Elapsed.TotalMilliseconds;
        var interval = now - _lastFrameMs;
        _lastFrameMs = now;

        // The first frame's interval is measured from the transition starting, which includes
        // whatever the caller did before yielding, so it says nothing about rendering.
        if (_intervals.Count > 0 || interval < 100)
        {
            _intervals.Add(interval);

            if (interval > SlowFrameMs)
            {
                SlowFrames++;
            }
        }
    }

    public void Settle() => Report("settle");

    /// <summary>Counted when a superseded transition tries to write something.</summary>
    public void Stale(long transitionId)
    {
        if (!Diag.IsEnabled)
        {
            return;
        }

        StaleCallbacks++;
        Diag.Write("frame", "stale", ("id", transitionId), ("current", _transitionId));
    }

    private void Report(string what)
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        _clock.Stop();

        if (_intervals.Count == 0)
        {
            Diag.Write("frame", what, ("id", _transitionId), ("frames", 0));
            return;
        }

        var sorted = _intervals.OrderBy(value => value).ToList();
        var median = sorted[sorted.Count / 2];
        var p95 = sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(sorted.Count * 0.95) - 1)];
        var slow = sorted.Count(value => value > SlowFrameMs);

        LastP95Ms = p95;

        Diag.Write("frame", what,
            ("id", _transitionId),
            ("frames", sorted.Count),
            ("ms", Math.Round(_clock.Elapsed.TotalMilliseconds, 1)),
            ("median", Math.Round(median, 2)),
            ("p95", Math.Round(p95, 2)),
            ("max", Math.Round(sorted[^1], 2)),
            ("slow", slow));
    }
}
