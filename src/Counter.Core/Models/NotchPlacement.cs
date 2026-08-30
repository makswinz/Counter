namespace Counter.Core.Models;

/// <summary>
/// Where the notch sits across the top of the screen.
///
/// Centre is where a notch belongs and is the default. The other two exist for a specific and
/// very common collision: a browser keeps its tabs along the top of the window, which is exactly
/// where this sits, and no amount of getting the visuals right makes a panel over somebody's
/// tab strip acceptable. Moving it to one side gets out of the way without giving it up.
/// </summary>
public enum NotchPlacement
{
    Centre = 0,
    Left = 1,
    Right = 2
}

/// <summary>Reading and writing the stored placement, which is the enum's name and nothing else.</summary>
public static class NotchPlacements
{
    /// <summary>In the order the settings panel offers them.</summary>
    public static readonly IReadOnlyList<NotchPlacement> All = new[]
    {
        NotchPlacement.Left, NotchPlacement.Centre, NotchPlacement.Right
    };

    public const NotchPlacement Default = NotchPlacement.Centre;

    /// <summary>
    /// Resolves a stored value. Anything unrecognised falls back rather than throwing: a bad
    /// preference is never a reason to put the window somewhere nobody can find it.
    /// </summary>
    public static NotchPlacement Parse(string? value)
    {
        var text = (value ?? string.Empty).Trim();

        // By name only. Enum.TryParse also accepts the underlying number, so a stored "2" would
        // silently mean Right - which is a quietly surprising thing for a hand-edited setting to
        // do, and not something this application ever writes.
        foreach (var placement in All)
        {
            if (string.Equals(placement.ToString(), text, StringComparison.OrdinalIgnoreCase))
            {
                return placement;
            }
        }

        return Default;
    }

    public static string Label(NotchPlacement placement) => placement switch
    {
        NotchPlacement.Left => "Left",
        NotchPlacement.Right => "Right",
        _ => "Centre"
    };
}
