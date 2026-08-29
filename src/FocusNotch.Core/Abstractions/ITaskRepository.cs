using FocusNotch.Core.Models;

namespace FocusNotch.Core.Abstractions;

public interface ITaskRepository
{
    /// <summary>Every task that has not been deleted, in sort order.</summary>
    IReadOnlyList<TaskItem> GetAll();

    TaskItem? Get(Guid id);

    void Add(TaskItem task);

    void Update(TaskItem task);

    /// <summary>
    /// Marks the task deleted without removing the row. Its focus sessions, segments and manual
    /// time stay attached to it, so deleting a task never erases the history of work done on it.
    /// </summary>
    void Delete(Guid id);

    /// <summary>Brings a soft-deleted task back, exactly as it was.</summary>
    void Restore(Guid id);

    /// <summary>Highest existing sort order plus one, so new tasks land at the end.</summary>
    int NextSortOrder();

    /// <summary>
    /// The contribution date of every completed task inside the window, one entry per task, so
    /// two tasks finished on the same day are two contributions.
    /// </summary>
    IReadOnlyList<DateOnly> GetCompletionDates(DateOnly fromInclusive, DateOnly toInclusive);
}
