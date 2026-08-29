using FocusNotch.App.ViewModels;
using FocusNotch.App.Services;
using FocusNotch.App.Views;
using FocusNotch.Core.Focus;
using FocusNotch.Core.Journey;
using FocusNotch.Core.Models;
using FocusNotch.Core.Statistics;
using Xunit;

namespace FocusNotch.Tests;

/// <summary>
/// The interaction layer under load.
///
/// The point of every test here is the same: after any sequence of requests, however hostile,
/// the panel is in exactly the state that was asked for last, and nothing that happened earlier
/// can arrive afterwards and change it. The state machine is pure, so hundreds of transitions
/// cost nothing and need no window.
/// </summary>
public class InteractionStressTests
{
    private static readonly DateTime T0 = new(2026, 8, 29, 9, 0, 0, DateTimeKind.Utc);

    private static readonly PanelLevel[] Levels =
    {
        PanelLevel.Collapsed, PanelLevel.Quick, PanelLevel.Planner, PanelLevel.Statistics
    };

    private static MonitorInfo Monitor(
        int width = 1920, int height = 1080, double scale = 1.0, int left = 0, int top = 0)
        => new("\\\\.\\DISPLAY1", "Display 1", left, top, width, height,
            left, top, width, height, true, scale);

    // ================================================================ Randomised requests

    [Fact]
    public void Five_hundred_random_requests_end_in_exactly_the_state_last_asked_for()
    {
        // A fixed seed, so a failure is reproducible rather than a story about one bad run.
        var random = new Random(20260829);
        var machine = new OverlayStateMachine();
        var applied = new List<PanelTransition>();

        machine.TransitionAccepted += applied.Add;

        var expected = PanelLevel.Collapsed;

        for (var i = 0; i < 500; i++)
        {
            var target = Levels[random.Next(Levels.Length)];
            var reason = (TransitionReason)random.Next(Enum.GetValues<TransitionReason>().Length);

            var accepted = machine.RequestLevel(target, reason);

            // Idempotence: asking for the level already showing changes nothing at all.
            Assert.Equal(expected != target, accepted);
            expected = target;

            Assert.Equal(expected, machine.Level);
        }

        Assert.Equal(expected, machine.Level);

        // Every accepted transition carries a strictly increasing identifier, and only the last
        // one is current, so nothing earlier is allowed to write a final geometry.
        for (var i = 1; i < applied.Count; i++)
        {
            Assert.True(applied[i].Id > applied[i - 1].Id);
            Assert.False(machine.IsCurrent(applied[i - 1].Id));
        }

        Assert.True(machine.IsCurrent(applied[^1].Id));
    }

    [Fact]
    public void A_hundred_collapsed_to_quick_cycles_leave_no_residue()
    {
        var machine = new OverlayStateMachine();
        var transitions = 0;
        machine.TransitionAccepted += _ => transitions++;

        for (var i = 0; i < 100; i++)
        {
            machine.RequestLevel(PanelLevel.Quick, TransitionReason.HoverIntent);
            machine.RequestLevel(PanelLevel.Collapsed, TransitionReason.HoverExit);
        }

        Assert.Equal(200, transitions);
        Assert.Equal(PanelLevel.Collapsed, machine.Level);
        Assert.False(machine.IsPinned);
        Assert.Equal(0, machine.PopupDepth);
        Assert.False(machine.HasPendingOpen);
        Assert.False(machine.HasPendingClose);
    }

    [Fact]
    public void A_hundred_rapid_open_close_open_requests_settle_open()
    {
        var machine = new OverlayStateMachine();

        for (var i = 0; i < 100; i++)
        {
            machine.RequestLevel(PanelLevel.Quick, TransitionReason.Click);
            machine.RequestLevel(PanelLevel.Collapsed, TransitionReason.Click);
            machine.RequestLevel(PanelLevel.Quick, TransitionReason.Click);
        }

        Assert.Equal(PanelLevel.Quick, machine.Level);
    }

    [Fact]
    public void Opening_the_planner_during_an_unfinished_quick_transition_wins()
    {
        var machine = new OverlayStateMachine();
        var accepted = new List<PanelTransition>();
        machine.TransitionAccepted += accepted.Add;

        machine.RequestLevel(PanelLevel.Quick, TransitionReason.HoverIntent);
        machine.RequestLevel(PanelLevel.Planner, TransitionReason.Command);

        Assert.Equal(PanelLevel.Planner, machine.Level);
        Assert.False(machine.IsCurrent(accepted[0].Id));
        Assert.True(machine.IsCurrent(accepted[1].Id));
    }

    // ================================================================ Popups and hover

    [Fact]
    public void Opening_and_closing_popovers_repeatedly_never_leaves_the_depth_stuck()
    {
        var machine = new OverlayStateMachine();
        machine.RequestLevel(PanelLevel.Quick, TransitionReason.Click);

        for (var i = 0; i < 100; i++)
        {
            machine.PushPopup();
            machine.PushPopup();
            machine.PopPopup();
            machine.PopPopup();
        }

        Assert.Equal(0, machine.PopupDepth);

        // Over-popping cannot drive it negative, which would silently disable auto-collapse.
        machine.PopPopup();
        machine.PopPopup();
        Assert.Equal(0, machine.PopupDepth);
    }

    [Fact]
    public void An_open_popup_prevents_an_unintended_collapse()
    {
        var machine = new OverlayStateMachine();
        machine.RequestLevel(PanelLevel.Quick, TransitionReason.Click);
        machine.Unpin();

        machine.PushPopup();
        machine.PointerExited(T0);
        machine.Tick(T0 + OverlayStateMachine.HoverCloseDelay + TimeSpan.FromSeconds(1));

        Assert.Equal(PanelLevel.Quick, machine.Level);

        machine.PopPopup();
        machine.PointerExited(T0);
        machine.Tick(T0 + OverlayStateMachine.HoverCloseDelay + TimeSpan.FromSeconds(1));

        Assert.Equal(PanelLevel.Collapsed, machine.Level);
    }

    [Fact]
    public void Moving_between_child_controls_never_reaches_the_machine()
    {
        var machine = new OverlayStateMachine();
        machine.RequestLevel(PanelLevel.Quick, TransitionReason.HoverIntent);
        machine.Unpin();

        // The window raises enter and leave only at its own boundary, so a hundred moves between
        // buttons produce nothing here at all. Simulating that is simply: no calls.
        for (var i = 0; i < 100; i++)
        {
            machine.Tick(T0.AddMilliseconds(i * 40));
        }

        Assert.Equal(PanelLevel.Quick, machine.Level);
    }

    [Fact]
    public void A_deliberate_close_is_not_undone_by_the_pointer_still_being_inside()
    {
        var machine = new OverlayStateMachine();

        machine.PointerEntered(T0);
        machine.RequestLevel(PanelLevel.Quick, TransitionReason.HoverIntent);
        machine.RequestLevel(PanelLevel.Collapsed, TransitionReason.Click);

        Assert.True(machine.IsHoverOpenSuppressed);

        // The pointer has not moved, so hover must not immediately reopen what was just closed.
        machine.PointerEntered(T0.AddMilliseconds(10));
        Assert.False(machine.IsHoverOpenSuppressed);
    }

    [Fact]
    public void Escape_on_a_collapsed_notch_releases_whatever_was_holding_it_open()
    {
        var machine = new OverlayStateMachine();

        // A pin that outlives the panel it was holding is the failure this guards: the next
        // hover opens a panel that can then never auto-close, because nothing clears the flag.
        machine.Pin();
        Assert.True(machine.IsPinned);

        Assert.Equal(OverlayStateMachine.EscapeResult.Nothing, machine.Escape());
        Assert.False(machine.IsPinned);
    }

    [Fact]
    public void A_panel_opened_by_hover_still_closes_when_the_pointer_leaves()
    {
        var machine = new OverlayStateMachine();

        machine.PointerEntered(T0);
        Assert.True(machine.Tick(T0 + OverlayStateMachine.HoverOpenDelay));
        Assert.Equal(PanelLevel.Quick, machine.Level);
        Assert.False(machine.IsPinned);

        machine.PointerExited(T0 + TimeSpan.FromSeconds(1));
        Assert.True(machine.Tick(T0 + TimeSpan.FromSeconds(2)));
        Assert.Equal(PanelLevel.Collapsed, machine.Level);
    }

    // ================================================================ Escape ordering

    [Fact]
    public void Escape_closes_the_innermost_thing_first()
    {
        var machine = new OverlayStateMachine();

        machine.RequestLevel(PanelLevel.Statistics, TransitionReason.Command);
        machine.OpenOverlay(OverlayKind.DurationPicker);

        Assert.Equal(OverlayStateMachine.EscapeResult.ClosedOverlay, machine.Escape());
        Assert.Equal(PanelLevel.Statistics, machine.Level);

        Assert.Equal(OverlayStateMachine.EscapeResult.ClosedStatistics, machine.Escape());
        Assert.Equal(PanelLevel.Quick, machine.Level);

        Assert.Equal(OverlayStateMachine.EscapeResult.ClosedQuickView, machine.Escape());
        Assert.Equal(PanelLevel.Collapsed, machine.Level);

        Assert.Equal(OverlayStateMachine.EscapeResult.Nothing, machine.Escape());
    }

    // ================================================================ Geometry

    [Fact]
    public void Randomised_levels_always_produce_a_whole_pixel_rectangle_at_every_scale()
    {
        var random = new Random(4242);

        foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
        {
            var monitor = Monitor(scale: scale);
            var first = NotchGeometryCoordinator.ComputeBounds(
                monitor, new ShellSize(330, 42, 13), 0, 16, 16);

            for (var i = 0; i < 500; i++)
            {
                var shell = new ShellSize(
                    330 + random.NextDouble() * 270,
                    42 + random.NextDouble() * 600,
                    13 + random.NextDouble());

                var bounds = NotchGeometryCoordinator.ComputeBounds(monitor, shell, 0, 16, 16);

                // The window is a fixed-width frame pinned to the monitor centre. Only its
                // height ever moves, whatever the card inside is doing.
                Assert.Equal(first.Width, bounds.Width);
                Assert.Equal(first.X, bounds.X);
                Assert.Equal(first.Y, bounds.Y);
                Assert.True(bounds.Height > 0);
            }
        }
    }

    [Fact]
    public void The_notch_stays_centred_across_a_full_open_at_every_scale()
    {
        foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
        {
            var monitor = Monitor(scale: scale);
            var centres = new HashSet<double>();

            for (var width = 330d; width <= 600d; width += 0.5)
            {
                var bounds = NotchGeometryCoordinator.ComputeBounds(
                    monitor, new ShellSize(width, 400, 14), 0, 16, 16);

                centres.Add(bounds.CentreX);
            }

            Assert.Single(centres);
        }
    }

    // ================================================================ Through the shell

    [Fact]
    public void Completing_a_task_during_a_transition_does_not_disturb_the_target_level()
    {
        var h = new ShellHarness();
        var task = h.AddTask("Interrupt me");

        h.Shell.OpenStatistics();
        Assert.Equal(PanelLevel.Statistics, h.Shell.Panel);

        h.Shell.SelectDate(task.ScheduledDate!.Value);
        h.Shell.OpenPlanner();
        var row = h.Shell.PlannerTasks.Single(r => r.Id == task.Id);

        h.Shell.OpenStatistics();
        h.Shell.ToggleTaskCompletion(row);

        Assert.Equal(PanelLevel.Statistics, h.Shell.Panel);
    }

    [Fact]
    public void Refreshing_the_journey_and_the_statistics_never_changes_the_panel()
    {
        var h = new ShellHarness();
        h.AddTask("Something");

        foreach (var level in new[] { PanelLevel.Quick, PanelLevel.Planner, PanelLevel.Statistics })
        {
            h.Shell.Overlay.RequestLevel(level, TransitionReason.Command);

            h.Shell.RefreshJourney("stress");
            h.Shell.RefreshStatistics("stress");
            h.Shell.RefreshTaskTimes("stress");

            Assert.Equal(level, h.Shell.Panel);
        }
    }

    [Fact]
    public void A_thousand_ticks_with_a_running_timer_change_nothing_but_the_countdown()
    {
        var h = new ShellHarness();
        var task = h.AddTask("Long one", 7200);

        h.Shell.OpenPlanner();
        h.Focus.Play(task);

        var level = h.Shell.Panel;
        var cells = h.Shell.HeatmapCells;
        var readsBefore = h.Reader.Reads;
        var texts = new HashSet<string>();

        for (var i = 0; i < 1000; i++)
        {
            h.Clock.AdvanceSeconds(1);
            h.Shell.Tick();
            texts.Add(h.Shell.TimerText);
        }

        Assert.Equal(level, h.Shell.Panel);
        Assert.Same(cells, h.Shell.HeatmapCells);
        Assert.Equal(readsBefore, h.Reader.Reads);

        // The countdown really did move; it just did not drag anything else with it.
        Assert.True(texts.Count > 900);
    }

    [Fact]
    public void A_burst_of_play_and_stop_never_leaves_two_live_sessions_or_an_open_run()
    {
        var h = new ShellHarness();
        var a = h.AddTask("A");
        var b = h.AddTask("B");

        var random = new Random(7);

        for (var i = 0; i < 200; i++)
        {
            h.Clock.AdvanceSeconds(random.Next(1, 30));

            switch (random.Next(5))
            {
                case 0: h.Focus.Play(a); break;
                case 1: h.Focus.Play(b); break;
                case 2: h.Focus.Toggle(); break;
                case 3: h.Focus.Stop(SessionEndReason.StoppedByUser); break;
                default: h.Focus.CompleteIfDue(); break;
            }

            Assert.True(h.Sessions.GetActiveSessions().Count <= 1);
            Assert.True(h.Sessions.GetOpenSegments().Count <= 1);
        }

        // Whatever happened, a run is only ever open while a session is genuinely running.
        var open = h.Sessions.GetOpenSegments();
        if (open.Count == 1)
        {
            Assert.True(h.Focus.IsRunning);
            Assert.Equal(h.Focus.Current!.Id, open[0].SessionId);
        }
    }

    private sealed class ShellHarness
    {
        public ShellHarness()
        {
            Clock = new TestClock(T0);
            Tasks = new FakeTaskRepository();
            Sessions = new FakeSessionRepository();
            Manual = new FakeManualTimeRepository();
            Reader = new RepositoryActivityReader(Tasks, Sessions, Manual);
            Focus = new FocusSessionService(new FocusEngine(Clock), Sessions, Clock);

            Shell = new ShellViewModel(
                Tasks,
                Manual,
                new FakeSettingsStore(),
                Focus,
                new JourneyActivityService(Reader, Clock),
                new StatisticsService(Reader, Clock),
                Reader,
                Clock);

            Shell.Load();
        }

        public TestClock Clock { get; }

        public FakeTaskRepository Tasks { get; }

        public FakeSessionRepository Sessions { get; }

        public FakeManualTimeRepository Manual { get; }

        public RepositoryActivityReader Reader { get; }

        public FocusSessionService Focus { get; }

        public ShellViewModel Shell { get; }

        public TaskItem AddTask(string title, long seconds = 1800)
        {
            var task = new TaskItem
            {
                Title = title,
                ScheduledDate = DateOnly.FromDateTime(T0),
                EstimatedSeconds = seconds,
                CreatedAtUtc = Clock.UtcNow,
                UpdatedAtUtc = Clock.UtcNow
            };

            Tasks.Add(task);
            Shell.Load();
            return task;
        }
    }
}
