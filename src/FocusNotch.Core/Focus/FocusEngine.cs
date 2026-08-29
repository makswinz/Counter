using FocusNotch.Core.Models;
using FocusNotch.Core.Time;

namespace FocusNotch.Core.Focus;

/// <summary>
/// Owns the single active focus session, its running segments and all of its state transitions.
/// The engine holds no timer of its own: callers pump <see cref="Poll"/> at whatever cadence
/// suits them (a dispatcher tick, a restore on startup, a hotkey press) and the engine
/// derives every value from the clock.
///
/// Every stretch of the session actually running is recorded as a <see cref="FocusSegment"/>.
/// Time spent is later summed from those instants rather than from a counter, so it stays
/// correct across a pause, a crash, sleep and a restart.
/// </summary>
public sealed class FocusEngine
{
    private readonly IClock _clock;

    public FocusEngine(IClock clock) => _clock = clock;

    public FocusSession? Current { get; private set; }

    /// <summary>The run in progress, or null whenever the session is not running.</summary>
    public FocusSegment? CurrentSegment { get; private set; }

    /// <summary>Raised whenever the session must be written to storage.</summary>
    public event Action<FocusSession>? SessionPersisted;

    /// <summary>Raised whenever a segment was opened or closed and must be written.</summary>
    public event Action<FocusSegment>? SegmentPersisted;

    /// <summary>Raised exactly once per session that reaches zero.</summary>
    public event Action<FocusSession>? SessionCompleted;

    /// <summary>Raised when the session reference or status changes, for UI refresh.</summary>
    public event Action? StateChanged;

    public bool HasActiveSession => Current is { IsActive: true };

    public TimeSpan Remaining => Current?.RemainingAt(_clock.UtcNow) ?? TimeSpan.Zero;

    public double RemainingFraction => Current?.RemainingFractionAt(_clock.UtcNow) ?? 1d;

    public FocusSession Start(Guid? taskId, long plannedSeconds, string? taskTitle = null)
    {
        if (HasActiveSession)
        {
            throw new InvalidOperationException(
                "A focus session is already active. Cancel it or call Switch instead.");
        }

        if (plannedSeconds < FocusDefaults.MinimumSeconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(plannedSeconds),
                plannedSeconds,
                "A focus session must be at least " + FocusDefaults.MinimumSeconds + " seconds.");
        }

        var now = _clock.UtcNow;
        var session = new FocusSession
        {
            Id = Guid.NewGuid(),
            TaskId = taskId,
            TaskTitle = taskTitle,
            Status = FocusSessionStatus.Running,
            PlannedSeconds = plannedSeconds,
            RemainingSecondsWhenPaused = null,
            StartedAtUtc = now,
            CurrentRunStartedAtUtc = now,
            CompletedAtUtc = null,
            EndReason = SessionEndReason.None,
            ElapsedSeconds = 0
        };

        Current = session;
        SessionPersisted?.Invoke(session);
        OpenSegment(session, now);
        StateChanged?.Invoke();
        return session;
    }

    /// <summary>Cancels any active session (preserving its elapsed time) and starts a new one.</summary>
    public FocusSession Switch(Guid? taskId, long plannedSeconds, string? taskTitle = null)
    {
        Cancel(SessionEndReason.SwitchedTask);
        return Start(taskId, plannedSeconds, taskTitle);
    }

    public void Pause()
    {
        if (Current is not { Status: FocusSessionStatus.Running } session)
        {
            return;
        }

        var now = _clock.UtcNow;

        var remaining = (long)Math.Round(
            session.RemainingAt(now).TotalSeconds,
            MidpointRounding.AwayFromZero);
        remaining = Math.Clamp(remaining, 0, session.PlannedSeconds);

        // Close the run before the status changes, so the cap can still see the target instant.
        CloseSegment(session, now);

        session.Status = FocusSessionStatus.Paused;
        session.RemainingSecondsWhenPaused = remaining;
        session.ElapsedSeconds = session.PlannedSeconds - remaining;
        session.CurrentRunStartedAtUtc = null;

        SessionPersisted?.Invoke(session);
        StateChanged?.Invoke();
    }

    public void Resume()
    {
        if (Current is not { Status: FocusSessionStatus.Paused } session)
        {
            return;
        }

        var now = _clock.UtcNow;

        session.Status = FocusSessionStatus.Running;
        session.CurrentRunStartedAtUtc = now;
        session.RemainingSecondsWhenPaused = null;

        SessionPersisted?.Invoke(session);
        OpenSegment(session, now);
        StateChanged?.Invoke();
    }

    public void Toggle()
    {
        switch (Current?.Status)
        {
            case FocusSessionStatus.Running:
                Pause();
                break;
            case FocusSessionStatus.Paused:
                Resume();
                break;
        }
    }

    /// <summary>Ends the active session, keeping the time it actually accumulated.</summary>
    public FocusSession? Cancel(SessionEndReason reason = SessionEndReason.StoppedByUser)
    {
        if (Current is not { IsActive: true } session)
        {
            return null;
        }

        var now = _clock.UtcNow;
        CloseSegment(session, now);

        var elapsed = (long)Math.Round(
            session.ElapsedAt(now).TotalSeconds,
            MidpointRounding.AwayFromZero);

        session.ElapsedSeconds = Math.Clamp(elapsed, 0, session.PlannedSeconds);
        session.Status = FocusSessionStatus.Cancelled;
        session.EndReason = reason;
        session.CurrentRunStartedAtUtc = null;
        session.RemainingSecondsWhenPaused = null;

        SessionPersisted?.Invoke(session);
        Current = null;
        StateChanged?.Invoke();
        return session;
    }

    /// <summary>
    /// Replaces the current session without writing anything or advancing the clock. This is how
    /// a failed persist is rolled back: the engine is put back exactly as it was before the
    /// transition that could not be saved.
    /// </summary>
    public void Adopt(FocusSession? session, FocusSegment? segment = null)
    {
        Current = session is { IsActive: true } ? session : null;
        CurrentSegment = Current is { Status: FocusSessionStatus.Running } ? segment : null;
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Re-attaches a session loaded from storage, together with whatever run was open when the
    /// process ended. A running session whose target instant already passed while the process
    /// was closed is completed at its true target time, and its open run is closed there too,
    /// so the time credited is capped at the planned duration and never at "now".
    /// </summary>
    public void Restore(FocusSession? session, FocusSegment? openSegment = null)
    {
        if (session is null || !session.IsActive)
        {
            Current = null;
            CurrentSegment = null;

            // A run left open by a session that is no longer live has to be closed, or it would
            // keep growing every time it is read.
            if (openSegment is { IsOpen: true })
            {
                openSegment.EndedAtUtc = CapEnd(session, openSegment, _clock.UtcNow);
                SegmentPersisted?.Invoke(openSegment);
            }

            StateChanged?.Invoke();
            return;
        }

        Current = session;

        if (session.Status == FocusSessionStatus.Running)
        {
            if (session.CurrentRunStartedAtUtc is null)
            {
                // Defensive: a running row without a run start cannot be projected forward.
                session.CurrentRunStartedAtUtc = _clock.UtcNow;
                SessionPersisted?.Invoke(session);
            }

            if (openSegment is { IsOpen: true })
            {
                CurrentSegment = openSegment;
            }
            else
            {
                // The row says running but no run was recorded. Open one from the run start so
                // the time since the process died is still attributed, capped at the target.
                OpenSegment(session, session.CurrentRunStartedAtUtc.Value);
            }
        }
        else
        {
            CurrentSegment = null;

            if (openSegment is { IsOpen: true })
            {
                openSegment.EndedAtUtc = CapEnd(session, openSegment, _clock.UtcNow);
                SegmentPersisted?.Invoke(openSegment);
            }
        }

        StateChanged?.Invoke();
        Poll();
    }

    /// <summary>
    /// Checks whether the active session has reached zero. Returns true only on the single
    /// transition into <see cref="FocusSessionStatus.Completed"/>.
    /// </summary>
    public bool Poll()
    {
        if (Current is not { Status: FocusSessionStatus.Running } session)
        {
            return false;
        }

        var target = session.TargetUtc;
        if (target is null || _clock.UtcNow < target.Value)
        {
            return false;
        }

        // The run ends at the target, not at the moment the app noticed. A session that expired
        // while the process was closed therefore credits exactly its planned length.
        CloseSegment(session, target.Value);

        session.Status = FocusSessionStatus.Completed;
        session.CompletedAtUtc = target.Value;
        session.EndReason = SessionEndReason.Completed;

        // The contribution is stamped once, from the instant the session actually reached zero,
        // so a session that finished while the machine was asleep still credits the right day
        // and no later timezone change can move it.
        session.CompletedForDate = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(target.Value, DateTimeKind.Utc), _clock.LocalTimeZone));

        session.ElapsedSeconds = session.PlannedSeconds;
        session.CurrentRunStartedAtUtc = null;
        session.RemainingSecondsWhenPaused = null;

        SessionPersisted?.Invoke(session);
        StateChanged?.Invoke();
        SessionCompleted?.Invoke(session);
        return true;
    }

    // =================================================================================
    // Segments
    // =================================================================================

    private void OpenSegment(FocusSession session, DateTime startedAtUtc)
    {
        var segment = new FocusSegment
        {
            Id = Guid.NewGuid(),
            SessionId = session.Id,
            TaskId = session.TaskId,
            StartedAtUtc = startedAtUtc,
            EndedAtUtc = null
        };

        CurrentSegment = segment;
        SegmentPersisted?.Invoke(segment);
    }

    /// <summary>
    /// Closes the run in progress. Two segments can never overlap, because there is only ever
    /// one open segment and it is always closed before the next one opens.
    /// </summary>
    private void CloseSegment(FocusSession session, DateTime endedAtUtc)
    {
        if (CurrentSegment is not { IsOpen: true } segment)
        {
            CurrentSegment = null;
            return;
        }

        segment.EndedAtUtc = CapEnd(session, segment, endedAtUtc);
        CurrentSegment = null;
        SegmentPersisted?.Invoke(segment);
    }

    /// <summary>
    /// Where a run is allowed to end: never before it started, and never past the instant the
    /// timer was due to reach zero. Time after the planned end is not counted automatically.
    /// </summary>
    private static DateTime CapEnd(FocusSession? session, FocusSegment segment, DateTime requestedEnd)
    {
        var end = requestedEnd;

        if (session is not null)
        {
            var target = session.TargetUtc
                         ?? (session.CurrentRunStartedAtUtc?.AddSeconds(
                             Math.Max(0, session.PlannedSeconds - session.ElapsedSeconds)));

            if (target is { } cap && end > cap)
            {
                end = cap;
            }
        }

        return end < segment.StartedAtUtc ? segment.StartedAtUtc : end;
    }
}
