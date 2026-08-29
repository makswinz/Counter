using Counter.Core.Journey;
using Counter.Core.Threading;
using Counter.Core.Time;

namespace Counter.Core.Statistics;

/// <summary>
/// Publishes the statistics snapshot the panel renders.
///
/// It reads and aggregates off the render path, exactly like the journey surface, and coalesces
/// a burst of requests into a single follow-up pass so a run of edits cannot queue a pile of
/// identical queries behind an animation. Nothing here runs on a timer tick.
/// </summary>
public sealed class StatisticsService
{
    private readonly IActivityReader _reader;
    private readonly IClock _clock;
    private readonly IBackgroundScheduler _scheduler;

    private bool _inFlight;
    private bool _requestedAgain;
    private StatisticsRange _pendingRange;

    public StatisticsService(IActivityReader reader, IClock clock, IBackgroundScheduler? scheduler = null)
    {
        _reader = reader;
        _clock = clock;
        _scheduler = scheduler ?? InlineScheduler.Instance;
    }

    public StatisticsModel Current { get; private set; } = StatisticsModel.Empty;

    public StatisticsRange Range { get; private set; } = StatisticsRange.Last7Days;

    public event Action<StatisticsModel>? Changed;

    public StatisticsModel Compute(StatisticsRange range)
    {
        var today = _clock.Today();
        var window = ActivityWindow.ForRange(today, range);
        var snapshot = _reader.Read(window.From, window.To, _clock.LocalTimeZone);

        return StatisticsCalculator.Build(snapshot, range, today, _clock.UtcNow, _clock.LocalTimeZone);
    }

    public StatisticsModel Refresh(StatisticsRange range)
    {
        Range = range;
        var model = Compute(range);
        Publish(model);
        return model;
    }

    public void Publish(StatisticsModel model)
    {
        Current = model;
        Changed?.Invoke(model);
    }

    public void RefreshAsync(StatisticsRange range, Action<Exception>? onFailed = null)
    {
        Range = range;

        if (_inFlight)
        {
            _pendingRange = range;
            _requestedAgain = true;
            return;
        }

        _inFlight = true;

        _scheduler.Run(
            () => Compute(range),
            model =>
            {
                _inFlight = false;
                Publish(model);

                if (_requestedAgain)
                {
                    _requestedAgain = false;
                    RefreshAsync(_pendingRange, onFailed);
                }
            },
            ex =>
            {
                _inFlight = false;
                _requestedAgain = false;
                onFailed?.Invoke(ex);
            });
    }

    public static string Label(StatisticsRange range) => range switch
    {
        StatisticsRange.Today => "Today",
        StatisticsRange.Last7Days => "7 days",
        StatisticsRange.Last30Days => "30 days",
        _ => "All time"
    };
}
