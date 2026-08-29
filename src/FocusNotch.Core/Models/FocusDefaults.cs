namespace FocusNotch.Core.Models;

public static class FocusDefaults
{
    /// <summary>Default planned focus duration (30 minutes).</summary>
    public const long DefaultSeconds = 30 * 60;

    /// <summary>Shortest session the duration picker will accept.</summary>
    public const long MinimumSeconds = 10;

    public const int MaxHours = 99;

    /// <summary>99:59:59. Every duration and aggregate is a 64-bit value so this cannot overflow.</summary>
    public const long MaxSeconds = MaxHours * 3600L + 59 * 60 + 59;

    /// <summary>The presets offered above the three duration columns.</summary>
    public static readonly IReadOnlyList<(string Label, long Seconds)> Presets = new[]
    {
        ("25m", 25 * 60L),
        ("45m", 45 * 60L),
        ("1h", 3600L),
        ("2h", 7200L)
    };
}
