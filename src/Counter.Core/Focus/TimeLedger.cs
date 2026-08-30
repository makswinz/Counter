using Counter.Core.Models;

namespace Counter.Core.Focus;

/// <summary>One stretch of running time reduced to what any consumer needs from it.</summary>
public readonly record struct RunSpan(Guid SessionId, Guid? TaskId, DateTime StartUtc, DateTime EndUtc)
{
    public long Seconds
    {
        get
        {
            var span = EndUtc - StartUtc;
            return span <= TimeSpan.Zero ? 0 : (long)Math.Round(span.TotalSeconds, MidpointRounding.AwayFromZero);
        }
    }
}

/// <summary>
/// Turns recorded runs into the numbers the interface shows.
///
/// Everything here is a pure function of instants, so the same code answers for a live session,
/// a session restored from storage and a session that finished a month ago. Paused stretches
/// simply are not segments, so they can never be counted; nothing has to remember to subtract
/// them.
/// </summary>
public static class TimeLedger
{
    /// <summary>
    /// Materialises segments into closed spans. An open segment is closed at
    /// <paramref name="nowUtc"/>, or earlier when <paramref name="capUtc"/> says the timer was
    /// due to reach zero before that, so a session left running past its planned end does not
    /// keep accruing time on its own.
    /// </summary>
    public static IReadOnlyList<RunSpan> ToSpans(
        IEnumerable<FocusSegment> segments,
        DateTime nowUtc,
        Func<Guid, DateTime?>? capUtc = null)
    {
        var spans = new List<RunSpan>();

        foreach (var segment in segments)
        {
            var end = segment.EndedAtUtc ?? nowUtc;

            if (segment.EndedAtUtc is null && capUtc?.Invoke(segment.SessionId) is { } cap && end > cap)
            {
                end = cap;
            }

            if (end < segment.StartedAtUtc)
            {
                end = segment.StartedAtUtc;
            }

            spans.Add(new RunSpan(segment.SessionId, segment.TaskId, segment.StartedAtUtc, end));
        }

        return spans;
    }

    /// <summary>Total running seconds across the given spans.</summary>
    public static long TotalSeconds(IEnumerable<RunSpan> spans)
    {
        long total = 0;
        foreach (var span in spans)
        {
            total += span.Seconds;
        }

        return total;
    }

    /// <summary>Running seconds per task. Spans with no task are grouped under the null key.</summary>
    public static IReadOnlyDictionary<Guid, long> SecondsByTask(IEnumerable<RunSpan> spans)
    {
        var totals = new Dictionary<Guid, long>();

        foreach (var span in spans)
        {
            if (span.TaskId is not { } taskId)
            {
                continue;
            }

            totals[taskId] = totals.TryGetValue(taskId, out var existing) ? existing + span.Seconds : span.Seconds;
        }

        return totals;
    }

    /// <summary>
    /// Splits running time across local calendar days.
    ///
    /// A session from 23:30 to 00:30 is thirty minutes on each day, not an hour on whichever end
    /// happened to be picked. The split is done on local instants, so a daily chart lines up
    /// with what the person actually experienced, and daylight-saving transitions are handled by
    /// the timezone conversion rather than by assuming a day is 86 400 seconds long.
    /// </summary>
    public static IReadOnlyDictionary<DateOnly, long> SecondsByLocalDay(
        IEnumerable<RunSpan> spans,
        TimeZoneInfo zone)
    {
        var totals = new Dictionary<DateOnly, long>();

        foreach (var span in spans)
        {
            if (span.Seconds <= 0)
            {
                continue;
            }

            var localStart = ToLocal(span.StartUtc, zone);
            var localEnd = ToLocal(span.EndUtc, zone);

            var day = DateOnly.FromDateTime(localStart);
            var lastDay = DateOnly.FromDateTime(localEnd);
            var cursor = localStart;

            // Walk day by day. A run cannot realistically cross more than a handful of
            // boundaries, and the loop is bounded by the run's own length either way.
            while (day <= lastDay)
            {
                var midnight = day.AddDays(1).ToDateTime(TimeOnly.MinValue);
                var segmentEnd = localEnd < midnight ? localEnd : midnight;

                var seconds = (long)Math.Round(
                    (segmentEnd - cursor).TotalSeconds, MidpointRounding.AwayFromZero);

                if (seconds > 0)
                {
                    totals[day] = totals.TryGetValue(day, out var existing) ? existing + seconds : seconds;
                }

                if (segmentEnd >= localEnd)
                {
                    break;
                }

                cursor = segmentEnd;
                day = day.AddDays(1);
            }
        }

        return totals;
    }

    /// <summary>Splits running time across the hours of one local day, for the Today chart.</summary>
    public static IReadOnlyDictionary<int, long> SecondsByLocalHour(
        IEnumerable<RunSpan> spans,
        DateOnly day,
        TimeZoneInfo zone)
    {
        var totals = new Dictionary<int, long>();
        var dayStart = day.ToDateTime(TimeOnly.MinValue);
        var dayEnd = day.AddDays(1).ToDateTime(TimeOnly.MinValue);

        foreach (var span in spans)
        {
            var localStart = ToLocal(span.StartUtc, zone);
            var localEnd = ToLocal(span.EndUtc, zone);

            if (localEnd <= dayStart || localStart >= dayEnd)
            {
                continue;
            }

            var cursor = localStart < dayStart ? dayStart : localStart;
            var stop = localEnd > dayEnd ? dayEnd : localEnd;

            while (cursor < stop)
            {
                var hour = cursor.Hour;
                var nextHour = cursor.Date.AddHours(hour + 1);
                var slice = stop < nextHour ? stop : nextHour;

                var seconds = (long)Math.Round((slice - cursor).TotalSeconds, MidpointRounding.AwayFromZero);
                if (seconds > 0)
                {
                    totals[hour] = totals.TryGetValue(hour, out var existing) ? existing + seconds : seconds;
                }

                cursor = slice;
            }
        }

        return totals;
    }

    /// <summary>Manual entries added up per local day. They are already stored as plain dates.</summary>
    public static IReadOnlyDictionary<DateOnly, long> ManualSecondsByLocalDay(
        IEnumerable<ManualTimeEntry> entries)
    {
        var totals = new Dictionary<DateOnly, long>();

        // Signed. A negative entry is a correction - a timer left running over lunch - and it
        // has to come off the day it names, or correcting a mistake would do nothing at all.
        foreach (var entry in entries)
        {
            if (entry.Seconds == 0)
            {
                continue;
            }

            totals[entry.LocalDate] = totals.TryGetValue(entry.LocalDate, out var existing)
                ? existing + entry.Seconds
                : entry.Seconds;
        }

        Floor(totals);

        return totals;
    }

    /// <summary>Manual seconds per task, so a task total can include hand-entered work once.</summary>
    /// <summary>
    /// Takes any negative total up to zero.
    ///
    /// Removals are allowed to exceed what a day or a task holds, because the user is correcting
    /// a total rather than deleting a particular record. What comes out the other side is still
    /// an amount of time, and time spent is never less than none.
    /// </summary>
    private static void Floor<TKey>(Dictionary<TKey, long> totals) where TKey : notnull
    {
        foreach (var key in totals.Keys.ToList())
        {
            if (totals[key] < 0)
            {
                totals[key] = 0;
            }
        }
    }

    public static IReadOnlyDictionary<Guid, long> ManualSecondsByTask(IEnumerable<ManualTimeEntry> entries)
    {
        var totals = new Dictionary<Guid, long>();

        foreach (var entry in entries)
        {
            if (entry.Seconds == 0 || entry.TaskId is not { } taskId)
            {
                continue;
            }

            totals[taskId] = totals.TryGetValue(taskId, out var existing) ? existing + entry.Seconds : entry.Seconds;
        }

        Floor(totals);

        return totals;
    }

    private static DateTime ToLocal(DateTime utc, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
}
