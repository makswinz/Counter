using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
/// The accent system: six families, one stored identifier, and every gradient in the interface
/// derived from it.
///
/// The rule worth protecting is not "the colours are pretty", it is that the accent and the
/// meaning of a state are different things. A running timer wears whatever the user chose; a
/// paused one is amber, a failure is red and a finished session is green, whatever the user
/// chose. Colour that follows a preference cannot also carry information.
/// </summary>
public class AccentPaletteTests
{
    /// <summary>Colours the engine has to survive that no preset would ever hand it.</summary>
    public static IEnumerable<object[]> AwkwardColours() => new[]
    {
        new object[] { "#FFF5E27A" },   // a very light yellow
        new object[] { "#FF2B3A5C" },   // a very dark navy
        new object[] { "#FFFAF6EE" },   // very nearly white
        new object[] { "#FF101418" },   // very nearly black
        new object[] { "#FF808080" },   // no chroma at all
        new object[] { "#FFB4FF00" },   // as saturated as sRGB gets
        new object[] { "#FF000000" },
        new object[] { "#FFFFFFFF" }
    };

    public static IEnumerable<object[]> EveryColour() =>
        AccentPalettes.All.Select(p => new object[] { p.Base }).Concat(AwkwardColours());

    [Fact]
    public void A_palette_is_a_name_and_one_colour()
    {
        // The whole design in one assertion: nothing is listed, so nothing can be inconsistent.
        foreach (var palette in AccentPalettes.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(palette.Id));
            Assert.False(string.IsNullOrWhiteSpace(palette.DisplayName));
            Assert.Matches("^#[0-9A-Fa-f]{8}$", palette.Base);
        }
    }

    [Fact]
    public void The_six_palettes_are_the_ones_the_design_specifies()
    {
        Assert.Equal(
            new[] { "blue", "cyan", "green", "purple", "pink", "orange" },
            AccentPalettes.All.Select(p => p.Id));

        // The exact base colours, so a well-meaning tweak to one of them is a deliberate act.
        Assert.Equal("#FF438BFF", AccentPalettes.Blue.Base);
        Assert.Equal("#FF23BDD4", AccentPalettes.Cyan.Base);
        Assert.Equal("#FF35C77D", AccentPalettes.Green.Base);
        Assert.Equal("#FF9468F2", AccentPalettes.Purple.Base);
        Assert.Equal("#FFF15C9D", AccentPalettes.Pink.Base);
        Assert.Equal("#FFFF9638", AccentPalettes.Orange.Base);
    }

    [Theory]
    [MemberData(nameof(EveryColour))]
    public void Every_generated_ramp_is_eight_digit_and_complete(string colour)
    {
        var ramp = AccentEngine.Derive(colour);

        foreach (var value in new[]
                 {
                     ramp.Highlight, ramp.Light, ramp.Base, ramp.Strong,
                     ramp.Deep, ramp.Shadow, ramp.Glow, ramp.Foreground
                 })
        {
            Assert.Matches("^#[0-9A-Fa-f]{8}$", value);
        }
    }

    [Theory]
    [MemberData(nameof(EveryColour))]
    public void Every_generated_ramp_gets_darker_and_stays_in_one_family(string colour)
    {
        // The two properties that make a gradient read as one material under one light. They are
        // asserted for the awkward colours as well as the presets, because the engine is only
        // worth anything if it holds for a colour nobody designed around.
        var ramp = AccentEngine.Derive(colour);
        var stops = new[] { ramp.Highlight, ramp.Light, ramp.Base, ramp.Strong, ramp.Deep };

        for (var index = 0; index + 1 < stops.Length; index++)
        {
            Assert.True(
                Perceptual.Luminance(stops[index]) > Perceptual.Luminance(stops[index + 1]),
                colour + " does not get darker between stop " + index + " and " + (index + 1) + ".");

            Assert.True(
                HueDistance(stops[index], stops[index + 1]) <= 40,
                colour + " changes hue between stop " + index + " and " + (index + 1) + ".");
        }

        // And the far ends stay in the family too, not only the neighbouring pairs.
        Assert.True(HueDistance(ramp.Highlight, ramp.Deep) <= 40, colour + " drifts hue across the ramp.");
        Assert.True(Perceptual.Luminance(ramp.Deep) > Perceptual.Luminance(ramp.Shadow), colour + " has no shadow.");
    }

    [Theory]
    [MemberData(nameof(EveryColour))]
    public void The_hue_of_the_chosen_colour_survives(string colour)
    {
        // Pink stays pink, green stays green. This is the promise that makes a single stored
        // identifier safe: the interface cannot introduce a colour the user did not choose.
        var source = Perceptual.FromHex(colour);
        var ramp = AccentEngine.Derive(colour);

        if (source.C < 0.02)
        {
            return; // A grey has no hue to preserve.
        }

        foreach (var stop in new[] { ramp.Highlight, ramp.Light, ramp.Base, ramp.Strong, ramp.Deep, ramp.Shadow })
        {
            var derived = Perceptual.FromHex(stop);

            if (derived.C < 0.02)
            {
                continue; // Gamut mapping can take a stop all the way to grey; that keeps no hue.
            }

            var difference = Math.Abs(Degrees(derived.H) - Degrees(source.H));
            difference = Math.Min(difference, 360 - difference);

            Assert.True(difference <= 2.0,
                colour + " drifted " + difference.ToString("F1") + " degrees at " + stop + ".");
        }
    }

    [Theory]
    [MemberData(nameof(EveryColour))]
    public void The_foreground_is_whichever_of_the_two_actually_reads(string colour)
    {
        var ramp = AccentEngine.Derive(colour);

        Assert.True(
            ramp.Foreground is AccentEngine.LightForeground or AccentEngine.DarkForeground,
            "The foreground is neither of the two inks: " + ramp.Foreground);

        var chosen = Math.Min(
            Perceptual.Contrast(ramp.Foreground, ramp.Base),
            Perceptual.Contrast(ramp.Foreground, ramp.Light));

        var other = ramp.Foreground == AccentEngine.LightForeground
            ? AccentEngine.DarkForeground
            : AccentEngine.LightForeground;

        var rejected = Math.Min(
            Perceptual.Contrast(other, ramp.Base),
            Perceptual.Contrast(other, ramp.Light));

        Assert.True(chosen >= rejected,
            colour + " was given the less readable of the two inks.");
    }

    [Fact]
    public void A_pale_accent_is_never_handed_white_text()
    {
        // The case the rule exists for. White on a light cyan or a pale yellow is not readable,
        // and no amount of it being the conventional choice makes it readable.
        foreach (var colour in new[] { "#FF23BDD4", "#FF35C77D", "#FFFF9638", "#FFF5E27A" })
        {
            Assert.Equal(AccentEngine.DarkForeground, AccentEngine.Derive(colour).Foreground);
        }

        // And a dark one is never handed near-black.
        Assert.Equal(AccentEngine.LightForeground, AccentEngine.Derive("#FF2B3A5C").Foreground);
    }

    [Fact]
    public void A_colour_outside_the_usable_band_is_brought_into_it_rather_than_refused()
    {
        // A base too pale or too dark to fit a five-stop ramp around is moved to the nearest edge
        // of the band. It is still the colour that was asked for, and it can still carry light.
        foreach (var colour in new[] { "#FFFFFFFF", "#FF000000" })
        {
            var ramp = AccentEngine.Derive(colour);
            var lightness = Perceptual.FromHex(ramp.Base).L;

            Assert.InRange(lightness, AccentEngine.MinimumBaseLightness - 0.01, AccentEngine.MaximumBaseLightness + 0.01);
        }
    }

    [Theory]
    [InlineData("blue", "blue")]
    [InlineData("ORANGE", "orange")]
    [InlineData("  pink  ", "pink")]
    [InlineData("teal", "blue")]
    [InlineData("", "blue")]
    [InlineData(null, "blue")]
    [InlineData("'; DROP TABLE Settings; --", "blue")]
    public void A_stored_identifier_round_trips_or_falls_back_safely(string? stored, string expected)
    {
        // A colour preference that cannot be read is never a reason to refuse to draw.
        Assert.Equal(expected, AccentPalettes.Parse(stored).Id);
    }

    [Fact]
    public void A_custom_colour_goes_through_exactly_the_same_engine()
    {
        var custom = AccentPalettes.Custom("#FF7A4BD3");

        Assert.StartsWith(AccentPalettes.CustomPrefix, custom.Id);
        Assert.Equal(AccentEngine.Derive("#FF7A4BD3"), custom.Ramp);

        // And it survives being stored and read back, which is what makes a picker possible.
        Assert.Equal(custom.Base, AccentPalettes.Parse(custom.Id).Base);

        // A custom identifier that is not a colour falls back rather than throwing.
        Assert.Equal(AccentPalettes.DefaultId, AccentPalettes.Parse("custom:not-a-colour").Id);
    }

    [Fact]
    public void Only_the_identifier_is_ever_stored()
    {
        var settings = new FakeSettingsStore();
        settings.Set(SettingKeys.AccentPalette, "green");

        Assert.Equal("green", AccentPalettes.Parse(settings.Get(SettingKeys.AccentPalette)).Id);
        Assert.True(AccentPalettes.IsKnown("green"));
        Assert.False(AccentPalettes.IsKnown("chartreuse"));
    }

    [Fact]
    public void Running_wears_the_accent_the_user_chose()
    {
        foreach (var accent in AccentPalettes.All)
        {
            var gradients = ThemePalette.Gradients(accent);
            var expected = GradientRamp.From(accent.Ramp);

            Assert.Equal(expected, gradients["RunningGradientBrush"]);
            Assert.Equal(expected, gradients["AccentGradientBrush"]);
            Assert.Equal(expected, gradients["AccentContourBrush"]);
        }
    }

    [Fact]
    public void Paused_warning_error_and_completed_never_follow_the_accent()
    {
        foreach (var accent in AccentPalettes.All)
        {
            var gradients = ThemePalette.Gradients(accent);

            Assert.Equal(ThemePalette.Paused, gradients["PausedGradientBrush"]);
            Assert.Equal(ThemePalette.Warning, gradients["WarningGradientBrush"]);
            Assert.Equal(ThemePalette.Danger, gradients["DangerGradientBrush"]);
            Assert.Equal(ThemePalette.Success, gradients["SuccessGradientBrush"]);
        }

        // And the exact base colours, so "amber" cannot quietly become "orange". The rest of each
        // ramp is generated, which is the point: a state family is lit exactly the way an accent
        // family is, and only its hue is fixed.
        Assert.Equal("#FFF3B643", ThemePalette.Paused.Base);
        Assert.Equal("#FFE6AA4F", ThemePalette.Warning.Base);
        Assert.Equal("#FFEF6472", ThemePalette.Danger.Base);
        Assert.Equal("#FF39C781", ThemePalette.Success.Base);
    }

    [Fact]
    public void The_state_colours_stay_put_while_the_accent_moves()
    {
        foreach (var isLight in new[] { false, true })
        {
            var baseline = ThemePalette.Solids(isLight, AccentPalettes.Blue);

            foreach (var accent in AccentPalettes.All)
            {
                var map = ThemePalette.Solids(isLight, accent);

                foreach (var key in new[]
                         {
                             "PausedBrush", "WarningBrush", "DangerBrush", "SuccessBrush",
                             "EdgePausedBrush", "EdgeUrgentBrush", "EdgeCompletedBrush",
                             "ProgressPausedBrush", "ProgressUrgentBrush", "ProgressCompletedBrush",
                             "SuccessSoftBrush"
                         })
                {
                    Assert.Equal(baseline[key], map[key]);
                }
            }
        }
    }

    [Fact]
    public void The_glass_stays_neutral_while_the_accent_moves()
    {
        // The chosen colour has to stay meaningful, and it cannot if the surfaces are wearing it
        // too. Every glass key is identical across all six families.
        var baseline = ThemePalette.Solids(false, AccentPalettes.Blue);

        foreach (var accent in AccentPalettes.All)
        {
            var map = ThemePalette.Solids(false, accent);

            foreach (var key in baseline.Keys.Where(k => k.StartsWith("Glass") || k.StartsWith("Row")))
            {
                if (key.StartsWith("RowFocused"))
                {
                    continue; // The keyboard focus ring is deliberately accent-driven.
                }

                Assert.Equal(baseline[key], map[key]);
            }
        }
    }

    [Fact]
    public void Choosing_an_accent_moves_every_accent_driven_colour()
    {
        var blue = ThemePalette.Solids(false, AccentPalettes.Blue);
        var orange = ThemePalette.Solids(false, AccentPalettes.Orange);

        foreach (var key in new[]
                 {
                     "AccentBrush", "AccentHighlightBrush", "AccentBaseBrush", "AccentStrongBrush",
                     "AccentDeepBrush", "AccentShadowBrush", "AccentGlowBrush", "AccentSoftTintBrush",
                     "SelectionFillBrush", "EdgeRunningBrush", "ProgressRunningBrush", "FocusRingBrush",
                     "ChartBarBrush", "HeatBrush1", "HeatBrush2", "HeatBrush3", "HeatBrush4",
                     "NotchGlowBrush"
                 })
        {
            Assert.NotEqual(blue[key], orange[key]);
        }
    }

    [Fact]
    public void Theme_and_accent_move_independently()
    {
        // Switching theme keeps the accent, and switching accent keeps the theme. Neither is
        // allowed to be a side effect of the other.
        var darkGreen = ThemePalette.Solids(false, AccentPalettes.Green);
        var lightGreen = ThemePalette.Solids(true, AccentPalettes.Green);
        var darkPink = ThemePalette.Solids(false, AccentPalettes.Pink);

        Assert.NotEqual(darkGreen["BackgroundBrush"], lightGreen["BackgroundBrush"]);
        Assert.Equal(darkGreen["BackgroundBrush"], darkPink["BackgroundBrush"]);

        Assert.NotEqual(darkGreen["AccentBaseBrush"], darkPink["AccentBaseBrush"]);
        Assert.Equal(darkGreen["AccentBaseBrush"], AccentPalettes.Green.Ramp.Base);
        Assert.Equal(lightGreen["AccentBaseBrush"], AccentPalettes.Green.Ramp.Base);
    }

    [Fact]
    public void A_gradient_is_five_stops_lit_from_the_upper_left()
    {
        var ramp = ThemePalette.Ramp(AccentPalettes.Blue);
        var brush = ThemeService.BuildGradient(ramp);

        Assert.True(brush.IsFrozen);
        Assert.Equal(new Point(0, 0), brush.StartPoint);
        Assert.Equal(new Point(1, 1), brush.EndPoint);
        Assert.Equal(BrushMappingMode.RelativeToBoundingBox, brush.MappingMode);

        Assert.Equal(5, brush.GradientStops.Count);
        Assert.Equal(new[] { 0.00, 0.18, 0.48, 0.76, 1.00 }, brush.GradientStops.Select(s => s.Offset));

        Assert.Equal(
            ramp.Stops.Select(ThemePalette.ToColor),
            brush.GradientStops.Select(s => s.Color));
    }

    [Fact]
    public void Every_gradient_in_the_application_shares_one_light_direction()
    {
        // Two controls lit from opposite corners is what makes an interface look assembled
        // rather than lit, so the direction and the offsets are asserted rather than trusted.
        foreach (var accent in AccentPalettes.All)
        {
            foreach (var (key, ramp) in ThemePalette.Gradients(accent))
            {
                var brush = ThemeService.BuildGradient(ramp);

                Assert.True(brush.IsFrozen, key + " is not frozen.");
                Assert.Equal(new Point(0, 0), brush.StartPoint);
                Assert.Equal(new Point(1, 1), brush.EndPoint);
                Assert.Equal(ThemeService.Offsets, brush.GradientStops.Select(s => s.Offset));
            }
        }
    }

    [Fact]
    public void The_halo_never_rises_above_twelve_percent()
    {
        foreach (var accent in AccentPalettes.All)
        {
            var halo = ThemeService.BuildHalo(accent);

            Assert.True(halo.IsFrozen);

            foreach (var stop in halo.GradientStops)
            {
                Assert.True(stop.Color.A <= 0x1F,
                    accent.Id + "'s halo reaches " + (stop.Color.A / 255d).ToString("P0") + ".");
            }

            // And it fades to nothing at its edge, so there is no visible disc boundary.
            Assert.Equal(0, halo.GradientStops[^1].Color.A);
        }
    }

    [Fact]
    public void The_ambient_reflection_is_light_rather_than_a_background()
    {
        // Ten percent at its brightest, thrown from the upper left, gone entirely by the far
        // corner. Anything stronger stops being a reflection and becomes a coloured panel.
        foreach (var accent in AccentPalettes.All)
        {
            var ambient = ThemeService.BuildAmbient(accent);

            Assert.True(ambient.IsFrozen);
            Assert.Equal(new Point(0.18, 0.06), ambient.GradientOrigin);
            Assert.True(ambient.GradientStops[0].Color.A <= 0x1A,
                accent.Id + "'s reflection starts at " + (ambient.GradientStops[0].Color.A / 255d).ToString("P0") + ".");
            Assert.Equal(0, ambient.GradientStops[^1].Color.A);
        }
    }

    [Fact]
    public void The_structural_contour_reads_in_both_themes()
    {
        // A neutral edge is what keeps the tool separated from the desktop when nothing is
        // running. On a dark panel it is white light catching the top-left; on a light one it has
        // to be a dark line instead, because white on white is not an edge at all.
        var dark = ThemeService.BuildStructuralContour(isLight: false);
        var light = ThemeService.BuildStructuralContour(isLight: true);

        foreach (var brush in new[] { dark, light })
        {
            Assert.True(brush.IsFrozen);
            Assert.Equal(new Point(0, 0), brush.StartPoint);
            Assert.Equal(new Point(1, 1), brush.EndPoint);
            Assert.True(brush.GradientStops[0].Color.A >= 0x30, "The lit corner of the contour is too faint.");
        }

        // The lit corner is white in both, and the far corner is dark in both.
        Assert.Equal(255, dark.GradientStops[0].Color.R);
        Assert.Equal(255, light.GradientStops[0].Color.R);
        Assert.Equal(0, dark.GradientStops[^1].Color.R);
        Assert.Equal(0, light.GradientStops[^1].Color.R);

        // The light theme's line has to be heavier, because it has less to work with.
        Assert.True(light.GradientStops[^1].Color.A > dark.GradientStops[^1].Color.A);
    }

    [Fact]
    public void The_glass_noise_is_monochrome_faint_and_cached()
    {
        var first = GlassNoise.Brush();
        var second = GlassNoise.Brush();

        Assert.Same(first, second);
        Assert.True(first.IsFrozen);
        Assert.Equal(TileMode.Tile, first.TileMode);
        Assert.Equal(BrushMappingMode.Absolute, first.ViewportUnits);

        var tile = (BitmapSource)first.ImageSource;
        Assert.Equal(GlassNoise.TileSize, tile.PixelWidth);
        Assert.Equal(GlassNoise.TileSize, tile.PixelHeight);

        var pixels = new byte[tile.PixelWidth * tile.PixelHeight * 4];
        tile.CopyPixels(pixels, tile.PixelWidth * 4, 0);

        for (var index = 0; index < pixels.Length; index += 4)
        {
            // Grey, so it tints nothing, and never above two percent, so it is felt rather
            // than seen.
            Assert.Equal(pixels[index], pixels[index + 1]);
            Assert.Equal(pixels[index], pixels[index + 2]);
            Assert.True(pixels[index + 3] <= 6, "The grain is stronger than two percent.");
        }
    }

    // ---------------------------------------------------------------------------------

    private static double Degrees(double radians)
    {
        var value = radians * 180 / Math.PI;
        return value < 0 ? value + 360 : value;
    }

    /// <summary>Shortest distance between two hues on the ordinary colour wheel, in degrees.</summary>
    private static double HueDistance(string a, string b)
    {
        var difference = Math.Abs(Hue(a) - Hue(b));
        return Math.Min(difference, 360 - difference);
    }

    private static double Hue(string argb)
    {
        var colour = ThemePalette.ToColor(argb);
        var r = colour.R / 255d;
        var g = colour.G / 255d;
        var b = colour.B / 255d;

        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var span = max - min;

        if (span < 1e-9)
        {
            return 0;
        }

        double hue;

        if (Math.Abs(max - r) < 1e-9)
        {
            hue = (((g - b) / span) + 6) % 6;
        }
        else if (Math.Abs(max - g) < 1e-9)
        {
            hue = ((b - r) / span) + 2;
        }
        else
        {
            hue = ((r - g) / span) + 4;
        }

        return hue * 60;
    }
}

/// <summary>
/// Statistics and Settings as two destinations.
///
/// They used to be one: the theme buttons lived along the bottom of Statistics, so changing a
/// colour meant opening a chart. These tests hold the separation - two commands, two selected
/// states, and each one closing the other.
/// </summary>
public class SettingsNavigationTests
{
    private static readonly DateTime T0 = new(2026, 8, 29, 9, 0, 0, DateTimeKind.Utc);

    private static ShellViewModel Build(out FakeSettingsStore settings)
    {
        var clock = new TestClock(T0);
        var tasks = new FakeTaskRepository();
        var sessions = new FakeSessionRepository();
        var manual = new FakeManualTimeRepository();
        var reader = new RepositoryActivityReader(tasks, sessions, manual);
        settings = new FakeSettingsStore();

        var shell = new ShellViewModel(
            tasks, manual, settings,
            new FocusSessionService(new FocusEngine(clock), sessions, clock),
            new JourneyActivityService(reader, clock),
            new StatisticsService(reader, clock),
            reader, clock);

        shell.Load();
        return shell;
    }

    [Fact]
    public void The_settings_command_opens_settings_and_nothing_else()
    {
        var shell = Build(out _);
        shell.OpenQuickView();

        shell.OpenSettings();

        Assert.Equal(PanelLevel.Settings, shell.Panel);
        Assert.True(shell.IsSettingsVisible);
        Assert.False(shell.IsStatisticsVisible);
        Assert.False(shell.IsPlannerVisible);
        Assert.Equal(NotchState.SettingsView, shell.State);
    }

    [Fact]
    public void The_statistics_command_opens_statistics_and_nothing_else()
    {
        var shell = Build(out _);
        shell.OpenQuickView();

        shell.OpenStatistics();

        Assert.Equal(PanelLevel.Statistics, shell.Panel);
        Assert.True(shell.IsStatisticsVisible);
        Assert.False(shell.IsSettingsVisible);
        Assert.Equal(NotchState.StatisticsView, shell.State);
    }

    [Fact]
    public void Opening_one_closes_the_other()
    {
        var shell = Build(out _);
        shell.OpenQuickView();

        shell.OpenStatistics();
        shell.OpenSettings();

        Assert.True(shell.IsSettingsVisible);
        Assert.False(shell.IsStatisticsVisible);

        shell.OpenStatistics();

        Assert.True(shell.IsStatisticsVisible);
        Assert.False(shell.IsSettingsVisible);
    }

    [Fact]
    public void Each_button_is_also_the_way_back_out_of_where_it_leads()
    {
        var shell = Build(out _);
        shell.OpenQuickView();

        shell.ToggleSettings();
        Assert.True(shell.IsSettingsVisible);

        shell.ToggleSettings();
        Assert.Equal(PanelLevel.Quick, shell.Panel);

        shell.ToggleStatistics();
        Assert.True(shell.IsStatisticsVisible);

        shell.ToggleStatistics();
        Assert.Equal(PanelLevel.Quick, shell.Panel);
    }

    [Theory]
    [InlineData(PanelLevel.Quick)]
    [InlineData(PanelLevel.Planner)]
    public void Leaving_returns_to_the_panel_it_was_opened_from(PanelLevel from)
    {
        var shell = Build(out _);

        if (from == PanelLevel.Planner)
        {
            shell.OpenPlanner();
        }
        else
        {
            shell.OpenQuickView();
        }

        shell.OpenSettings();
        shell.Back();
        Assert.Equal(from, shell.Panel);

        shell.OpenStatistics();
        shell.Back();
        Assert.Equal(from, shell.Panel);
    }

    [Fact]
    public void Escape_leaves_settings_without_collapsing_the_whole_panel()
    {
        var shell = Build(out _);
        shell.OpenPlanner();
        shell.OpenSettings();

        shell.Escape();

        Assert.NotEqual(PanelLevel.Settings, shell.Panel);
        Assert.NotEqual(PanelLevel.Collapsed, shell.Panel);
    }

    [Fact]
    public void The_accent_is_read_at_start_up_and_a_choice_is_reported_back()
    {
        var settings = new FakeSettingsStore();
        settings.Set(SettingKeys.AccentPalette, "pink");

        var clock = new TestClock(T0);
        var tasks = new FakeTaskRepository();
        var sessions = new FakeSessionRepository();
        var manual = new FakeManualTimeRepository();
        var reader = new RepositoryActivityReader(tasks, sessions, manual);

        var shell = new ShellViewModel(
            tasks, manual, settings,
            new FocusSessionService(new FocusEngine(clock), sessions, clock),
            new JourneyActivityService(reader, clock),
            new StatisticsService(reader, clock),
            reader, clock);

        Assert.Equal("pink", shell.AccentId);
        Assert.Equal("Pink", shell.AccentName);

        // Six families and the door to a seventh colour.
        Assert.Equal(7, shell.Accents.Count);
        Assert.Equal(6, shell.Accents.Count(a => !a.IsCustom));
        Assert.Single(shell.Accents, a => a.IsCustom);
        Assert.Single(shell.Accents, a => a.IsSelected);
        Assert.Equal("pink", shell.Accents.Single(a => a.IsSelected).Id);

        // The view model asks; the host applies and reports back. Nothing changes until it does.
        string? requested = null;
        shell.AccentRequested += id => requested = id;
        shell.Accents.Single(a => a.Id == "orange").SelectCommand.Execute(null);

        Assert.Equal("orange", requested);
        Assert.Equal("pink", shell.AccentId);

        shell.ReportAccent("orange");

        Assert.Equal("orange", shell.AccentId);
        Assert.Equal("orange", shell.Accents.Single(a => a.IsSelected).Id);
    }

    [Fact]
    public void An_unreadable_stored_accent_falls_back_without_throwing()
    {
        var settings = new FakeSettingsStore();
        settings.Set(SettingKeys.AccentPalette, "not-a-colour");

        var clock = new TestClock(T0);
        var tasks = new FakeTaskRepository();
        var sessions = new FakeSessionRepository();
        var manual = new FakeManualTimeRepository();
        var reader = new RepositoryActivityReader(tasks, sessions, manual);

        var shell = new ShellViewModel(
            tasks, manual, settings,
            new FocusSessionService(new FocusEngine(clock), sessions, clock),
            new JourneyActivityService(reader, clock),
            new StatisticsService(reader, clock),
            reader, clock);

        Assert.Equal("blue", shell.AccentId);
        Assert.Equal("blue", shell.Accents.Single(a => a.IsSelected).Id);
    }

    [Fact]
    public void The_default_duration_is_only_written_once_it_is_a_duration_the_timer_would_accept()
    {
        var shell = Build(out _);
        var written = new List<long>();
        shell.DefaultDurationRequested += seconds => written.Add(seconds);

        // Below the minimum. Somebody is still typing, and nothing is stored yet.
        shell.DefaultDuration.Load(0);
        shell.DefaultDuration.Seconds = 5;
        Assert.Empty(written);

        shell.DefaultDuration.Minutes = 45;
        Assert.Contains(45 * 60 + 5, written);

        shell.ReportDefaultDuration(45 * 60);
        Assert.Equal(45 * 60, shell.DefaultDurationSeconds);
        Assert.Equal("45m", shell.DefaultDurationLabel);
    }

    [Fact]
    public void Behaviour_toggles_ask_rather_than_assume()
    {
        var shell = Build(out _);
        bool? asked = null;
        shell.StartWithWindowsRequested += value => asked = value;

        Assert.False(shell.StartWithWindows);
        shell.ToggleStartWithWindowsCommand.Execute(null);

        // The registry is the authority. The view model reflects what the host reports, which
        // may be the value it asked for or the one that was actually achieved.
        Assert.True(asked);
        Assert.False(shell.StartWithWindows);

        shell.ReportBehaviour(alwaysOnTop: true, openOnHover: false, startWithWindows: false, soundEnabled: true);
        Assert.False(shell.StartWithWindows);
        Assert.False(shell.OpenOnHover);
    }
}

/// <summary>
/// The two controls this pass rebuilt, measured rather than looked at.
///
/// Both are about one thing: a circle whose size and centre are decided by the control and not
/// by whatever is inside it. That is testable without a screen, and it is exactly the property
/// that was missing when a two-digit date sat differently from a one-digit one.
/// </summary>
public class CircularControlTests
{
    [Fact]
    public void A_badge_is_always_an_exact_square_whatever_it_holds()

    {
        Sta.Run(() =>
        {
            foreach (var content in new object[] { "1", "8", "11", "20", "28", "31", "a very long string indeed" })
            {
                var badge = new CircularBadge { Diameter = 22, Content = content };
                badge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

                Assert.Equal(22, badge.DesiredSize.Width);
                Assert.Equal(22, badge.DesiredSize.Height);
            }
        });
    }

    [Fact]
    public void A_badge_offered_more_room_does_not_take_it()

    {
        Sta.Run(() =>
        {
            var badge = new CircularBadge { Diameter = 22, Content = "31" };
            badge.Measure(new Size(400, 400));

            Assert.Equal(22, badge.DesiredSize.Width);
            Assert.Equal(22, badge.DesiredSize.Height);
        });
    }

    [Fact]
    public void A_badge_centres_its_content_and_rounds_to_whole_pixels()

    {
        Sta.Run(() =>
        {
            var badge = new CircularBadge();

            Assert.Equal(HorizontalAlignment.Center, badge.HorizontalContentAlignment);
            Assert.Equal(VerticalAlignment.Center, badge.VerticalContentAlignment);
            Assert.True(badge.UseLayoutRounding);
            Assert.True(badge.SnapsToDevicePixels);
        });
    }

    [Fact]
    public void The_baseline_correction_is_capped_at_a_pixel()

    {
        Sta.Run(() =>
        {
            var badge = new CircularBadge();

            Assert.Equal(-0.5, badge.BaselineOffset);

            badge.BaselineOffset = 1;
            Assert.Equal(1, badge.BaselineOffset);

            Assert.Throws<ArgumentException>(() => badge.BaselineOffset = 3);
        });
    }

    [Fact]
    public void Ticking_a_task_off_cannot_change_the_size_of_anything()

    {
        Sta.Run(() =>
        {
            var check = new CompletionCheck { Width = 28, Height = 28 };

            check.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var open = check.DesiredSize;

            check.IsChecked = true;
            check.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var done = check.DesiredSize;

            Assert.Equal(open, done);
            Assert.Equal(28, done.Width);
            Assert.Equal(28, done.Height);
        });
    }

    [Fact]
    public void An_icon_measures_to_its_own_square_whatever_the_artwork_is()

    {
        Sta.Run(() =>
        {
            foreach (IconKind kind in Enum.GetValues<IconKind>())
            {
                var icon = new AppIcon { Kind = kind, IconSize = 16 };
                icon.Measure(new Size(400, 90));

                Assert.Equal(16, icon.DesiredSize.Width);
                Assert.Equal(16, icon.DesiredSize.Height);
            }
        });
    }

    [Fact]
    public void An_icon_refuses_an_optical_correction_larger_than_a_pixel()

    {
        Sta.Run(() =>
        {
            var icon = new AppIcon();

            icon.OpticalOffsetX = 1;
            Assert.Equal(1, icon.OpticalOffsetX);

            Assert.Throws<ArgumentException>(() => icon.OpticalOffsetX = 1.5);
            Assert.Throws<ArgumentException>(() => icon.OpticalOffsetY = -2);
        });
    }

    [Fact]
    public void An_icon_button_carries_its_icon_as_a_kind_rather_than_a_loose_object()

    {
        Sta.Run(() =>
        {
            var button = new IconButton { Icon = IconKind.Settings, IconVariant = IconVariant.Regular };

            Assert.Equal(IconKind.Settings, button.Icon);
            Assert.False(button.IsSelected);
            Assert.IsAssignableFrom<Button>(button);
        });
    }
}
