using Counter.Core.Streaks;

namespace Counter.Core.Journey;

/// <summary>
/// One immutable snapshot of the journey surface. Everything here is derived from the
/// contributions currently in storage, so it can never drift the way an incremented counter
/// would. Quick view, planner and statistics all render this same instance.
/// </summary>
public sealed class JourneyModel
{
    public static readonly JourneyModel Empty = new(
        0, 0, Array.Empty<HeatmapCell>(), new Dictionary<DateOnly, DayActivity>(), default);

    public JourneyModel(
        int currentStreak,
        int longestStreak,
        IReadOnlyList<HeatmapCell> cells,
        IReadOnlyDictionary<DateOnly, DayActivity> days,
        DateOnly today)
    {
        CurrentStreak = currentStreak;
        LongestStreak = longestStreak;
        Cells = cells;
        Days = days;
        Today = today;
    }

    /// <summary>Consecutive productive local days ending today, or yesterday if today is empty.</summary>
    public int CurrentStreak { get; }

    /// <summary>The longest run of consecutive productive days anywhere in the record.</summary>
    public int LongestStreak { get; }

    /// <summary>Twelve weeks by seven rows, oldest week first, Monday first within a week.</summary>
    public IReadOnlyList<HeatmapCell> Cells { get; }

    /// <summary>Every day that carries anything, keyed by local date.</summary>
    public IReadOnlyDictionary<DateOnly, DayActivity> Days { get; }

    public DateOnly Today { get; }

    public string StreakText => CurrentStreak + "d";

    public DayActivity On(DateOnly date) =>
        Days.TryGetValue(date, out var day) ? day : DayActivity.Empty(date);
}
