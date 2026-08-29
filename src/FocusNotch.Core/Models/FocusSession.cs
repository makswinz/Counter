namespace FocusNotch.Core.Models;

/// <summary>
/// A focus countdown. The remaining time of a running session is always derived from an
/// absolute UTC target instant, never from a mutable counter that is decremented on a tick.
/// That keeps the session correct across sleep, hibernation, lock and process restarts.
/// </summary>
public sealed class FocusSession
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid? TaskId { get; set; }

    /// <summary>
    /// The task's title as it was when the session started. History has to keep reading
    /// correctly after the task is renamed or removed from the lists.
    /// </summary>
    public string? TaskTitle { get; set; }

    public FocusSessionStatus Status { get; set; } = FocusSessionStatus.Running;

    /// <summary>Planned length in seconds, up to 99:59:59.</summary>
    public long PlannedSeconds { get; set; }

    /// <summary>Exact remaining seconds captured at the moment of the last pause.</summary>
    public long? RemainingSecondsWhenPaused { get; set; }

    public DateTime StartedAtUtc { get; set; }

    /// <summary>Start of the current uninterrupted run; null unless the session is running.</summary>
    public DateTime? CurrentRunStartedAtUtc { get; set; }

    public DateTime? CompletedAtUtc { get; set; }

    /// <summary>
    /// The local calendar day a successfully completed session counts for. Captured once, at
    /// completion, so the contribution stays on the day it was earned across timezone changes.
    /// </summary>
    public DateOnly? CompletedForDate { get; set; }

    /// <summary>Why the session stopped. <see cref="SessionEndReason.None"/> while it is live.</summary>
    public SessionEndReason EndReason { get; set; } = SessionEndReason.None;

    /// <summary>Seconds already consumed by finished runs (excludes the run in progress).</summary>
    public long ElapsedSeconds { get; set; }

    public bool IsActive => Status is FocusSessionStatus.Running or FocusSessionStatus.Paused;

    /// <summary>Absolute UTC instant at which a running session reaches zero.</summary>
    public DateTime? TargetUtc =>
        Status == FocusSessionStatus.Running && CurrentRunStartedAtUtc.HasValue
            ? CurrentRunStartedAtUtc.Value.AddSeconds(Math.Max(0, PlannedSeconds - ElapsedSeconds))
            : null;

    public TimeSpan RemainingAt(DateTime nowUtc)
    {
        switch (Status)
        {
            case FocusSessionStatus.Running:
                var target = TargetUtc;
                if (target is null)
                {
                    return TimeSpan.FromSeconds(Math.Max(0, PlannedSeconds - ElapsedSeconds));
                }

                var remaining = target.Value - nowUtc;
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;

            case FocusSessionStatus.Paused:
                var paused = RemainingSecondsWhenPaused ?? Math.Max(0, PlannedSeconds - ElapsedSeconds);
                return TimeSpan.FromSeconds(Math.Max(0, paused));

            case FocusSessionStatus.Completed:
                return TimeSpan.Zero;

            default:
                return TimeSpan.FromSeconds(Math.Max(0, PlannedSeconds - ElapsedSeconds));
        }
    }

    public TimeSpan ElapsedAt(DateTime nowUtc)
    {
        if (Status == FocusSessionStatus.Running && CurrentRunStartedAtUtc.HasValue)
        {
            var run = nowUtc - CurrentRunStartedAtUtc.Value;
            if (run < TimeSpan.Zero)
            {
                run = TimeSpan.Zero;
            }

            var total = TimeSpan.FromSeconds(ElapsedSeconds) + run;
            var planned = TimeSpan.FromSeconds(PlannedSeconds);
            return total > planned ? planned : total;
        }

        return TimeSpan.FromSeconds(Math.Min(ElapsedSeconds, PlannedSeconds));
    }

    /// <summary>Fraction of the planned duration still remaining, in the range 0..1.</summary>
    public double RemainingFractionAt(DateTime nowUtc)
    {
        if (PlannedSeconds <= 0)
        {
            return 0;
        }

        return Math.Clamp(RemainingAt(nowUtc).TotalSeconds / PlannedSeconds, 0d, 1d);
    }

    public FocusSession Clone() => (FocusSession)MemberwiseClone();
}
