using FocusNotch.Core.Focus;
using FocusNotch.Core.Models;
using FocusNotch.Core.Streaks;
using FocusNotch.Core.Threading;
using FocusNotch.Core.Time;

namespace FocusNotch.Core.Journey;

/// <summary>
/// The single source of truth for the journey streak and its heatmap.
///
/// A local calendar day is productive when it carries at least one contribution, and a
/// contribution is a completed task attributed to that date, a successfully completed focus
/// session attributed to that date, or a positive manual time entry on it. All three are read
/// back out of storage on every refresh, so nothing here can fall out of step with what was
/// actually saved. Unfinished tasks, and cancelled, running or paused sessions, never count.
/// </summary>
public sealed class JourneyActivityService
{
    private readonly IActivityReader _reader;
    private readonly IClock _clock;
    private readonly IBackgroundScheduler _scheduler;

    private bool _refreshInFlight;
    private bool _refreshRequestedAgain;

    public JourneyActivityService(
        IActivityReader reader,
        IClock clock,
        IBackgroundScheduler? scheduler = null)
    {
        _reader = reader;
        _clock = clock;
        _scheduler = scheduler ?? InlineScheduler.Instance;
    }

    /// <summary>The newest computed snapshot. Every consumer reads this same instance.</summary>
    public JourneyModel Current { get; private set; } = JourneyModel.Empty;

    /// <summary>Raised after <see cref="Current"/> has been replaced.</summary>
    public event Action<JourneyModel>? Changed;

    /// <summary>
    /// Reads the contribution window and recalculates the streak and grid. Pure with respect to
    /// the caller: it touches no UI and can safely run off the dispatcher.
    /// </summary>
    public JourneyModel Compute(int weeks = StreakCalculator.DefaultWeeks)
    {
        var today = _clock.Today();
        var window = ActivityWindow.ForHeatmap(today, weeks);
        var snapshot = _reader.Read(window.From, window.To, _clock.LocalTimeZone);

        return Build(snapshot, today, _clock.UtcNow, _clock.LocalTimeZone, weeks);
    }

    /// <summary>
    /// Turns raw rows into the journey surface. Separated from the read so the whole mapping -
    /// contributions, intensity levels, streak, grid placement - can be tested without a
    /// database and without a clock that moves on its own.
    /// </summary>
    public static JourneyModel Build(
        ActivitySnapshot snapshot,
        DateOnly today,
        DateTime nowUtc,
        TimeZoneInfo zone,
        int weeks = StreakCalculator.DefaultWeeks)
    {
        var activity = BuildDays(snapshot, nowUtc, zone);
        var counts = StreakCalculator.CountByDay(activity);

        var streak = StreakCalculator.CurrentStreak(counts, today);
        var longest = StreakCalculator.LongestStreak(counts, today);
        var cells = StreakCalculator.BuildHeatmap(activity, today, weeks);

        return new JourneyModel(streak, longest, cells, activity, today);
    }

    /// <summary>
    /// Adds a snapshot up into one entry per local calendar day.
    ///
    /// Focus time is split at local midnight, so a session that ran from 23:30 to 00:30 puts
    /// half an hour on each day rather than an hour on whichever end happened to win. Manual
    /// entries already carry a plain date and are simply added to it.
    /// </summary>
    public static IReadOnlyDictionary<DateOnly, DayActivity> BuildDays(
        ActivitySnapshot snapshot,
        DateTime nowUtc,
        TimeZoneInfo zone)
    {
        var targets = snapshot.Sessions.ToDictionary(s => s.Id, TargetOf);
        var spans = TimeLedger.ToSpans(
            snapshot.Segments,
            nowUtc,
            sessionId => targets.TryGetValue(sessionId, out var target) ? target : null);

        var focusByDay = TimeLedger.SecondsByLocalDay(spans, zone);
        var manualByDay = TimeLedger.ManualSecondsByLocalDay(snapshot.ManualEntries);

        var tasksByDay = new Dictionary<DateOnly, int>();
        foreach (var task in snapshot.Tasks)
        {
            if (task is { IsCompleted: true, IsDeleted: false, CompletedForDate: { } day })
            {
                tasksByDay[day] = tasksByDay.TryGetValue(day, out var existing) ? existing + 1 : 1;
            }
        }

        var sessionsByDay = new Dictionary<DateOnly, int>();
        foreach (var session in snapshot.Sessions)
        {
            if (session is { Status: FocusSessionStatus.Completed, CompletedForDate: { } day })
            {
                sessionsByDay[day] = sessionsByDay.TryGetValue(day, out var existing) ? existing + 1 : 1;
            }
        }

        var manualCountByDay = new Dictionary<DateOnly, int>();
        foreach (var entry in snapshot.ManualEntries)
        {
            if (entry.Seconds <= 0)
            {
                continue;
            }

            manualCountByDay[entry.LocalDate] =
                manualCountByDay.TryGetValue(entry.LocalDate, out var existing) ? existing + 1 : 1;
        }

        var days = new HashSet<DateOnly>();
        days.UnionWith(focusByDay.Keys);
        days.UnionWith(manualByDay.Keys);
        days.UnionWith(tasksByDay.Keys);
        days.UnionWith(sessionsByDay.Keys);
        days.UnionWith(manualCountByDay.Keys);

        var result = new Dictionary<DateOnly, DayActivity>(days.Count);
        foreach (var day in days)
        {
            result[day] = new DayActivity(
                day,
                tasksByDay.GetValueOrDefault(day),
                sessionsByDay.GetValueOrDefault(day),
                manualCountByDay.GetValueOrDefault(day),
                focusByDay.GetValueOrDefault(day),
                manualByDay.GetValueOrDefault(day));
        }

        return result;
    }

    /// <summary>Where a session's run is allowed to end, so an open run cannot outgrow its plan.</summary>
    internal static DateTime? TargetOf(SessionRecord session)
    {
        if (session.Status == FocusSessionStatus.Completed && session.CompletedAtUtc is { } completed)
        {
            return completed;
        }

        return null;
    }

    /// <summary>
    /// Publishes a freshly computed snapshot. Call this after any committed change that could
    /// alter activity, never before the transaction has landed.
    /// </summary>
    public JourneyModel Refresh(int weeks = StreakCalculator.DefaultWeeks)
    {
        var model = Compute(weeks);
        Publish(model);
        return model;
    }

    /// <summary>
    /// Publishes a snapshot produced elsewhere, so a background computation can be handed back
    /// on the UI thread without recomputing it.
    /// </summary>
    public void Publish(JourneyModel model)
    {
        Current = model;
        Changed?.Invoke(model);
    }

    /// <summary>
    /// Recomputes off the render path and publishes the result on the caller's thread. Requests
    /// that arrive while one is already running are coalesced into a single follow-up pass, so a
    /// burst of edits cannot queue a pile of identical queries behind the animation.
    /// </summary>
    public void RefreshAsync(int weeks = StreakCalculator.DefaultWeeks, Action<Exception>? onFailed = null)
    {
        if (_refreshInFlight)
        {
            _refreshRequestedAgain = true;
            return;
        }

        _refreshInFlight = true;

        _scheduler.Run(
            () => Compute(weeks),
            model =>
            {
                _refreshInFlight = false;
                Publish(model);

                if (_refreshRequestedAgain)
                {
                    _refreshRequestedAgain = false;
                    RefreshAsync(weeks, onFailed);
                }
            },
            ex =>
            {
                _refreshInFlight = false;
                _refreshRequestedAgain = false;
                onFailed?.Invoke(ex);
            });
    }

    // ---------------------------------------------------------------------------------
    // Contribution dates
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// The date a task counts for when it is completed: the day it was scheduled for when it
    /// has one, otherwise the day it was actually completed. That is what makes ticking off a
    /// task that was scheduled for yesterday light up yesterday rather than today.
    /// </summary>
    public DateOnly ContributionDateFor(DateOnly? scheduledDate) => scheduledDate ?? _clock.Today();
}
