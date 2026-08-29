using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using FocusNotch.App.Controls;
using FocusNotch.App.Theme;
using FocusNotch.Core.Models;
using Xunit;

namespace FocusNotch.Tests;

/// <summary>
/// The icon family, asserted as data.
///
/// The rule this file protects is not "the icons look nice", it is "there is exactly one family
/// and every icon in the interface comes from it". That is checkable: the geometries are
/// generated from bundled SVGs whose checksums are recorded, the views can only name an
/// <see cref="IconKind"/>, and no view is allowed to draw a glyph any other way.
/// </summary>
public class IconographyTests
{
    // =================================================================================
    // The bundled assets
    // =================================================================================

    [Fact]
    public void The_bundled_svgs_match_the_manifest()
    {
        var manifest = File.ReadAllText(Path.Combine(IconDirectory, "manifest.json"));

        Assert.Matches("\"revision\": *\"" + Regex.Escape(IconCatalog.Revision) + "\"", manifest);
        Assert.Matches("\"commit\": *\"" + Regex.Escape(IconCatalog.Commit) + "\"", manifest);
        Assert.Contains("fluentui-system-icons", manifest);

        // Every file the catalog claims to have come from is present, and every file present is
        // one the catalog uses. A stray SVG nobody draws is an icon somebody forgot to remove.
        var bundled = Directory.GetFiles(IconDirectory, "*.svg").Select(Path.GetFileName).ToHashSet();
        var used = IconCatalog.All.Select(entry => entry.Glyph.SourceFile).ToHashSet();

        Assert.Equal(used.OrderBy(f => f, StringComparer.Ordinal), bundled.OrderBy(f => f, StringComparer.Ordinal));
    }

    [Fact]
    public void The_licence_and_the_attribution_are_present()
    {
        var licence = File.ReadAllText(Path.Combine(IconDirectory, "LICENSE.txt"));
        Assert.Contains("MIT License", licence);
        Assert.Contains("Microsoft Corporation", licence);

        var notices = File.ReadAllText(Path.Combine(Root, "THIRD_PARTY_NOTICES.md"));
        Assert.Contains("Fluent UI System Icons", notices);
        Assert.Contains(IconCatalog.Revision, notices);
        Assert.Contains(IconCatalog.Commit, notices);
    }

    [Fact]
    public void Every_bundled_svg_is_official_artwork_rather_than_a_redraw()
    {
        // Fluent ships single-colour filled artwork on a square viewBox. Anything with a stroke,
        // a transform or a non-square box came from somewhere else.
        foreach (var file in Directory.GetFiles(IconDirectory, "*.svg"))
        {
            var svg = File.ReadAllText(file);
            var name = Path.GetFileName(file);

            Assert.StartsWith("ic_fluent_", name, StringComparison.Ordinal);
            Assert.Matches(@"viewBox=""0 0 (\d+) \1""", svg);
            Assert.DoesNotContain("stroke=", svg);
            Assert.DoesNotContain("transform=", svg);
        }
    }

    // =================================================================================
    // The generated geometries
    // =================================================================================

    [Fact]
    public void Every_icon_the_catalog_names_has_a_geometry()
    {
        foreach (var (kind, variant, glyph) in IconCatalog.All)
        {
            var geometry = IconCatalog.Geometry(glyph.ResourceKey);

            Assert.True(geometry is not null, kind + " " + variant + " has no geometry.");
            Assert.True(geometry!.IsFrozen, glyph.ResourceKey + " is not frozen.");
            Assert.False(geometry.IsEmpty(), glyph.ResourceKey + " is empty.");
        }
    }

    [Fact]
    public void Every_kind_resolves_including_through_the_variant_fallback()
    {
        foreach (IconKind kind in Enum.GetValues<IconKind>())
        {
            if (kind == IconKind.None)
            {
                Assert.Null(IconCatalog.Resolve(kind, IconVariant.Regular));
                continue;
            }

            // Asking for a weight that was not bundled must fall back rather than draw nothing.
            Assert.NotNull(IconCatalog.Resolve(kind, IconVariant.Regular));
            Assert.NotNull(IconCatalog.Resolve(kind, IconVariant.Filled));
        }
    }

    [Fact]
    public void Every_geometry_fits_inside_its_own_viewbox()
    {
        foreach (var (kind, variant, glyph) in IconCatalog.All)
        {
            var bounds = IconCatalog.Geometry(glyph.ResourceKey)!.Bounds;
            var label = kind + " " + variant;

            Assert.True(bounds.Left >= -0.01, label + " leaks out of the left of its viewBox.");
            Assert.True(bounds.Top >= -0.01, label + " leaks out of the top of its viewBox.");
            Assert.True(bounds.Right <= glyph.ViewboxSize + 0.01, label + " leaks out of the right.");
            Assert.True(bounds.Bottom <= glyph.ViewboxSize + 0.01, label + " leaks out of the bottom.");
        }
    }

    [Fact]
    public void Every_geometry_is_drawn_on_its_own_centre()
    {
        // This is what makes one shared square host enough. If the artwork itself were off
        // centre, no amount of layout would line the family up, and every view would end up
        // carrying its own margin - which is exactly the state this pass was asked to fix.
        foreach (var (kind, variant, glyph) in IconCatalog.All)
        {
            var bounds = IconCatalog.Geometry(glyph.ResourceKey)!.Bounds;
            var middle = glyph.ViewboxSize / 2;

            var dx = Math.Abs(bounds.Left + (bounds.Width / 2) - middle);
            var dy = Math.Abs(bounds.Top + (bounds.Height / 2) - middle);

            // Two units on a twenty-unit grid is a tenth of the icon. The play triangle is the
            // only one that comes near it, and deliberately so.
            Assert.True(dx <= 2, kind + " " + variant + " is " + dx.ToString("N2") + " units off centre horizontally.");
            Assert.True(dy <= 2, kind + " " + variant + " is " + dy.ToString("N2") + " units off centre vertically.");
        }
    }

    [Fact]
    public void Optical_corrections_are_centralised_and_capped()
    {
        foreach (IconKind kind in Enum.GetValues<IconKind>())
        {
            foreach (IconVariant variant in Enum.GetValues<IconVariant>())
            {
                var offset = IconCatalog.OpticalOffset(kind, variant);

                Assert.True(Math.Abs(offset.X) <= IconCatalog.MaximumOpticalOffset,
                    kind + " " + variant + " has an oversized horizontal correction.");
                Assert.True(Math.Abs(offset.Y) <= IconCatalog.MaximumOpticalOffset,
                    kind + " " + variant + " has an oversized vertical correction.");
            }
        }

        // The triangle is the one shape whose optical centre is not its bounding box.
        Assert.Equal(new Vector(0.5, 0), IconCatalog.OpticalOffset(IconKind.Play, IconVariant.Filled));
        Assert.Equal(default, IconCatalog.OpticalOffset(IconKind.Settings, IconVariant.Regular));
    }

    // =================================================================================
    // The mapping the brief asks for
    // =================================================================================

    [Theory]
    [InlineData(IconKind.Settings, IconVariant.Regular, "ic_fluent_settings_20_regular.svg")]
    [InlineData(IconKind.DataBarVertical, IconVariant.Regular, "ic_fluent_data_bar_vertical_20_regular.svg")]
    [InlineData(IconKind.ClipboardTaskList, IconVariant.Regular, "ic_fluent_clipboard_task_list_ltr_20_regular.svg")]
    [InlineData(IconKind.Play, IconVariant.Filled, "ic_fluent_play_20_filled.svg")]
    [InlineData(IconKind.Pause, IconVariant.Filled, "ic_fluent_pause_20_filled.svg")]
    [InlineData(IconKind.Stop, IconVariant.Filled, "ic_fluent_stop_20_filled.svg")]
    [InlineData(IconKind.Checkmark, IconVariant.Regular, "ic_fluent_checkmark_12_regular.svg")]
    [InlineData(IconKind.Calendar, IconVariant.Regular, "ic_fluent_calendar_ltr_20_regular.svg")]
    [InlineData(IconKind.CalendarToday, IconVariant.Regular, "ic_fluent_calendar_today_20_regular.svg")]
    [InlineData(IconKind.CalendarEmpty, IconVariant.Regular, "ic_fluent_calendar_empty_20_regular.svg")]
    [InlineData(IconKind.ChevronLeft, IconVariant.Regular, "ic_fluent_chevron_left_20_regular.svg")]
    [InlineData(IconKind.ChevronRight, IconVariant.Regular, "ic_fluent_chevron_right_20_regular.svg")]
    [InlineData(IconKind.ChevronDown, IconVariant.Regular, "ic_fluent_chevron_down_20_regular.svg")]
    [InlineData(IconKind.ChevronUp, IconVariant.Regular, "ic_fluent_chevron_up_20_regular.svg")]
    [InlineData(IconKind.Add, IconVariant.Regular, "ic_fluent_add_20_regular.svg")]
    [InlineData(IconKind.Edit, IconVariant.Regular, "ic_fluent_edit_20_regular.svg")]
    [InlineData(IconKind.Delete, IconVariant.Regular, "ic_fluent_delete_20_regular.svg")]
    [InlineData(IconKind.Dismiss, IconVariant.Regular, "ic_fluent_dismiss_20_regular.svg")]
    [InlineData(IconKind.MoreHorizontal, IconVariant.Regular, "ic_fluent_more_horizontal_20_regular.svg")]
    [InlineData(IconKind.PaintBrush, IconVariant.Regular, "ic_fluent_paint_brush_20_regular.svg")]
    [InlineData(IconKind.Desktop, IconVariant.Regular, "ic_fluent_desktop_20_regular.svg")]
    [InlineData(IconKind.Speaker2, IconVariant.Regular, "ic_fluent_speaker_2_20_regular.svg")]
    [InlineData(IconKind.SpeakerOff, IconVariant.Regular, "ic_fluent_speaker_off_20_regular.svg")]
    [InlineData(IconKind.Timer, IconVariant.Regular, "ic_fluent_timer_20_regular.svg")]
    [InlineData(IconKind.Fire, IconVariant.Regular, "ic_fluent_fire_20_regular.svg")]
    [InlineData(IconKind.CheckmarkCircle, IconVariant.Filled, "ic_fluent_checkmark_circle_20_filled.svg")]
    [InlineData(IconKind.Warning, IconVariant.Filled, "ic_fluent_warning_20_filled.svg")]
    [InlineData(IconKind.ErrorCircle, IconVariant.Filled, "ic_fluent_error_circle_20_filled.svg")]
    [InlineData(IconKind.WeatherSunny, IconVariant.Regular, "ic_fluent_weather_sunny_20_regular.svg")]
    [InlineData(IconKind.WeatherMoon, IconVariant.Regular, "ic_fluent_weather_moon_20_regular.svg")]
    [InlineData(IconKind.Clock, IconVariant.Regular, "ic_fluent_clock_20_regular.svg")]
    public void The_required_mapping_points_at_the_official_asset(
        IconKind kind, IconVariant variant, string file)
    {
        var glyph = IconCatalog.Resolve(kind, variant);

        Assert.NotNull(glyph);
        Assert.Equal(file, glyph!.Value.SourceFile);
    }

    // =================================================================================
    // The views
    // =================================================================================

    [Fact]
    public void No_view_draws_a_glyph_any_other_way()
    {
        foreach (var file in ThemeAndViewFiles())
        {
            var text = File.ReadAllText(file);
            var name = Path.GetFileName(file);

            // A Path in a view is a hand-drawn icon by another name, and a text glyph is the
            // symbol-font approach this pass exists to remove.
            Assert.False(text.Contains("<Path "), name + " draws a Path.");
            Assert.False(Regex.IsMatch(text, @"FontFamily=""[^""]*(Segoe MDL2|Segoe Fluent Icons|Wingdings|Marlett)"),
                name + " uses a symbol font.");
            Assert.False(text.Contains("ctl:FluentIcon"), name + " still uses the old icon control.");
            Assert.False(text.Contains("Tag=\"{StaticResource Icon"), name + " puts a geometry in a Tag.");
        }
    }

    [Fact]
    public void No_view_uses_a_text_character_as_an_icon()
    {
        // Arrows, multiplication signs, bullets, gears, triangles and every emoji. If a glyph is
        // wanted, it comes from the family; if a character is wanted, it is words.
        var forbidden = new[]
        {
            '×', '•', '–', '—',   // multiplication sign, bullet, en and em dash
            '←', '↑', '→', '↓',   // arrows
            '▶', '◀', '■', '●',   // triangles, square, circle
            '✖', '✓', '✔', '⚙',   // cross, ticks, gear
            '⏸', '⏹', '➕', '➖'    // pause, stop, plus, minus
        };

        foreach (var file in ThemeAndViewFiles())
        {
            var text = File.ReadAllText(file);
            var name = Path.GetFileName(file);

            foreach (var character in forbidden)
            {
                Assert.False(text.Contains(character),
                    name + " uses U+" + ((int)character).ToString("X4") + " as an icon.");
            }

            foreach (var rune in text.EnumerateRunes())
            {
                // Anything outside the basic multilingual plane in a view is an emoji.
                Assert.True(rune.Value <= 0xFFFF,
                    name + " contains an emoji: U+" + rune.Value.ToString("X"));
            }
        }
    }

    [Fact]
    public void Every_icon_only_button_carries_a_tooltip_and_a_name()
    {
        var view = File.ReadAllText(ViewFile);
        var problems = new List<string>();

        foreach (Match match in Regex.Matches(view, @"<ctl:Icon(?:Toggle)?Button\b(?:[^<>]|\n)*?/>"))
        {
            var element = match.Value;

            if (!element.Contains("ToolTip"))
            {
                problems.Add("an icon button with no tooltip: " + Summarise(element));
            }

            if (!element.Contains("AutomationProperties.Name"))
            {
                problems.Add("an icon button with no accessible name: " + Summarise(element));
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Statistics_and_settings_are_two_buttons_that_share_nothing()
    {
        var view = File.ReadAllText(ViewFile);
        var statistics = new List<string>();
        var settings = new List<string>();

        foreach (Match match in Regex.Matches(view, @"<ctl:IconButton\b(?:[^<>]|\n)*?/>"))
        {
            var element = match.Value;

            if (element.Contains("ToggleStatisticsCommand"))
            {
                statistics.Add(element);
            }
            else if (element.Contains("ToggleSettingsCommand"))
            {
                settings.Add(element);
            }
        }

        // One of each in the notch header, and one of each in the shared panel header.
        Assert.Equal(2, statistics.Count);
        Assert.Equal(2, settings.Count);

        foreach (var element in statistics)
        {
            Assert.Contains("Icon=\"DataBarVertical\"", element);
            Assert.Contains("ToolTip=\"Statistics\"", element);
            Assert.Contains("AutomationProperties.Name=\"Statistics\"", element);
            Assert.Contains("IsSelected=\"{Binding IsStatisticsVisible", element);
            Assert.DoesNotContain("ToggleSettingsCommand", element);
        }

        foreach (var element in settings)
        {
            Assert.Contains("Icon=\"Settings\"", element);
            Assert.Contains("ToolTip=\"Settings\"", element);
            Assert.Contains("AutomationProperties.Name=\"Settings\"", element);
            Assert.Contains("IsSelected=\"{Binding IsSettingsVisible", element);
            Assert.DoesNotContain("ToggleStatisticsCommand", element);
        }
    }

    [Fact]
    public void No_view_pads_an_icon_by_hand()
    {
        // Margins on an icon are how a family drifts out of alignment one view at a time. The
        // gap between an icon and the text beside it is spacing, not centring, so a right-only
        // margin is allowed; anything that moves the glyph inside its own host is not.
        var problems = new List<string>();

        foreach (var file in ThemeAndViewFiles())
        {
            foreach (Match match in Regex.Matches(File.ReadAllText(file), @"<ctl:AppIcon\b(?:[^<>]|\n)*?/>"))
            {
                var margin = Regex.Match(match.Value, @"Margin=""([^""]+)""");
                if (!margin.Success)
                {
                    continue;
                }

                var value = margin.Groups[1].Value;

                // The spacing scale is allowed by name. Those resources are right-only gaps
                // between an icon and the text beside it, which is spacing rather than centring.
                if (value.StartsWith("{StaticResource GapRight", StringComparison.Ordinal))
                {
                    continue;
                }

                if (value.StartsWith("{", StringComparison.Ordinal))
                {
                    problems.Add(Path.GetFileName(file) + ": " + value);
                    continue;
                }

                var parts = value.Split(',');
                var left = double.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
                var top = parts.Length > 1 ? double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : left;
                var bottom = parts.Length > 3 ? double.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture) : top;

                if (left != 0 || top != 0 || bottom != 0)
                {
                    problems.Add(Path.GetFileName(file) + ": " + margin.Value);
                }
            }
        }

        Assert.True(problems.Count == 0,
            "An icon is being nudged by a margin instead of by the catalog: " +
            string.Join("; ", problems));
    }

    [Fact]
    public void No_deprecated_icon_key_survives_anywhere()
    {
        // The hand-drawn set these replaced. A leftover reference would resolve to nothing and
        // render an empty button rather than failing.
        var retired = new[]
        {
            "IconTimer", "IconPlayFilled", "IconPauseFilled", "IconStopFilled", "IconChevronDown",
            "IconChevronUp", "IconChevronLeft", "IconChevronRight", "IconList", "IconCheckmark",
            "IconCheckmarkCircle", "IconCircleFilled", "IconCalendar", "IconCalendarToday",
            "IconCalendarEmpty", "IconAdd", "IconEdit", "IconDelete", "IconDismiss",
            "IconDismissCircle", "IconMoreHorizontal", "IconSettings", "IconDesktop",
            "IconSpeaker2", "IconSpeakerOff", "IconPin", "IconPinFilled", "IconFlameFilled",
            "IconSparkleFilled", "IconNote", "IconArrowReset", "IconWarning", "IconErrorCircle",
            "IconChart", "IconClockAdd"
        };

        foreach (var file in ThemeAndViewFiles().Where(f => !f.EndsWith("Icons.xaml")))
        {
            var text = File.ReadAllText(file);

            foreach (var key in retired)
            {
                Assert.False(
                    Regex.IsMatch(text, @"(?:Static|Dynamic)Resource\s+" + key + @"\b"),
                    Path.GetFileName(file) + " still references the retired key " + key + ".");
            }
        }
    }

    // =================================================================================
    // Helpers
    // =================================================================================

    private static string Summarise(string element)
    {
        var icon = Regex.Match(element, @"Icon=""(\w+)""");
        var command = Regex.Match(element, @"Command=""\{Binding (\w+)");
        return (icon.Success ? icon.Groups[1].Value : "?") + " / " +
               (command.Success ? command.Groups[1].Value : "?");
    }

    private static string Root => TestPaths.RepositoryRoot;

    private static string IconDirectory => Path.Combine(Root, "Assets", "Icons", "Fluent");

    private static string ViewFile =>
        Path.Combine(Root, "src", "FocusNotch.App", "Views", "NotchWindow.xaml");

    private static IEnumerable<string> ThemeAndViewFiles()
    {
        foreach (var file in Directory.GetFiles(
                     Path.Combine(Root, "src", "FocusNotch.App", "Theme"), "*.xaml"))
        {
            yield return file;
        }

        foreach (var file in Directory.GetFiles(
                     Path.Combine(Root, "src", "FocusNotch.App", "Views"), "*.xaml"))
        {
            yield return file;
        }
    }
}

/// <summary>Finds the repository from the test binaries, for the tests that read source files.</summary>
public static class TestPaths
{
    public static string RepositoryRoot { get; } = Locate();

    private static string Locate()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FocusNotch.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
