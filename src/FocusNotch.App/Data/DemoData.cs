using FocusNotch.Core.Abstractions;
using FocusNotch.Core.Models;
using FocusNotch.Core.Time;

namespace FocusNotch.App.Data;

/// <summary>
/// Sample content for the --demo switch. It is only ever written into a database that has no
/// tasks at all, so a normal launch can never inject rows into real user data.
/// </summary>
public static class DemoData
{
    public static bool SeedIfEmpty(ITaskRepository tasks, IClock clock)
    {
        if (tasks.GetAll().Count > 0)
        {
            return false;
        }

        var now = clock.UtcNow;
        var today = clock.Today();

        var samples = new[]
        {
            new TaskItem
            {
                Title = "Design daily",
                Note = "Design and brainstorming for new upcoming products",
                ScheduledDate = today,
                EstimatedSeconds = 30 * 60,
                SortOrder = 0
            },
            new TaskItem
            {
                Title = "Fix bug for Screen",
                Note = "Fix bug - 7 from Sentry reported",
                ScheduledDate = today,
                EstimatedSeconds = 25 * 60,
                SortOrder = 1
            },
            new TaskItem
            {
                Title = "Build an app for AI token tracker",
                Note = "Vibe code using Claude Code",
                ScheduledDate = today,
                EstimatedSeconds = 50 * 60,
                SortOrder = 2
            },
            new TaskItem
            {
                Title = "Write the weekly review",
                Note = "What moved, what stalled, what is next",
                ScheduledDate = today.AddDays(1),
                EstimatedSeconds = 20 * 60,
                SortOrder = 3
            },
            new TaskItem
            {
                Title = "Read the WPF composition notes",
                Note = null,
                ScheduledDate = null,
                EstimatedSeconds = 45 * 60,
                SortOrder = 4
            }
        };

        foreach (var task in samples)
        {
            task.CreatedAtUtc = now;
            task.UpdatedAtUtc = now;
            tasks.Add(task);
        }

        return true;
    }
}
