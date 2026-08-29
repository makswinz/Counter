namespace Counter.Core.Focus;

public static class TimeFormat
{
    /// <summary>
    /// MM:SS below an hour, H:MM:SS from one hour, HH:MM:SS from ten. Never negative.
    ///
    /// The notch reserves room for the widest form it can reach, so a countdown crossing an
    /// hour boundary changes the digits without changing the measured width of the label.
    /// </summary>
    public static string Countdown(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        // Round up so a countdown shows 30:00 for the whole first second, not 29:59.
        var total = (long)Math.Ceiling(value.TotalSeconds - 0.0005);
        if (total < 0)
        {
            total = 0;
        }

        var hours = total / 3600;
        var minutes = total % 3600 / 60;
        var seconds = total % 60;

        if (hours >= 10)
        {
            return string.Format("{0:00}:{1:00}:{2:00}", hours, minutes, seconds);
        }

        return hours > 0
            ? string.Format("{0}:{1:00}:{2:00}", hours, minutes, seconds)
            : string.Format("{0:00}:{1:00}", minutes, seconds);
    }

    /// <summary>Compact label used on the duration pill of a task row, e.g. "30m" or "1h 05m".</summary>
    public static string Compact(long totalSeconds)
    {
        if (totalSeconds < 0)
        {
            totalSeconds = 0;
        }

        var hours = totalSeconds / 3600;
        var minutes = totalSeconds % 3600 / 60;
        var seconds = totalSeconds % 60;

        if (hours > 0)
        {
            return string.Format("{0}h {1:00}m", hours, minutes);
        }

        if (minutes > 0)
        {
            return minutes + "m";
        }

        return seconds + "s";
    }

    /// <summary>
    /// Time actually spent, as shown on a task row: "42m", "1h 24m", "12h 08m". Anything under a
    /// minute reads as "&lt;1m" rather than a second count, because a row is not a stopwatch.
    /// </summary>
    public static string Spent(long totalSeconds)
    {
        if (totalSeconds <= 0)
        {
            return "0m";
        }

        var hours = totalSeconds / 3600;
        var minutes = totalSeconds % 3600 / 60;

        if (hours > 0)
        {
            return string.Format("{0}h {1:00}m", hours, minutes);
        }

        return minutes > 0 ? minutes + "m" : "<1m";
    }

    /// <summary>The same value with the word, for tooltips and summaries: "1h 35m focused".</summary>
    public static string SpentWithSuffix(long totalSeconds, string suffix) => Spent(totalSeconds) + " " + suffix;

    /// <summary>HH:MM:SS with every field padded. Used by the duration picker's own read-back.</summary>
    public static string Clock(long totalSeconds)
    {
        if (totalSeconds < 0)
        {
            totalSeconds = 0;
        }

        return string.Format(
            "{0:00}:{1:00}:{2:00}", totalSeconds / 3600, totalSeconds % 3600 / 60, totalSeconds % 60);
    }
}
