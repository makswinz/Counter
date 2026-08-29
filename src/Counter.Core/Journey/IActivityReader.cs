namespace Counter.Core.Journey;

/// <summary>
/// Reads history. It is a separate, read-only abstraction from the repositories on purpose:
/// this is the only database work in the app that runs off the UI thread, and giving it its own
/// narrow surface makes it obvious that the implementation must be safe to call from a
/// background thread. It returns raw rows; nothing here aggregates.
/// </summary>
public interface IActivityReader
{
    /// <summary>
    /// Everything inside the window in one pass: tasks scheduled or completed in it, sessions
    /// started or completed in it, every run that overlaps it, and every manual entry on it.
    /// </summary>
    ActivitySnapshot Read(DateOnly fromInclusive, DateOnly toInclusive, TimeZoneInfo zone);
}
