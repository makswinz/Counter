namespace FocusNotch.Core.Models;

/// <summary>
/// Why a focus session stopped. Stored on the row so the history can say what happened rather
/// than only that the session is no longer live.
/// </summary>
public enum SessionEndReason
{
    /// <summary>Still live, or ended before this column existed.</summary>
    None = 0,

    /// <summary>The countdown reached zero on its own.</summary>
    Completed = 1,

    /// <summary>The user pressed Stop.</summary>
    StoppedByUser = 2,

    /// <summary>The task it was focusing was marked complete.</summary>
    TaskCompleted = 3,

    /// <summary>Focus moved to another task.</summary>
    SwitchedTask = 4,

    /// <summary>The task it pointed at was deleted.</summary>
    TaskDeleted = 5,

    /// <summary>Cancelled by startup repair after more than one live session was found.</summary>
    RepairedDuplicate = 6
}
