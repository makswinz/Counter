using Counter.Core.Focus;
using Counter.Core.Models;
using Counter.Core.Validation;
using Xunit;

namespace Counter.Tests;

public class TaskValidationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n  ")]
    public void An_empty_or_whitespace_title_is_rejected(string? title)
    {
        var result = TaskValidator.ValidateTitle(title);

        Assert.False(result.IsValid);
        Assert.False(string.IsNullOrEmpty(result.Error));
    }

    [Fact]
    public void A_title_at_the_limit_is_accepted_and_one_past_it_is_not()
    {
        Assert.True(TaskValidator.ValidateTitle(new string('a', 140)).IsValid);
        Assert.False(TaskValidator.ValidateTitle(new string('a', 141)).IsValid);
    }

    [Fact]
    public void Surrounding_whitespace_does_not_count_towards_the_title_limit()
        => Assert.True(TaskValidator.ValidateTitle("  " + new string('a', 140) + "  ").IsValid);

    [Fact]
    public void A_note_is_optional_but_capped_at_five_hundred_characters()
    {
        Assert.True(TaskValidator.ValidateNote(null).IsValid);
        Assert.True(TaskValidator.ValidateNote(string.Empty).IsValid);
        Assert.True(TaskValidator.ValidateNote(new string('n', 500)).IsValid);
        Assert.False(TaskValidator.ValidateNote(new string('n', 501)).IsValid);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(9, false)]
    [InlineData(10, true)]
    [InlineData(1800, true)]
    [InlineData(3600, true)]
    [InlineData(7200, true)]
    [InlineData(24 * 3600, true)]
    [InlineData(99 * 3600 + 59 * 60 + 59, true)]
    [InlineData(99 * 3600 + 59 * 60 + 60, false)]
    public void A_duration_must_sit_between_ten_seconds_and_ninety_nine_hours(
        long seconds, bool expected)
        => Assert.Equal(expected, TaskValidator.ValidateDuration(seconds).IsValid);

    [Fact]
    public void The_maximum_duration_is_exactly_ninety_nine_hours_fifty_nine_fifty_nine()
    {
        Assert.Equal(99 * 3600L + 59 * 60 + 59, FocusDefaults.MaxSeconds);
        Assert.True(TaskValidator.ValidateDuration(FocusDefaults.MaxSeconds).IsValid);
        Assert.False(TaskValidator.ValidateDuration(FocusDefaults.MaxSeconds + 1).IsValid);
    }

    [Fact]
    public void ValidateForSave_checks_every_stored_field()
    {
        var task = new TaskItem
        {
            Title = "Ship the notch",
            Note = "Then write it up",
            EstimatedSeconds = FocusDefaults.DefaultSeconds
        };

        Assert.True(TaskValidator.ValidateForSave(task).IsValid);

        task.Title = "  ";
        Assert.False(TaskValidator.ValidateForSave(task).IsValid);

        task.Title = "Ship the notch";
        task.EstimatedSeconds = 3;
        Assert.False(TaskValidator.ValidateForSave(task).IsValid);
    }

    [Fact]
    public void The_default_duration_is_thirty_minutes()
        => Assert.Equal(30 * 60, FocusDefaults.DefaultSeconds);

    [Theory]
    [InlineData(1800, "30m")]
    [InlineData(300, "5m")]
    [InlineData(45, "45s")]
    [InlineData(3900, "1h 05m")]
    public void Compact_duration_labels_stay_short(int seconds, string expected)
        => Assert.Equal(expected, TimeFormat.Compact(seconds));
}
