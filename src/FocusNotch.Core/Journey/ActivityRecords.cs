using FocusNotch.Core.Models;

namespace FocusNotch.Core.Journey;

/// <summary>
/// A task as the history sees it. Carries its own title, so a row that has been renamed or
/// deleted still reads correctly in statistics long after it left the planner.
/// </summary>
public sealed record TaskRecord(
    Guid Id,
    string Title,
    DateOnly? ScheduledDate,
    bool IsCompleted,
    DateOnly? CompletedForDate,
    long EstimatedSeconds,
    bool IsDeleted);

/// <summary>A finished or live session, reduced to what the history needs.</summary>
public sealed record SessionRecord(
    Guid Id,
    Guid? TaskId,
    string? TaskTitle,
    FocusSessionStatus Status,
    SessionEndReason EndReason,
    long PlannedSeconds,
    DateTime StartedAtUtc,
    DateTime? CompletedAtUtc,
    DateOnly? CompletedForDate);

/// <summary>
/// Everything the journey and the statistics surface need for one window, read in one pass.
///
/// The reader returns raw rows and nothing else: every aggregate, every split across midnight
/// and every streak is computed in <see cref="Statistics.StatisticsCalculator"/> or
/// <see cref="JourneyActivityService"/>, where it can be tested without a database.
/// </summary>
public sealed record ActivitySnapshot(
    IReadOnlyList<TaskRecord> Tasks,
    IReadOnlyList<SessionRecord> Sessions,
    IReadOnlyList<FocusSegment> Segments,
    IReadOnlyList<ManualTimeEntry> ManualEntries)
{
    public static readonly ActivitySnapshot Empty = new(
        Array.Empty<TaskRecord>(),
        Array.Empty<SessionRecord>(),
        Array.Empty<FocusSegment>(),
        Array.Empty<ManualTimeEntry>());
}

/// <summary>What one local calendar day actually holds. Backs both the streak and the tooltip.</summary>
public sealed record DayActivity(
    DateOnly Date,
    int CompletedTasks,
    int CompletedSessions,
    int ManualEntries,
    long FocusSeconds,
    long ManualSeconds)
{
    public static DayActivity Empty(DateOnly date) => new(date, 0, 0, 0, 0, 0);

    /// <summary>
    /// A completed task is one contribution, a completed focus session is one, and a positive
    /// manual entry is one. They are separate on purpose: finishing a task and finishing a
    /// session are different pieces of work and both deserve to count.
    /// </summary>
    public int Contributions => CompletedTasks + CompletedSessions + ManualEntries;

    public long TotalSeconds => FocusSeconds + ManualSeconds;
}
