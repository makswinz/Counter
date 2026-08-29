using FocusNotch.Core.Models;

namespace FocusNotch.Core.Abstractions;

public interface IFocusSessionRepository
{
    /// <summary>Inserts or updates a session by primary key.</summary>
    void Save(FocusSession session);

    /// <summary>
    /// Writes sessions and their running segments in one transaction, so a switch can never be
    /// observed - or survive a crash - as "old one cancelled but new one never started", as two
    /// live sessions at once, or as a session whose final run was never closed.
    /// </summary>
    void SaveAll(IReadOnlyList<FocusSession> sessions, IReadOnlyList<FocusSegment> segments);

    /// <summary>The single running or paused session, if one survived the last shutdown.</summary>
    FocusSession? GetActive();

    /// <summary>
    /// Every running or paused session, newest first. There must only ever be one; this exists
    /// so startup can detect and repair a database that somehow holds more.
    /// </summary>
    IReadOnlyList<FocusSession> GetActiveSessions();

    FocusSession? Get(Guid id);

    /// <summary>Every segment belonging to one session, oldest first.</summary>
    IReadOnlyList<FocusSegment> GetSegments(Guid sessionId);

    /// <summary>
    /// Every run that was never closed. After a clean shutdown there is at most one, belonging
    /// to the live session; after a crash there may be one belonging to a session that is no
    /// longer live, and startup has to close it rather than let it keep growing.
    /// </summary>
    IReadOnlyList<FocusSegment> GetOpenSegments();

    /// <summary>Completion instants of successfully completed sessions, newest last.</summary>
    IReadOnlyList<DateTime> GetCompletionsUtc(DateTime sinceUtc);

    /// <summary>
    /// The contribution date of every successfully completed session inside the window, one
    /// entry per session. Cancelled, running and paused sessions are never included.
    /// </summary>
    IReadOnlyList<DateOnly> GetCompletionDates(DateOnly fromInclusive, DateOnly toInclusive);
}
