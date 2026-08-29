namespace FocusNotch.Core.Statistics;

/// <summary>Actual time recorded against one task, added up from what is stored.</summary>
public sealed record TaskTimeTotals(
    Guid TaskId,
    long FocusSeconds,
    long ManualSeconds,
    int SessionCount,
    DateTime? LastFocusedUtc)
{
    public static TaskTimeTotals Empty(Guid taskId) => new(taskId, 0, 0, 0, null);

    public long TotalSeconds => FocusSeconds + ManualSeconds;
}

/// <summary>
/// Reads per-task totals cheaply, so every row can show its time without the panel loading the
/// whole history.
///
/// Only closed runs are summed. The run in progress is added by the caller from the segment it
/// already holds in memory, which is what lets a running row tick without a query every second
/// and keeps the cap at the timer's target in one place rather than duplicated in SQL.
/// </summary>
public interface ITaskTimeReader
{
    IReadOnlyList<TaskTimeTotals> ReadTotals();
}
