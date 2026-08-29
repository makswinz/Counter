using Counter.App.ViewModels;
using Xunit;

namespace Counter.Tests;

/// <summary>
/// The panel state machine, driven by an explicit instant rather than a real timer, so hover
/// hysteresis is asserted exactly instead of by sleeping.
/// </summary>
public class OverlayStateMachineTests
{
    private static readonly DateTime T0 = new(2026, 8, 29, 10, 0, 0, DateTimeKind.Utc);

    private static OverlayStateMachine Machine() => new();

    private static List<PanelTransition> Record(OverlayStateMachine machine)
    {
        var log = new List<PanelTransition>();
        machine.TransitionAccepted += log.Add;
        return log;
    }

    // ---------------------------------------------------------------- Idempotence

    [Fact]
    public void Requesting_the_current_state_is_a_no_op()
    {
        var machine = Machine();
        var log = Record(machine);

        Assert.False(machine.RequestLevel(PanelLevel.Collapsed, TransitionReason.Command));
        Assert.Empty(log);

        Assert.True(machine.RequestLevel(PanelLevel.Quick, TransitionReason.Command));
        Assert.False(machine.RequestLevel(PanelLevel.Quick, TransitionReason.Command));

        Assert.Single(log);
    }

    [Fact]
    public void A_newer_request_supersedes_the_previous_transition()
    {
        var machine = Machine();
        var log = Record(machine);

        machine.RequestLevel(PanelLevel.Quick, TransitionReason.Command);
        var first = log[0].Id;

        machine.RequestLevel(PanelLevel.Planner, TransitionReason.Command);
        var second = log[1].Id;

        Assert.True(second > first);
        Assert.True(machine.IsCurrent(second));
        Assert.False(machine.IsCurrent(first));
    }

    [Fact]
    public void A_stale_transition_identifier_is_never_current_again()
    {
        var machine = Machine();
        var log = Record(machine);

        machine.RequestLevel(PanelLevel.Quick, TransitionReason.Command);
        machine.RequestLevel(PanelLevel.Planner, TransitionReason.Command);
        machine.RequestLevel(PanelLevel.Quick, TransitionReason.Command);

        Assert.False(machine.IsCurrent(log[0].Id));
        Assert.False(machine.IsCurrent(log[1].Id));
        Assert.True(machine.IsCurrent(log[2].Id));
    }

    [Fact]
    public void Rapid_open_close_open_ends_in_the_final_requested_state()
    {
        var machine = Machine();

        machine.RequestLevel(PanelLevel.Quick, TransitionReason.Command);
        machine.RequestLevel(PanelLevel.Collapsed, TransitionReason.Command);
        machine.RequestLevel(PanelLevel.Quick, TransitionReason.Command);
        machine.RequestLevel(PanelLevel.Planner, TransitionReason.Command);

        Assert.Equal(PanelLevel.Planner, machine.Level);
    }

    // ---------------------------------------------------------------- Hover hysteresis

    [Fact]
    public void Hover_opens_only_after_the_open_delay()
    {
        var machine = Machine();
        machine.PointerEntered(T0);

        Assert.False(machine.Tick(T0 + TimeSpan.FromMilliseconds(100)));
        Assert.Equal(PanelLevel.Collapsed, machine.Level);

        Assert.True(machine.Tick(T0 + OverlayStateMachine.HoverOpenDelay));
        Assert.Equal(PanelLevel.Quick, machine.Level);
    }

    [Fact]
    public void Leaving_before_the_open_delay_cancels_the_pending_open()
    {
        var machine = Machine();
        machine.PointerEntered(T0);
        machine.PointerExited(T0 + TimeSpan.FromMilliseconds(80));

        Assert.False(machine.HasPendingOpen);
        Assert.False(machine.Tick(T0 + TimeSpan.FromSeconds(5)));
        Assert.Equal(PanelLevel.Collapsed, machine.Level);
    }

    [Fact]
    public void Hover_re_entry_cancels_a_pending_collapse()
    {
        var machine = Machine();
        machine.RequestLevel(PanelLevel.Quick, TransitionReason.Command);

        machine.PointerExited(T0);
        Assert.True(machine.HasPendingClose);

        machine.PointerEntered(T0 + TimeSpan.FromMilliseconds(100));
        Assert.False(machine.HasPendingClose);

        machine.Tick(T0 + TimeSpan.FromSeconds(5));
        Assert.Equal(PanelLevel.Quick, machine.Level);
    }

    [Fact]
    public void Pointer_exit_collapses_only_after_the_close_delay()
    {
        var machine = Machine();
        machine.RequestLevel(PanelLevel.Quick, TransitionReason.HoverIntent);
        machine.PointerExited(T0);

        Assert.False(machine.Tick(T0 + TimeSpan.FromMilliseconds(200)));
        Assert.Equal(PanelLevel.Quick, machine.Level);

        Assert.True(machine.Tick(T0 + OverlayStateMachine.HoverCloseDelay));
        Assert.Equal(PanelLevel.Collapsed, machine.Level);
    }

    [Fact]
    public void Moving_between_child_controls_does_not_collapse()
    {
        // Child controls never call the machine at all: only the root boundary does. Entering
        // once and never leaving is exactly what a pointer crossing between children looks like.
        var machine = Machine();
        machine.PointerEntered(T0);
        machine.Tick(T0 + OverlayStateMachine.HoverOpenDelay);

        Assert.Equal(PanelLevel.Quick, machine.Level);

        for (var i = 1; i <= 20; i++)
        {
            machine.Tick(T0 + TimeSpan.FromSeconds(i));
        }

        Assert.Equal(PanelLevel.Quick, machine.Level);
    }

    [Fact]
    public void A_pinned_panel_does_not_collapse_on_pointer_exit()
    {
        var machine = Machine();
        machine.RequestLevel(PanelLevel.Quick, TransitionReason.HoverIntent);
        machine.Pin();

        machine.PointerExited(T0);
        Assert.False(machine.HasPendingClose);

        machine.Tick(T0 + TimeSpan.FromSeconds(5));
        Assert.Equal(PanelLevel.Quick, machine.Level);
    }

    [Fact]
    public void An_open_popup_prevents_collapse()
    {
        var machine = Machine();
        machine.RequestLevel(PanelLevel.Quick, TransitionReason.HoverIntent);
        machine.PushPopup();

        machine.PointerExited(T0);
        machine.Tick(T0 + TimeSpan.FromSeconds(5));
        Assert.Equal(PanelLevel.Quick, machine.Level);

        // Closing the last popup restores normal collapse behaviour.
        machine.PopPopup();
        machine.PointerExited(T0 + TimeSpan.FromSeconds(5));
        Assert.True(machine.Tick(T0 + TimeSpan.FromSeconds(6)));
        Assert.Equal(PanelLevel.Collapsed, machine.Level);
    }

    [Fact]
    public void Nested_popups_are_counted_not_flagged()
    {
        var machine = Machine();
        machine.RequestLevel(PanelLevel.Quick, TransitionReason.HoverIntent);

        machine.PushPopup();
        machine.PushPopup();
        machine.PopPopup();

        Assert.Equal(1, machine.PopupDepth);
        Assert.True(machine.BlocksAutoCollapse);

        machine.PopPopup();
        Assert.Equal(0, machine.PopupDepth);
        Assert.False(machine.BlocksAutoCollapse);
    }

    [Fact]
    public void An_open_overlay_prevents_collapse()
    {
        var machine = Machine();
        machine.RequestLevel(PanelLevel.Quick, TransitionReason.HoverIntent);
        machine.OpenOverlay(OverlayKind.DurationPicker);

        machine.PointerExited(T0);
        machine.Tick(T0 + TimeSpan.FromSeconds(5));

        Assert.Equal(PanelLevel.Quick, machine.Level);
        Assert.Equal(OverlayKind.DurationPicker, machine.Overlay);
    }

    [Fact]
    public void An_open_editor_or_transient_message_prevents_collapse()
    {
        var machine = Machine();
        machine.RequestLevel(PanelLevel.Quick, TransitionReason.HoverIntent);
        machine.HasOpenEditor = true;

        machine.PointerExited(T0);
        machine.Tick(T0 + TimeSpan.FromSeconds(5));
        Assert.Equal(PanelLevel.Quick, machine.Level);

        machine.HasOpenEditor = false;
        machine.HasTransientMessage = true;

        machine.PointerExited(T0 + TimeSpan.FromSeconds(5));
        machine.Tick(T0 + TimeSpan.FromSeconds(10));
        Assert.Equal(PanelLevel.Quick, machine.Level);
    }

    [Fact]
    public void A_deliberate_close_holds_hover_opening_off_until_the_pointer_leaves()
    {
        // This is the open-close flutter: collapsing shrinks the window out from under the
        // pointer, and without the hold the hover intent immediately re-opens it.
        var machine = Machine();
        machine.PointerEntered(T0);
        machine.Tick(T0 + OverlayStateMachine.HoverOpenDelay);
        Assert.Equal(PanelLevel.Quick, machine.Level);

        machine.RequestLevel(PanelLevel.Collapsed, TransitionReason.Click);
        Assert.True(machine.IsHoverOpenSuppressed);

        // The pointer is still inside, so hover intent would normally fire again.
        machine.PointerEntered(T0 + TimeSpan.FromMilliseconds(300));
        machine.Tick(T0 + TimeSpan.FromSeconds(2));

        // PointerEntered clears the hold, which is correct: a genuine new entry should open.
        // What must not happen is re-opening without any new entry at all.
        var quiet = Machine();
        quiet.PointerEntered(T0);
        quiet.Tick(T0 + OverlayStateMachine.HoverOpenDelay);
        quiet.RequestLevel(PanelLevel.Collapsed, TransitionReason.Click);

        Assert.False(quiet.Tick(T0 + TimeSpan.FromSeconds(3)));
        Assert.Equal(PanelLevel.Collapsed, quiet.Level);
    }

    [Fact]
    public void A_hover_driven_collapse_does_not_hold_hover_opening_off()
    {
        var machine = Machine();
        machine.RequestLevel(PanelLevel.Quick, TransitionReason.HoverIntent);
        machine.PointerExited(T0);
        machine.Tick(T0 + OverlayStateMachine.HoverCloseDelay);

        Assert.Equal(PanelLevel.Collapsed, machine.Level);
        Assert.False(machine.IsHoverOpenSuppressed);
    }

    [Fact]
    public void Hover_opening_can_be_turned_off_entirely()
    {
        var machine = Machine();
        machine.OpenOnHover = false;

        machine.PointerEntered(T0);
        Assert.False(machine.HasPendingOpen);

        machine.Tick(T0 + TimeSpan.FromSeconds(5));
        Assert.Equal(PanelLevel.Collapsed, machine.Level);
    }

    // ---------------------------------------------------------------- Deactivation

    [Fact]
    public void Deactivation_collapses_an_idle_panel()
    {
        var machine = Machine();
        machine.RequestLevel(PanelLevel.Quick, TransitionReason.HoverIntent);

        Assert.True(machine.Deactivated());
        Assert.Equal(PanelLevel.Collapsed, machine.Level);
    }

    [Fact]
    public void Deactivation_leaves_a_pinned_or_overlaid_panel_alone()
    {
        var pinned = Machine();
        pinned.RequestLevel(PanelLevel.Quick, TransitionReason.HoverIntent);
        pinned.Pin();
        Assert.False(pinned.Deactivated());
        Assert.Equal(PanelLevel.Quick, pinned.Level);

        var overlaid = Machine();
        overlaid.RequestLevel(PanelLevel.Planner, TransitionReason.Command);
        overlaid.Unpin();
        overlaid.OpenOverlay(OverlayKind.SwitchConfirmation);
        Assert.False(overlaid.Deactivated());
        Assert.Equal(PanelLevel.Planner, overlaid.Level);

        var withPopup = Machine();
        withPopup.RequestLevel(PanelLevel.Quick, TransitionReason.HoverIntent);
        withPopup.PushPopup();
        Assert.False(withPopup.Deactivated());
        Assert.Equal(PanelLevel.Quick, withPopup.Level);
    }

    [Fact]
    public void Deactivation_while_the_pointer_is_inside_changes_nothing()
    {
        var machine = Machine();
        machine.RequestLevel(PanelLevel.Quick, TransitionReason.HoverIntent);
        machine.PointerEntered(T0);

        Assert.False(machine.Deactivated());
        Assert.Equal(PanelLevel.Quick, machine.Level);
    }

    // ---------------------------------------------------------------- Escape

    [Fact]
    public void Escape_closes_the_overlay_first_then_the_planner_then_the_quick_view()
    {
        var machine = Machine();
        machine.RequestLevel(PanelLevel.Planner, TransitionReason.Command);
        machine.OpenOverlay(OverlayKind.DurationPicker);

        Assert.Equal(OverlayStateMachine.EscapeResult.ClosedOverlay, machine.Escape());
        Assert.Equal(OverlayKind.None, machine.Overlay);
        Assert.Equal(PanelLevel.Planner, machine.Level);

        Assert.Equal(OverlayStateMachine.EscapeResult.ClosedPlanner, machine.Escape());
        Assert.Equal(PanelLevel.Quick, machine.Level);

        Assert.Equal(OverlayStateMachine.EscapeResult.ClosedQuickView, machine.Escape());
        Assert.Equal(PanelLevel.Collapsed, machine.Level);

        Assert.Equal(OverlayStateMachine.EscapeResult.Nothing, machine.Escape());
    }

    [Fact]
    public void Opening_an_overlay_keeps_the_panel_level_explicit_underneath()
    {
        var machine = Machine();
        machine.RequestLevel(PanelLevel.Planner, TransitionReason.Command);
        machine.OpenOverlay(OverlayKind.TaskEditor);

        Assert.Equal(PanelLevel.Planner, machine.Level);
        Assert.Equal(OverlayKind.TaskEditor, machine.Overlay);

        machine.CloseOverlay();
        Assert.Equal(PanelLevel.Planner, machine.Level);
    }

    [Fact]
    public void Opening_an_overlay_cancels_any_pending_hover_intent()
    {
        var machine = Machine();
        machine.RequestLevel(PanelLevel.Quick, TransitionReason.HoverIntent);
        machine.PointerExited(T0);
        Assert.True(machine.HasPendingClose);

        machine.OpenOverlay(OverlayKind.DeleteConfirmation);
        Assert.False(machine.HasPendingClose);
    }

    [Fact]
    public void Collapsing_clears_the_pin()
    {
        var machine = Machine();
        machine.RequestLevel(PanelLevel.Quick, TransitionReason.Command);
        machine.Pin();

        machine.RequestLevel(PanelLevel.Collapsed, TransitionReason.Command);

        Assert.False(machine.IsPinned);
        Assert.False(machine.BlocksAutoCollapse);
    }

    [Fact]
    public void A_refit_mints_a_new_identifier_without_changing_the_level()
    {
        var machine = Machine();
        machine.RequestLevel(PanelLevel.Quick, TransitionReason.Command);
        var before = machine.CurrentTransitionId;

        var refit = machine.RequestRefit(TransitionReason.ContentChanged);

        Assert.Equal(PanelLevel.Quick, machine.Level);
        Assert.Equal(PanelLevel.Quick, refit.From);
        Assert.Equal(PanelLevel.Quick, refit.To);
        Assert.True(refit.Id > before);
        Assert.True(machine.IsCurrent(refit.Id));
    }
}
