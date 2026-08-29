using Counter.Core.Models;

namespace Counter.Core.Abstractions;

/// <summary>
/// Work recorded by hand. Kept apart from the timer's own segments so the two can never be
/// added together twice, and so a manual entry can be edited or removed on its own.
/// </summary>
public interface IManualTimeRepository
{
    void Add(ManualTimeEntry entry);

    void Delete(Guid id);

    IReadOnlyList<ManualTimeEntry> GetForTask(Guid taskId);

    IReadOnlyList<ManualTimeEntry> GetInRange(DateOnly fromInclusive, DateOnly toInclusive);
}
