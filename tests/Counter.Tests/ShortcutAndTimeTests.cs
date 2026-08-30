using Counter.App.ViewModels;
using Counter.Core.Focus;
using Counter.Core.Models;
using Xunit;

namespace Counter.Tests;

/// <summary>
/// Two things that went wrong in the same week, for the same underlying reason: a default that
/// looked harmless in isolation and was not, once it met the rest of the machine.
/// </summary>
public class ShortcutAndTimeTests
{
    // ================================================================ Global shortcuts

    /// <summary>
    /// Gestures that belong to applications, not to whoever registers them first.
    ///
    /// A global hotkey outranks every application shortcut on Windows, and RegisterHotKey does
    /// not fail or warn when it takes one: Ctrl+Shift+N was registered here for "new task" and
    /// silently stopped opening a private window in every browser on the machine.
    /// </summary>
    public static IEnumerable<object[]> ClaimedByApplications() => new[]
    {
        new object[] { "Ctrl+Shift+N" },   // a private window, in every browser there is
        new object[] { "Ctrl+Shift+S" },   // Save As
        new object[] { "Ctrl+Shift+F" },   // find in files
        new object[] { "Ctrl+Shift+P" },   // the command palette
        new object[] { "Ctrl+Shift+T" },   // reopen the closed tab
        new object[] { "Ctrl+Shift+E" },
        new object[] { "Ctrl+Shift+Space" }
    };

    [Theory]
    [MemberData(nameof(ClaimedByApplications))]
    public void No_default_shortcut_takes_a_gesture_applications_already_use(string gesture)
    {
        Assert.DoesNotContain(
            Counter.App.App.HotkeyDefaults,
            entry => string.Equals(entry.Default, gesture, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Every_default_shortcut_is_distinct_and_uses_a_modifier_pair()
    {
        var defaults = Counter.App.App.HotkeyDefaults;

        // Distinct, or two of them race for the same registration and one silently loses.
        Assert.Equal(defaults.Length, defaults.Select(e => e.Default).Distinct().Count());
        Assert.Equal(defaults.Length, defaults.Select(e => e.Id).Distinct().Count());

        foreach (var entry in defaults)
        {
            // A single modifier is not enough for something that outranks the whole machine.
            Assert.Contains('+', entry.Default);
            Assert.StartsWith("Ctrl+Alt+", entry.Default, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(entry.Description));
        }
    }

    // ================================================================ Adjusting time

    private static ManualTimeViewModel Dialog(long available)
    {
        var model = new ManualTimeViewModel();
        model.Load(Guid.NewGuid(), "Write the report", new DateOnly(2026, 8, 29), available);
        return model;
    }

    [Fact]
    public void Adding_and_removing_are_the_same_entry_with_the_sign_reversed()
    {
        var model = Dialog(available: 7200);
        model.Hours = 1;
        model.Minutes = 0;

        Assert.True(model.IsAdding);
        Assert.Equal(3600, model.SignedSeconds);

        model.UseRemovingCommand.Execute(null);

        Assert.True(model.IsRemoving);
        Assert.Equal(-3600, model.SignedSeconds);
        Assert.Equal(3600, model.TotalSeconds);
        Assert.Equal("Remove", model.ActionText);
    }

    [Fact]
    public void A_removal_cannot_exceed_what_the_task_holds()
    {
        // Taking two hours off a task with one on it would leave a negative total, which is not
        // a smaller amount of time, it is a nonsense.
        var model = Dialog(available: 3600);
        model.UseRemovingCommand.Execute(null);

        model.Hours = 0;
        model.Minutes = 59;
        Assert.True(model.CanSave);

        model.Hours = 2;
        Assert.False(model.CanSave);

        // The same amount is fine in the other direction: adding is never capped.
        model.UseAddingCommand.Execute(null);
        Assert.True(model.CanSave);
    }

    [Fact]
    public void An_empty_dial_is_not_an_entry_either_way()
    {
        var model = Dialog(available: 3600);
        model.Hours = 0;
        model.Minutes = 0;

        Assert.False(model.CanSave);

        model.UseRemovingCommand.Execute(null);
        Assert.False(model.CanSave);
    }

    // ================================================================ What a removal does

    private static ManualTimeEntry Entry(Guid task, DateOnly day, long seconds) => new()
    {
        Id = Guid.NewGuid(),
        TaskId = task,
        TaskTitle = "Write the report",
        LocalDate = day,
        Seconds = seconds,
        CreatedAtUtc = new DateTime(2026, 8, 29, 9, 0, 0, DateTimeKind.Utc)
    };

    [Fact]
    public void A_removal_comes_off_the_task_it_names()
    {
        var task = Guid.NewGuid();
        var day = new DateOnly(2026, 8, 29);

        var totals = TimeLedger.ManualSecondsByTask(new[]
        {
            Entry(task, day, 7200),
            Entry(task, day, -1800)
        });

        Assert.Equal(5400, totals[task]);
    }

    [Fact]
    public void A_total_is_never_driven_below_nothing()
    {
        // A removal is a correction to a total rather than the deletion of a particular record,
        // so it is allowed to exceed what is there. What comes out is still an amount of time.
        var task = Guid.NewGuid();
        var day = new DateOnly(2026, 8, 29);

        var byTask = TimeLedger.ManualSecondsByTask(new[]
        {
            Entry(task, day, 600),
            Entry(task, day, -9000)
        });

        var byDay = TimeLedger.ManualSecondsByLocalDay(new[]
        {
            Entry(task, day, 600),
            Entry(task, day, -9000)
        });

        Assert.Equal(0, byTask[task]);
        Assert.Equal(0, byDay[day]);
    }

    [Fact]
    public void A_removal_comes_off_the_day_it_names()
    {
        var task = Guid.NewGuid();
        var monday = new DateOnly(2026, 8, 24);
        var tuesday = new DateOnly(2026, 8, 25);

        var totals = TimeLedger.ManualSecondsByLocalDay(new[]
        {
            Entry(task, monday, 3600),
            Entry(task, monday, -1200),
            Entry(task, tuesday, 1800)
        });

        Assert.Equal(2400, totals[monday]);
        Assert.Equal(1800, totals[tuesday]);
    }
}
