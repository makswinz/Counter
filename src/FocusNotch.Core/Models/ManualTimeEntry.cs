namespace FocusNotch.Core.Models;

/// <summary>
/// Work recorded after the fact, without a timer having run.
///
/// Kept in its own table rather than as a synthetic segment so that timer time and hand-entered
/// time can always be told apart and can never be counted twice.
/// </summary>
public sealed class ManualTimeEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid? TaskId { get; set; }

    /// <summary>Snapshot of the task title, so the entry still reads correctly in history.</summary>
    public string? TaskTitle { get; set; }

    /// <summary>The local calendar day the work belongs to.</summary>
    public DateOnly LocalDate { get; set; }

    public long Seconds { get; set; }

    public string? Note { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public ManualTimeEntry Clone() => (ManualTimeEntry)MemberwiseClone();
}
