using System.Windows.Forms;
using Counter.App.Services;
using Xunit;

namespace Counter.Tests;

/// <summary>
/// The tray submenu that is rebuilt while the app is running.
///
/// The display list is the only menu whose contents change after start-up: choosing a screen
/// rewrites it, and so does Windows reporting that the arrangement has changed. That makes it
/// the one menu where refilling has to be safe to do a second time.
/// </summary>
public class TrayMenuTests
{
    private static ToolStripMenuItem Filled(int count)
    {
        var owner = new ToolStripMenuItem("Display");

        for (var index = 0; index < count; index++)
        {
            owner.DropDownItems.Add(new ToolStripMenuItem("Screen " + index));
        }

        return owner;
    }

    [Fact]
    public void Refilling_a_populated_submenu_does_not_throw()
    {
        // The defect this covers: disposing each item inside a foreach over the live collection.
        // Disposing a ToolStripItem detaches it from its owner, so the first Dispose invalidates
        // the enumerator and the second step throws "Collection was modified". It could only ever
        // happen on the second refill, which is exactly the one a user triggers by picking a
        // display - the first, at start-up, walks an empty collection and looks fine.
        Sta.Run(() =>
        {
            var owner = Filled(2);

            TrayIconService.ReplaceDropDown(owner, new ToolStripItem[]
            {
                new ToolStripMenuItem("Screen A"),
                new ToolStripMenuItem("Screen B"),
                new ToolStripMenuItem("Screen C")
            });

            Assert.Equal(3, owner.DropDownItems.Count);
            Assert.Equal("Screen A", owner.DropDownItems[0].Text);
            Assert.Equal("Screen C", owner.DropDownItems[2].Text);
        });
    }

    [Fact]
    public void Refilling_repeatedly_leaves_only_the_last_set()
    {
        // Unplugging and replugging a screen refills this menu each time. Entries must not
        // accumulate, and a stale entry must not survive to be clicked.
        Sta.Run(() =>
        {
            var owner = Filled(0);

            for (var round = 0; round < 5; round++)
            {
                TrayIconService.ReplaceDropDown(owner, new ToolStripItem[]
                {
                    new ToolStripMenuItem("Round " + round)
                });
            }

            Assert.Single(owner.DropDownItems);
            Assert.Equal("Round 4", owner.DropDownItems[0].Text);
        });
    }

    [Fact]
    public void The_replaced_entries_are_disposed()
    {
        // The old items own native menu handles. Detaching without disposing would leak one per
        // display change, and the app can be left running for days.
        Sta.Run(() =>
        {
            var owner = Filled(0);
            var stale = new ToolStripMenuItem("Screen 0");
            var disposed = false;
            stale.Disposed += (_, _) => disposed = true;
            owner.DropDownItems.Add(stale);

            TrayIconService.ReplaceDropDown(owner, System.Array.Empty<ToolStripItem>());

            Assert.True(disposed);
            Assert.Empty(owner.DropDownItems);
        });
    }

    [Fact]
    public void An_empty_submenu_can_be_refilled()
    {
        // The start-up path, which is the case the original code happened to survive.
        Sta.Run(() =>
        {
            var owner = Filled(0);

            TrayIconService.ReplaceDropDown(owner, new ToolStripItem[]
            {
                new ToolStripMenuItem("Primary")
            });

            Assert.Single(owner.DropDownItems);
        });
    }
}
