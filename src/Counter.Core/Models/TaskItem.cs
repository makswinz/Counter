namespace Counter.Core.Models;

/// <summary>
/// A single to-do item. <see cref="ScheduledDate"/> is a plain calendar date so that
/// changing the machine timezone never moves a task to a different day.
/// </summary>
public sealed class TaskItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Title { get; set; } = string.Empty;

    public string? Note { get; set; }

    public DateOnly? ScheduledDate { get; set; }

    /// <summary>
    /// Planned focus length, up to 99:59:59. A 64-bit value so neither this nor anything
    /// aggregated from it can overflow at the top of the supported range.
    /// </summary>
    public long EstimatedSeconds { get; set; } = FocusDefaults.DefaultSeconds;

    public bool IsCompleted { get; set; }

    public DateTime? CompletedAtUtc { get; set; }

    /// <summary>
    /// The local calendar day this task counts as productivity for, set when it is completed.
    /// Stored as a plain date rather than derived from <see cref="CompletedAtUtc"/> so that
    /// completing a task that was scheduled for a past day credits that day, and so that a
    /// later timezone change can never move an already-earned contribution.
    /// </summary>
    public DateOnly? CompletedForDate { get; set; }

    /// <summary>
    /// Set instead of removing the row. Deleting a task must not erase the hours already spent
    /// on it, so the task leaves every list but its sessions, segments and manual entries stay
    /// attached to it and keep answering for the statistics.
    /// </summary>
    public DateTime? DeletedAtUtc { get; set; }

    public bool IsDeleted => DeletedAtUtc.HasValue;

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public int SortOrder { get; set; }

    public TaskItem Clone() => (TaskItem)MemberwiseClone();
}
