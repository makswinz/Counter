using FocusNotch.App.Controls;
using FocusNotch.App.Theme;
using FocusNotch.App.ViewModels;
using FocusNotch.Core.Colour;
using FocusNotch.Core.Focus;
using FocusNotch.Core.Journey;
using FocusNotch.Core.Models;
using FocusNotch.Core.Statistics;
using Xunit;

namespace FocusNotch.Tests;

/// <summary>
/// Any colour at all, and the same engine behind it.
///
/// The point of the picker is not that there are more colours. It is that a colour mixed by hand
/// is not a different kind of accent: it goes through the identical derivation, so it gets the
/// same five lit stops, the same contour, the same halo and the same measured ink as Blue does.
/// A custom colour that had to describe its own gradient would be exactly the hand-assembled
/// list the whole accent system exists to abolish.
/// </summary>
public class CustomAccentTests
{
    private static readonly DateTime T0 = new(2026, 8, 29, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>Atan2 returns a signed angle; the picker works on the whole circle.</summary>
    private static double Degrees(double radians) => ((radians * 180 / Math.PI) % 360 + 360) % 360;

    private static ShellViewModel Shell(FakeSettingsStore settings)
    {
        var clock = new TestClock(T0);
        var tasks = new FakeTaskRepository();
        var sessions = new FakeSessionRepository();
        var manual = new FakeManualTimeRepository();
        var reader = new RepositoryActivityReader(tasks, sessions, manual);

        return new ShellViewModel(
            tasks, manual, settings,
            new FocusSessionService(new FocusEngine(clock), sessions, clock),
            new JourneyActivityService(reader, clock),
            new StatisticsService(reader, clock),
            reader, clock);
    }

    // ==================================================================== reading what was typed

    [Theory]
    [InlineData("red", "#FFFF0000")]
    [InlineData("  Red  ", "#FFFF0000")]
    [InlineData("tomato", "#FFFF6347")]
    [InlineData("DarkSlateBlue", "#FF483D8B")]
    [InlineData("#E5484D", "#FFE5484D")]
    [InlineData("E5484D", "#FFE5484D")]
    [InlineData("#e5484d", "#FFE5484D")]
    [InlineData("#7B4", "#FF77BB44")]
    [InlineData("7B4", "#FF77BB44")]
    [InlineData("#FF102030", "#FF102030")]
    public void A_colour_is_read_the_way_a_person_would_write_it(string typed, string expected)
    {
        Assert.True(ColourInput.TryNormalise(typed, out var hex));
        Assert.Equal(expected, hex);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a colour")]
    [InlineData("#GGGGGG")]
    [InlineData("#12345")]
    public void Something_that_is_not_a_colour_is_refused_rather_than_thrown(string? typed)
    {
        // A half-typed colour is what a text field being typed into looks like, and it is not
        // an error. It is simply not applied.
        Assert.False(ColourInput.TryNormalise(typed, out var hex));
        Assert.Equal(string.Empty, hex);
    }

    [Fact]
    public void A_transparency_is_dropped_rather_than_carried()
    {
        // An accent is a colour, not a colour and a transparency: half an alpha on the base
        // would quietly desaturate every stop derived from it.
        Assert.True(ColourInput.TryNormalise("#8025BFA0", out var hex));
        Assert.Equal("#FF25BFA0", hex);
    }

    // ==================================================================== the same engine

    [Theory]
    [InlineData("#FFE5484D")]
    [InlineData("#FF77BB44")]
    [InlineData("#FF0093CA")]
    [InlineData("#FFFFFF00")]
    [InlineData("#FF202020")]
    public void A_hand_mixed_colour_is_not_a_second_class_accent(string colour)
    {
        var custom = AccentPalettes.Custom(colour);
        var named = AccentPalettes.Blue;

        // Same shape of ramp, produced by the same call, with nothing named anywhere in it.
        Assert.Equal(AccentEngine.Derive(custom.Base), custom.Ramp);
        Assert.Equal(GradientRamp.From(named.Ramp).Stops.Count, GradientRamp.From(custom.Ramp).Stops.Count);
        Assert.StartsWith(AccentPalettes.CustomPrefix, custom.Id);
    }

    [Fact]
    public void A_custom_identifier_round_trips_through_storage()
    {
        var stored = AccentPalettes.Custom("#FF77BB44").Id;

        Assert.Equal("custom:#FF77BB44", stored);
        Assert.Equal("#FF77BB44", AccentPalettes.Parse(stored).Base);
        Assert.False(AccentPalettes.IsKnown(stored));
    }

    // ==================================================================== the strips

    [Fact]
    public void The_brightness_strip_offers_exactly_what_the_engine_accepts()
    {
        // Not a pixel wider. A strip that reaches past the engine's band would let somebody drag
        // to a value that is silently clamped on the way in, which reads as the control sticking.
        Sta.Run(() =>
        {
            var strip = new OklchStrip { Axis = OklchAxis.Lightness };

            Assert.Equal(AccentEngine.MinimumBaseLightness, strip.Minimum);
            Assert.Equal(AccentEngine.MaximumBaseLightness, strip.Maximum);
        });
    }

    [Fact]
    public void The_colour_strip_joins_at_both_ends()
    {
        // Hue is a circle. Dragging off one end is not an error, and arrowing past 360 is not a
        // wall - it is the same colour coming back round.
        Sta.Run(() =>
        {
            var strip = new OklchStrip { Axis = OklchAxis.Hue, Hue = 350 };

            strip.Hue = 370;
            Assert.Equal(370, strip.Hue); // the property itself is raw

            var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(strip);
            var range = (System.Windows.Automation.Provider.IRangeValueProvider)
                peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.RangeValue);

            range.SetValue(400);
            Assert.Equal(40, strip.Hue, 6);

            range.SetValue(-30);
            Assert.Equal(330, strip.Hue, 6);
        });
    }

    [Fact]
    public void The_other_two_axes_clamp_rather_than_wrap()
    {
        Sta.Run(() =>
        {
            var lightness = new OklchStrip { Axis = OklchAxis.Lightness };
            var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(lightness);
            var range = (System.Windows.Automation.Provider.IRangeValueProvider)
                peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.RangeValue);

            range.SetValue(5);
            Assert.Equal(AccentEngine.MaximumBaseLightness, lightness.Lightness, 6);

            range.SetValue(-5);
            Assert.Equal(AccentEngine.MinimumBaseLightness, lightness.Lightness, 6);
        });
    }

    [Fact]
    public void A_strip_is_reachable_by_assistive_technology()
    {
        // A bare FrameworkElement publishes no peer however many properties are set on it, so
        // without this the three strips would be invisible to a screen reader and unreachable
        // by anything driving the interface.
        Sta.Run(() =>
        {
            var strip = new OklchStrip { Axis = OklchAxis.Chroma, Chroma = 0.1 };
            var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(strip);

            Assert.Equal(
                System.Windows.Automation.Peers.AutomationControlType.Slider,
                peer.GetAutomationControlType());

            var range = (System.Windows.Automation.Provider.IRangeValueProvider)
                peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.RangeValue);

            Assert.False(range.IsReadOnly);
            Assert.Equal(0.1, range.Value, 6);
            Assert.Equal(strip.Minimum, range.Minimum);
            Assert.Equal(strip.Maximum, range.Maximum);
            Assert.True(range.SmallChange > 0);
            Assert.True(range.LargeChange > range.SmallChange);
        });
    }

    // ==================================================================== the panel

    [Fact]
    public void The_seventh_swatch_is_a_door_rather_than_a_family()
    {
        var shell = Shell(new FakeSettingsStore());

        var custom = Assert.Single(shell.Accents, a => a.IsCustom);
        Assert.Equal("Custom accent", custom.AccessibleName);

        // Pressing it asks for a colour rather than a name, and opens the editor.
        string? requested = null;
        shell.AccentRequested += id => requested = id;

        Assert.False(shell.IsCustomAccentOpen);
        custom.SelectCommand.Execute(null);

        Assert.True(shell.IsCustomAccentOpen);
        Assert.StartsWith(AccentPalettes.CustomPrefix, requested);
    }

    [Fact]
    public void Moving_a_strip_shows_the_colour_without_storing_it()
    {
        // A drag is one decision expressed as several hundred mouse-moves. Only the decision
        // belongs in the database, so the preview and the request are different events.
        var shell = Shell(new FakeSettingsStore());

        var previews = new List<string>();
        var requests = new List<string>();
        shell.AccentPreviewRequested += id => previews.Add(id);
        shell.AccentRequested += id => requests.Add(id);

        shell.CustomHue = 140;
        shell.CustomChroma = 0.12;
        shell.CustomLightness = 0.55;

        Assert.Equal(3, previews.Count);
        Assert.Empty(requests);

        shell.CommitCustomAccentCommand.Execute(null);

        Assert.Single(requests);
        Assert.Equal(AccentPalettes.CustomPrefix + shell.CustomHex, requests[0]);
    }

    [Fact]
    public void The_strips_and_the_text_always_describe_the_same_colour()
    {
        // Two controls onto one value. If they can disagree, one of them is lying about what
        // the interface is currently wearing.
        var shell = Shell(new FakeSettingsStore());

        shell.CustomHue = 200;
        shell.CustomChroma = 0.09;
        shell.CustomLightness = 0.70;

        Assert.Equal("#" + shell.CustomHex.Substring(3), shell.CustomText);

        var round = Perceptual.FromHex(shell.CustomHex);
        Assert.Equal(200, Degrees(round.H), 0);
        Assert.Equal(0.70, round.L, 2);
    }

    [Fact]
    public void Typing_a_colour_moves_the_strips_to_it()
    {
        var shell = Shell(new FakeSettingsStore());

        string? requested = null;
        shell.AccentRequested += id => requested = id;

        shell.CustomText = "red";
        shell.ApplyCustomTextCommand.Execute(null);

        Assert.Equal("#FFFF0000", shell.CustomHex);
        Assert.Equal("custom:#FFFF0000", requested);

        // And the coordinates followed, so the thumbs are where the colour is.
        Assert.Equal(Degrees(Perceptual.FromHex("#FFFF0000").H), shell.CustomHue, 3);
    }

    [Fact]
    public void Typing_something_that_is_not_a_colour_puts_back_what_is_selected()
    {
        // The field must never keep a value the interface is not actually wearing.
        var shell = Shell(new FakeSettingsStore());

        shell.CustomText = "red";
        shell.ApplyCustomTextCommand.Execute(null);

        var requests = 0;
        shell.AccentRequested += _ => requests++;

        shell.CustomText = "aubergine-ish";
        shell.ApplyCustomTextCommand.Execute(null);

        Assert.Equal("#FFFF0000", shell.CustomHex);
        Assert.Equal("#FF0000", shell.CustomText);
        Assert.Equal(0, requests);
    }

    [Fact]
    public void A_stored_custom_accent_comes_back_selected_and_loaded()
    {
        var settings = new FakeSettingsStore();
        settings.Set(SettingKeys.AccentPalette, "custom:#FF77BB44");

        var shell = Shell(settings);
        shell.ReportAccent("custom:#FF77BB44");

        Assert.Equal("Custom", shell.AccentName);
        Assert.True(Assert.Single(shell.Accents, a => a.IsCustom).IsSelected);
        Assert.Equal("#FF77BB44", shell.CustomHex);
        Assert.Equal("#FF77BB44", Assert.Single(shell.Accents, a => a.IsCustom).BaseColour);

        // And no named family is left looking selected alongside it.
        Assert.Single(shell.Accents, a => a.IsSelected);
    }

    [Fact]
    public void Choosing_a_named_family_leaves_the_mixed_colour_where_it_was()
    {
        // Trying Green for an afternoon must not throw away the colour that was mixed before it.
        var shell = Shell(new FakeSettingsStore());

        shell.CustomText = "#0093CA";
        shell.ApplyCustomTextCommand.Execute(null);

        shell.ReportAccent("green");

        Assert.Equal("green", shell.AccentId);
        Assert.False(Assert.Single(shell.Accents, a => a.IsCustom).IsSelected);
        Assert.Equal("#FF0093CA", shell.CustomHex);
    }

    [Fact]
    public void Reporting_a_colour_back_does_not_ask_for_it_again()
    {
        // The host applies and reports; if reporting looked like the user having moved a strip,
        // every apply would ask for another apply.
        var shell = Shell(new FakeSettingsStore());

        var previews = 0;
        shell.AccentPreviewRequested += _ => previews++;

        shell.ReportCustomAccent("#FF0093CA");

        Assert.Equal(0, previews);
        Assert.Equal("#FF0093CA", shell.CustomHex);
    }
}
