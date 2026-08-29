namespace FocusNotch.Core.Models;

/// <summary>
/// One uninterrupted stretch of a session actually running.
///
/// Time spent is derived by adding these up, never by incrementing a stored total: a counter
/// that is bumped on a tick drifts across sleep, a crash or a missed tick, whereas a pair of
/// instants either was recorded or was not. A segment with no end is the run in progress.
/// </summary>
public sealed class FocusSegment
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid SessionId { get; set; }

    /// <summary>
    /// Copied from the session when the segment opens. Keeping it here means a segment can be
    /// attributed to its task without a join, and survives the session being re-pointed.
    /// </summary>
    public Guid? TaskId { get; set; }

    public DateTime StartedAtUtc { get; set; }

    /// <summary>Null while this is the run in progress.</summary>
    public DateTime? EndedAtUtc { get; set; }

    public bool IsOpen => EndedAtUtc is null;

    /// <summary>Whole seconds between the two instants, never negative.</summary>
    public long SecondsAt(DateTime nowUtc)
    {
        var end = EndedAtUtc ?? nowUtc;
        var span = end - StartedAtUtc;
        return span <= TimeSpan.Zero ? 0 : (long)Math.Round(span.TotalSeconds, MidpointRounding.AwayFromZero);
    }

    public FocusSegment Clone() => (FocusSegment)MemberwiseClone();
}
