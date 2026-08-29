using System.Globalization;
using System.Text;
using Counter.Core.Focus;
using Counter.Core.Journey;

namespace Counter.Core.Streaks;

/// <summary>One square of the journey heatmap.</summary>
/// <param name="Week">Zero-based column, oldest week first.</param>
/// <param name="Row">Zero-based row, Monday = 0 through Sunday = 6.</param>
public sealed record HeatmapCell(
    DateOnly Date,
    DayActivity Activity,
    int Intensity,
    int Week,
    int Row,
    bool IsFuture)
{
    /// <summary>Contributions on this day: completed tasks, completed sessions, manual entries.</summary>
    public int Count => Activity.Contributions;

    /// <summary>
    /// What hovering or focusing the square says. A future day carries only its date, because
    /// there is nothing to report about it yet.
    /// </summary>
    public string Tooltip
    {
        get
        {
            var date = Date.ToString("dddd d MMMM", CultureInfo.InvariantCulture);

            if (IsFuture)
            {
                return date;
            }

            var text = new StringBuilder(date);

            if (Activity.CompletedTasks > 0)
            {
                text.Append('\n')
                    .Append(Activity.CompletedTasks)
                    .Append(Activity.CompletedTasks == 1 ? " task completed" : " tasks completed");
            }

            if (Activity.FocusSeconds > 0)
            {
                text.Append('\n').Append(TimeFormat.Spent(Activity.FocusSeconds)).Append(" focused");
            }

            if (Activity.ManualSeconds > 0)
            {
                text.Append('\n').Append(TimeFormat.Spent(Activity.ManualSeconds)).Append(" manually added");
            }

            if (Activity.Contributions == 0)
            {
                text.Append("\nNo contributions");
            }

            return text.ToString();
        }
    }

    /// <summary>A single-line reading of the same thing, for screen readers.</summary>
    public string AccessibleDescription
    {
        get
        {
            var date = Date.ToString("dddd d MMMM yyyy", CultureInfo.InvariantCulture);

            if (IsFuture)
            {
                return date + ", no activity yet";
            }

            var unit = Activity.Contributions == 1 ? "contribution" : "contributions";
            return date + ", " + Activity.Contributions + " " + unit;
        }
    }
}
