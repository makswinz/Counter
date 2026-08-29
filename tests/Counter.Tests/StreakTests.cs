using Counter.Core.Models;
using Counter.Core.Streaks;
using Xunit;

namespace Counter.Tests;

public class StreakTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static DateTime At(int year, int month, int day, int hour = 12, int minute = 0)
        => new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    // ---------------------------------------------------------------- Consecutive days

    [Fact]
    public void Consecutive_days_ending_today_all_count()
    {
        var counts = StreakCalculator.CountByLocalDay(
            new[] { At(2026, 8, 24), At(2026, 8, 25), At(2026, 8, 26), At(2026, 8, 27), At(2026, 8, 28) },
            Utc);

        Assert.Equal(5, StreakCalculator.CurrentStreak(counts, new DateOnly(2026, 8, 28)));
    }

    [Fact]
    public void Several_sessions_on_one_day_still_count_as_a_single_day()
    {
        var counts = StreakCalculator.CountByLocalDay(
            new[] { At(2026, 8, 27, 9), At(2026, 8, 27, 14), At(2026, 8, 28, 8), At(2026, 8, 28, 17) },
            Utc);

        Assert.Equal(2, StreakCalculator.CurrentStreak(counts, new DateOnly(2026, 8, 28)));
        Assert.Equal(2, counts[new DateOnly(2026, 8, 27)]);
    }

    [Fact]
    public void A_streak_ending_yesterday_is_still_live_before_today_has_a_session()
    {
        var counts = StreakCalculator.CountByLocalDay(
            new[] { At(2026, 8, 25), At(2026, 8, 26), At(2026, 8, 27) },
            Utc);

        Assert.Equal(3, StreakCalculator.CurrentStreak(counts, new DateOnly(2026, 8, 28)));
    }

    // ---------------------------------------------------------------- Gaps

    [Fact]
    public void A_gap_breaks_the_streak_at_the_gap()
    {
        var counts = StreakCalculator.CountByLocalDay(
            new[]
            {
                At(2026, 8, 20), At(2026, 8, 21), At(2026, 8, 22),
                // 23 and 24 missing
                At(2026, 8, 25), At(2026, 8, 26), At(2026, 8, 27), At(2026, 8, 28)
            },
            Utc);

        Assert.Equal(4, StreakCalculator.CurrentStreak(counts, new DateOnly(2026, 8, 28)));
    }

    [Fact]
    public void Nothing_yesterday_or_today_means_no_current_streak()
    {
        var counts = StreakCalculator.CountByLocalDay(
            new[] { At(2026, 8, 20), At(2026, 8, 21), At(2026, 8, 22) },
            Utc);

        Assert.Equal(0, StreakCalculator.CurrentStreak(counts, new DateOnly(2026, 8, 28)));
    }

    [Fact]
    public void An_empty_history_has_a_zero_streak()
        => Assert.Equal(0, StreakCalculator.CurrentStreak(
            new Dictionary<DateOnly, int>(), new DateOnly(2026, 8, 28)));

    // ---------------------------------------------------------------- Local day grouping

    [Fact]
    public void Sessions_are_grouped_by_local_calendar_day_not_by_utc_day()
    {
        // UTC+2: 23:30 UTC on the 27th is 01:30 local on the 28th.
        var berlin = TimeZoneInfo.CreateCustomTimeZone("Test+2", TimeSpan.FromHours(2), "Test+2", "Test+2");

        var counts = StreakCalculator.CountByLocalDay(
            new[] { At(2026, 8, 27, 23, 30), At(2026, 8, 28, 10) },
            berlin);

        Assert.False(counts.ContainsKey(new DateOnly(2026, 8, 27)));
        Assert.Equal(2, counts[new DateOnly(2026, 8, 28)]);
    }

    [Fact]
    public void A_negative_offset_pushes_an_early_utc_session_back_a_local_day()
    {
        // UTC-5: 02:00 UTC on the 28th is 21:00 local on the 27th.
        var newYork = TimeZoneInfo.CreateCustomTimeZone("Test-5", TimeSpan.FromHours(-5), "Test-5", "Test-5");

        var counts = StreakCalculator.CountByLocalDay(new[] { At(2026, 8, 28, 2) }, newYork);

        Assert.Equal(1, counts[new DateOnly(2026, 8, 27)]);
        Assert.False(counts.ContainsKey(new DateOnly(2026, 8, 28)));
    }

    [Fact]
    public void Midnight_exactly_belongs_to_the_day_that_starts()
    {
        var counts = StreakCalculator.CountByLocalDay(new[] { At(2026, 8, 28, 0, 0) }, Utc);
        Assert.Equal(1, counts[new DateOnly(2026, 8, 28)]);
    }

    [Fact]
    public void Only_completed_sessions_contribute_to_the_streak()
    {
        var sessions = new[]
        {
            new FocusSession { Status = FocusSessionStatus.Completed, CompletedAtUtc = At(2026, 8, 28) },
            new FocusSession { Status = FocusSessionStatus.Cancelled, CompletedAtUtc = At(2026, 8, 28) },
            new FocusSession { Status = FocusSessionStatus.Running, CompletedAtUtc = null },
            new FocusSession { Status = FocusSessionStatus.Paused, CompletedAtUtc = null }
        };

        var counts = StreakCalculator.CountByLocalDay(sessions, Utc);

        Assert.Single(counts);
        Assert.Equal(1, counts[new DateOnly(2026, 8, 28)]);
    }

    // ---------------------------------------------------------------- Heatmap

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(4, 4)]
    [InlineData(9, 4)]
    [InlineData(-3, 0)]
    public void Heatmap_intensity_saturates_at_level_four(int sessions, int expected)
        => Assert.Equal(expected, StreakCalculator.Intensity(sessions));

    [Fact]
    public void Heatmap_is_twelve_weeks_of_seven_days()
    {
        var cells = StreakCalculator.BuildHeatmap(
            new Dictionary<DateOnly, int>(), new DateOnly(2026, 8, 28));

        Assert.Equal(84, cells.Count);
        Assert.Equal(12, cells.Select(c => c.Week).Distinct().Count());
        Assert.Equal(7, cells.Select(c => c.Row).Distinct().Count());
    }

    [Fact]
    public void Heatmap_rows_run_monday_to_sunday_and_end_on_the_current_week()
    {
        var today = new DateOnly(2026, 8, 28); // a Friday
        var cells = StreakCalculator.BuildHeatmap(new Dictionary<DateOnly, int>(), today);

        Assert.All(cells, c => Assert.Equal(c.Row, StreakCalculator.MondayIndex(c.Date.DayOfWeek)));

        var lastWeek = cells.Where(c => c.Week == 11).ToList();
        Assert.Equal(new DateOnly(2026, 8, 24), lastWeek[0].Date);  // Monday
        Assert.Equal(new DateOnly(2026, 8, 30), lastWeek[6].Date);  // Sunday
        Assert.Contains(lastWeek, c => c.Date == today);
    }

    [Fact]
    public void Heatmap_carries_the_per_day_counts_and_marks_future_squares()
    {
        var today = new DateOnly(2026, 8, 28);
        var counts = new Dictionary<DateOnly, int>
        {
            [new DateOnly(2026, 8, 26)] = 3,
            [today] = 5
        };

        var cells = StreakCalculator.BuildHeatmap(counts, today);

        Assert.Equal(3, cells.Single(c => c.Date == new DateOnly(2026, 8, 26)).Count);
        Assert.Equal(3, cells.Single(c => c.Date == new DateOnly(2026, 8, 26)).Intensity);
        Assert.Equal(4, cells.Single(c => c.Date == today).Intensity);
        Assert.False(cells.Single(c => c.Date == today).IsFuture);
        Assert.True(cells.Single(c => c.Date == new DateOnly(2026, 8, 29)).IsFuture);
    }

    [Fact]
    public void Heatmap_tooltip_names_the_date_and_what_happened_on_it()
    {
        var today = new DateOnly(2026, 8, 28);
        var counts = new Dictionary<DateOnly, int> { [today] = 1 };
        var cell = StreakCalculator.BuildHeatmap(counts, today).Single(c => c.Date == today);

        Assert.StartsWith("Friday 28 August", cell.Tooltip);
        Assert.Contains("1 task completed", cell.Tooltip);
    }

    [Fact]
    public void Heatmap_tooltip_pluralises_more_than_one_of_a_kind()
    {
        var today = new DateOnly(2026, 8, 28);
        var counts = new Dictionary<DateOnly, int> { [today] = 2 };
        var cell = StreakCalculator.BuildHeatmap(counts, today).Single(c => c.Date == today);

        Assert.Contains("2 tasks completed", cell.Tooltip);
    }

    [Fact]
    public void An_empty_day_says_so_rather_than_showing_only_a_date()
    {
        var today = new DateOnly(2026, 8, 28);
        var cell = StreakCalculator
            .BuildHeatmap(new Dictionary<DateOnly, int>(), today)
            .Single(c => c.Date == today);

        Assert.Contains("No contributions", cell.Tooltip);
    }

    [Fact]
    public void The_accessible_description_reads_as_one_line()
    {
        var today = new DateOnly(2026, 8, 28);
        var counts = new Dictionary<DateOnly, int> { [today] = 2 };
        var cell = StreakCalculator.BuildHeatmap(counts, today).Single(c => c.Date == today);

        Assert.Equal("Friday 28 August 2026, 2 contributions", cell.AccessibleDescription);
        Assert.DoesNotContain("\n", cell.AccessibleDescription);
    }

    [Fact]
    public void The_longest_streak_is_found_anywhere_in_the_record()
    {
        var today = new DateOnly(2026, 8, 28);
        var counts = new Dictionary<DateOnly, int>
        {
            // A four-day run in July, then a two-day run ending today.
            [new DateOnly(2026, 7, 1)] = 1,
            [new DateOnly(2026, 7, 2)] = 1,
            [new DateOnly(2026, 7, 3)] = 1,
            [new DateOnly(2026, 7, 4)] = 1,
            [new DateOnly(2026, 8, 27)] = 1,
            [today] = 1
        };

        Assert.Equal(4, StreakCalculator.LongestStreak(counts, today));
        Assert.Equal(2, StreakCalculator.CurrentStreak(counts, today));
    }

    [Fact]
    public void A_future_run_cannot_invent_a_longest_streak()
    {
        var today = new DateOnly(2026, 8, 28);
        var counts = new Dictionary<DateOnly, int>
        {
            [today.AddDays(1)] = 1,
            [today.AddDays(2)] = 1,
            [today.AddDays(3)] = 1
        };

        Assert.Equal(0, StreakCalculator.LongestStreak(counts, today));
    }
}
