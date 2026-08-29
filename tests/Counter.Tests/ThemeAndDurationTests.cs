using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Counter.App.Theme;
using Counter.App.ViewModels;
using Counter.Core.Focus;
using Counter.Core.Models;
using Counter.Core.Validation;
using Xunit;

namespace Counter.Tests;

/// <summary>
/// The two themes and the six accents, asserted as data.
///
/// A theme rots when a key exists in one palette and not the other: something goes black in
/// light mode or invisible in dark mode, and nobody notices until they switch. An accent rots
/// the same way, one combination at a time. Comparing the key sets across every theme and every
/// palette makes both a build failure instead.
/// </summary>
public class ThemeTests
{
    /// <summary>Every theme and accent combination the application can actually be in.</summary>
    public static IEnumerable<object[]> Combinations()
    {
        foreach (var accent in AccentPalettes.All)
        {
            yield return new object[] { false, accent.Id };
            yield return new object[] { true, accent.Id };
        }
    }

    [Fact]
    public void Both_themes_define_exactly_the_same_keys()
    {
        var dark = ThemePalette.Dark.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        var light = ThemePalette.Light.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.Equal(dark, light);
    }

    [Fact]
    public void Every_theme_and_accent_combination_defines_exactly_the_same_keys()
    {
        // Twelve combinations, one key set. Anything the accent map adds for Blue and forgets
        // for Orange would leave one palette with a colour the others cannot produce.
        var expected = Keys(ThemePalette.Solids(false, AccentPalettes.Blue));

        foreach (var accent in AccentPalettes.All)
        {
            Assert.Equal(expected, Keys(ThemePalette.Solids(false, accent)));
            Assert.Equal(expected, Keys(ThemePalette.Solids(true, accent)));
        }
    }

    [Theory]
    [MemberData(nameof(Combinations))]
    public void Every_value_is_a_solid_eight_digit_colour(bool isLight, string accentId)
    {
        foreach (var (key, value) in ThemePalette.Solids(isLight, AccentPalettes.Parse(accentId)))
        {
            Assert.True(
                Regex.IsMatch(value, "^#[0-9A-Fa-f]{8}$"),
                key + " is not an eight-digit ARGB colour: " + value);
        }
    }

    [Fact]
    public void Every_brush_the_views_use_exists_in_the_theme()
    {
        var declared = new HashSet<string>(ThemePalette.Solids(false, AccentPalettes.Blue).Keys);

        foreach (var key in ThemePalette.Gradients(AccentPalettes.Blue).Keys)
        {
            declared.Add(key);
        }

        // The rest are generated rather than listed. Four are built by the theme service from
        // the accent or the theme, because a radial or a directional brush is a shape as well as
        // a colour; three are white light rather than paint and so never change at all; and the
        // grain is a texture the noise generator installs once.
        declared.Add(ThemeService.HaloKey);
        declared.Add(ThemeService.AmbientKey);
        declared.Add(ThemeService.ContourKey);
        declared.Add(ThemeService.InnerContourKey);
        declared.Add(GlassNoise.Key);
        declared.Add(GlassNoise.RippleKey);
        declared.Add(ThemeService.EdgeReflectionKey);
        declared.Add(ThemeService.TopSheenKey);
        declared.Add("TopHighlightBrush");
        declared.Add("GlossOverlayBrush");
        declared.Add("SpecularHighlightBrush");

        // Not paint at all: the chosen material, published so the panel template can switch its
        // layers on it. It is referenced the same way a brush is, so it has to be named here.
        declared.Add(ThemeService.MaterialKey);

        // The brushes are declared in XAML and replaced by key, so a key referenced in a view
        // but missing from the palette would simply never change with the theme.
        foreach (var key in ReferencedBrushKeys())
        {
            Assert.True(declared.Contains(key), key + " is used by a view but has no theme entry.");
        }
    }

    [Fact]
    public void Gradients_are_declared_in_one_file_and_nowhere_else()
    {
        // A gradient invented inside a view is a gradient the theme cannot reach and the accent
        // cannot change, which is exactly how the old interface ended up with unrelated hues
        // blended together in three different places.
        foreach (var file in ThemeAndViewFiles().Where(f => !f.EndsWith("Brushes.xaml")))
        {
            var text = File.ReadAllText(file);
            var name = Path.GetFileName(file);

            Assert.False(text.Contains("<LinearGradientBrush"), name + " declares a linear gradient.");
            Assert.False(text.Contains("<RadialGradientBrush"), name + " declares a radial gradient.");
            Assert.False(text.Contains("<GradientStop"), name + " declares a gradient stop.");
        }
    }

    [Fact]
    public void Every_state_ramp_is_actually_used_somewhere()
    {
        // A semantic ramp nobody paints with is a state the interface does not express. The
        // running ramp lives in the code that switches the notch edge; the rest are in views.
        var sources = ThemeAndViewFiles()
            .Concat(new[]
            {
                Path.Combine(RepositoryRoot(), "src", "Counter.App", "Views", "NotchWindow.xaml.cs")
            })
            .Select(File.ReadAllText)
            .ToList();

        foreach (var key in ThemePalette.Gradients(AccentPalettes.Blue).Keys)
        {
            Assert.True(sources.Any(text => text.Contains(key, StringComparison.Ordinal)),
                key + " is declared but nothing paints with it.");
        }
    }

    [Fact]
    public void Every_gradient_stays_inside_one_colour_family()
    {
        // The rule that makes a gradient read as lighting rather than as decoration: the three
        // stops are the same hue at three depths, and each one is darker than the last.
        foreach (var accent in AccentPalettes.All)
        {
            foreach (var (key, ramp) in ThemePalette.Gradients(accent))
            {
                var light = Luminance(ramp.Light);
                var mid = Luminance(ramp.Base);
                var deep = Luminance(ramp.Deep);

                Assert.True(light > mid, key + " does not get darker from its lit stop.");
                Assert.True(mid > deep, key + " does not get darker toward its shadowed stop.");

                Assert.True(HueDistance(ramp.Light, ramp.Base) <= 40,
                    key + " changes hue between its lit and base stops.");
                Assert.True(HueDistance(ramp.Base, ramp.Deep) <= 40,
                    key + " changes hue between its base and shadowed stops.");
            }
        }
    }

    [Fact]
    public void The_declared_brushes_agree_with_the_palette_exactly()
    {
        // Brushes.xaml carries literal values so a design-time preview and the first frame before
        // ThemeService runs are both correct. That makes it a second copy of the dark theme with
        // the default accent, and a second copy drifts. Rather than trusting it, every literal is
        // compared with what the palette computes - which, for the accent block, means comparing
        // it with what the engine generates from one base colour.
        var declared = DeclaredBrushes();
        var expected = ThemePalette.Solids(isLight: false, AccentPalettes.Blue);

        foreach (var (key, value) in expected)
        {
            if (key == ThemePalette.ShadowKey)
            {
                continue; // A Color, declared in Colors.xaml rather than as a brush.
            }

            Assert.True(declared.ContainsKey(key), key + " is in the palette but not declared in Brushes.xaml.");
            Assert.True(
                string.Equals(declared[key], value, StringComparison.OrdinalIgnoreCase),
                key + " is declared as " + declared[key] + " but the palette computes " + value + ".");
        }
    }

    [Fact]
    public void The_declared_gradients_agree_with_the_palette_exactly()
    {
        var text = File.ReadAllText(Path.Combine(BrushesFile()));
        var expected = ThemePalette.Gradients(AccentPalettes.Blue);

        foreach (var (key, ramp) in expected)
        {
            var block = Regex.Match(
                text,
                "<LinearGradientBrush x:Key=\"" + key + "\"[^>]*>(.*?)</LinearGradientBrush>",
                RegexOptions.Singleline);

            Assert.True(block.Success, key + " is not declared in Brushes.xaml.");

            var stops = Regex.Matches(block.Groups[1].Value, "Color=\"(#[0-9A-Fa-f]{8})\"")
                .Select(m => m.Groups[1].Value.ToUpperInvariant())
                .ToList();

            Assert.Equal(ramp.Stops.Select(v => v.ToUpperInvariant()), stops);
        }
    }

    [Fact]
    public void No_view_hardcodes_a_colour_literal()
    {
        // Colours belong in Brushes.xaml and the palette, so a theme switch reaches all of them.
        // Brushes.xaml itself is the one file allowed to hold literals: it is the declaration.
        foreach (var file in ThemeAndViewFiles().Where(f => !f.EndsWith("Brushes.xaml")))
        {
            var text = File.ReadAllText(file);
            var matches = Regex.Matches(text, "\"#[0-9A-Fa-f]{6,8}\"");

            Assert.True(
                matches.Count == 0,
                Path.GetFileName(file) + " hardcodes " + matches.Count + " colour(s): " +
                string.Join(", ", matches.Select(m => m.Value)));
        }
    }

    [Fact]
    public void The_dark_theme_is_not_pure_black_and_has_real_separation_between_surfaces()
    {
        // Relative luminance is almost zero for every plausible dark surface, so the "not black"
        // check is made on the raw channels: pure black would be nought across the board.
        Assert.True(Brightest(ThemePalette.Dark["BackgroundBrush"]) >= 0x0E,
            "The dark background is effectively pure black.");

        var steps = new[]
        {
            "BackgroundBrush", "NotchBackgroundBrush", "SurfaceBrush",
            "SurfaceRaisedBrush", "SurfacePressedBrush"
        };

        // Each step has to be genuinely lighter than the one below it, or a control stops
        // reading as sitting on a card and starts reading as painted onto it.
        for (var i = 1; i < steps.Length; i++)
        {
            var below = Brightest(ThemePalette.Dark[steps[i - 1]]);
            var above = Brightest(ThemePalette.Dark[steps[i]]);

            Assert.True(above >= below, steps[i] + " is darker than " + steps[i - 1] + ".");
        }

        // And the whole run must actually go somewhere, not five near-identical blacks.
        Assert.True(
            Brightest(ThemePalette.Dark["SurfacePressedBrush"]) -
            Brightest(ThemePalette.Dark["BackgroundBrush"]) >= 0x18,
            "The dark surface steps are too close together to read as depth.");
    }

    [Fact]
    public void The_light_theme_has_readable_text_and_a_visible_outer_border()
    {
        var surface = Luminance(ThemePalette.Light["NotchBackgroundBrush"]);
        var text = Luminance(ThemePalette.Light["TextPrimaryBrush"]);
        var secondary = Luminance(ThemePalette.Light["TextSecondaryBrush"]);

        Assert.True(Contrast(text, surface) >= 7, "Primary text must be comfortably readable.");
        Assert.True(Contrast(secondary, surface) >= 4.5, "Secondary text must meet AA.");

        // The card has to be findable against a white wallpaper.
        Assert.True(
            Luminance(ThemePalette.Light["EdgeIdleBrush"]) < surface - 0.05,
            "The light theme's outer edge is too close to the surface to be seen.");
    }

    [Theory]
    [MemberData(nameof(Combinations))]
    public void The_heatmap_levels_climb_in_every_theme_and_accent(bool isLight, string accentId)
    {
        var palette = ThemePalette.Solids(isLight, AccentPalettes.Parse(accentId));
        var levels = Enumerable.Range(0, 5).Select(i => Luminance(palette["HeatBrush" + i])).ToList();

        for (var i = 1; i < levels.Count; i++)
        {
            Assert.NotEqual(levels[i], levels[i - 1]);
        }
    }

    [Fact]
    public void The_shadow_is_neutral_in_light_mode_rather_than_black()
    {
        Assert.NotEqual(ThemePalette.Dark[ThemePalette.ShadowKey], ThemePalette.Light[ThemePalette.ShadowKey]);
        Assert.True(Luminance(ThemePalette.Light[ThemePalette.ShadowKey]) > 0.1);
    }

    [Theory]
    [InlineData("System", ThemePreference.System)]
    [InlineData("light", ThemePreference.Light)]
    [InlineData("DARK", ThemePreference.Dark)]
    [InlineData(null, ThemePreference.System)]
    [InlineData("nonsense", ThemePreference.System)]
    public void A_stored_theme_value_round_trips(string? stored, ThemePreference expected)
        => Assert.Equal(expected, ThemePalette.Parse(stored));

    [Fact]
    public void System_resolves_to_whichever_theme_windows_is_using()
    {
        Assert.True(ThemePalette.IsLight(ThemePreference.System, systemIsLight: true));
        Assert.False(ThemePalette.IsLight(ThemePreference.System, systemIsLight: false));

        // An explicit choice ignores Windows entirely.
        Assert.True(ThemePalette.IsLight(ThemePreference.Light, systemIsLight: false));
        Assert.False(ThemePalette.IsLight(ThemePreference.Dark, systemIsLight: true));
    }

    [Fact]
    public void The_first_run_default_is_system()
        => Assert.Equal(ThemePreference.System, ThemePalette.Parse(null));

    // ---------------------------------------------------------------------------------

    private static IEnumerable<string> ThemeAndViewFiles()
    {
        var root = RepositoryRoot();

        foreach (var file in Directory.GetFiles(Path.Combine(root, "src", "Counter.App", "Theme"), "*.xaml"))
        {
            yield return file;
        }

        foreach (var file in Directory.GetFiles(Path.Combine(root, "src", "Counter.App", "Views"), "*.xaml"))
        {
            yield return file;
        }
    }

    private static IEnumerable<string> ReferencedBrushKeys()
    {
        var keys = new HashSet<string>();

        foreach (var file in ThemeAndViewFiles())
        {
            foreach (Match match in Regex.Matches(
                         File.ReadAllText(file), @"(?:Static|Dynamic)Resource\s+([A-Za-z0-9_]+Brush\d?)"))
            {
                keys.Add(match.Groups[1].Value);
            }
        }

        return keys;
    }

    private static string BrushesFile() =>
        Path.Combine(RepositoryRoot(), "src", "Counter.App", "Theme", "Brushes.xaml");

    /// <summary>Every solid brush literal declared in Brushes.xaml, by key.</summary>
    private static Dictionary<string, string> DeclaredBrushes()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (Match match in Regex.Matches(
                     File.ReadAllText(BrushesFile()),
                     "<SolidColorBrush x:Key=\"([A-Za-z0-9_]+)\"\\s+Color=\"(#[0-9A-Fa-f]{8})\""))
        {
            map[match.Groups[1].Value] = match.Groups[2].Value;
        }

        return map;
    }

    private static List<string> Keys(IReadOnlyDictionary<string, string> map) =>
        map.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

    /// <summary>Shortest distance between two hues on the colour wheel, in degrees.</summary>
    private static double HueDistance(string a, string b)
    {
        var difference = Math.Abs(Hue(a) - Hue(b));
        return Math.Min(difference, 360 - difference);
    }

    private static double Hue(string argb)
    {
        var r = int.Parse(argb.Substring(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255d;
        var g = int.Parse(argb.Substring(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255d;
        var b = int.Parse(argb.Substring(7, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255d;

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
            hue = ((g - b) / span + 6) % 6;
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

    /// <summary>Walks up from the test binaries until the solution file is in sight.</summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Counter.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    /// <summary>The largest of the three channels, as a plain byte.</summary>
    private static int Brightest(string argb)
    {
        var r = int.Parse(argb.Substring(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var g = int.Parse(argb.Substring(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var b = int.Parse(argb.Substring(7, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return Math.Max(r, Math.Max(g, b));
    }

    /// <summary>Relative luminance, as WCAG defines it.</summary>
    private static double Luminance(string argb)
    {
        var r = Channel(argb.Substring(3, 2));
        var g = Channel(argb.Substring(5, 2));
        var b = Channel(argb.Substring(7, 2));
        return 0.2126 * r + 0.7152 * g + 0.0722 * b;
    }

    private static double Channel(string hex)
    {
        var value = int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255d;
        return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    private static double Contrast(double a, double b)
    {
        var lighter = Math.Max(a, b);
        var darker = Math.Min(a, b);
        return (lighter + 0.05) / (darker + 0.05);
    }
}

/// <summary>
/// The duration picker, now that a session can be up to ninety-nine hours long.
///
/// The columns deliberately do not carry into each other. Wrapping looks clever and is horrible
/// to use: pressing up on 59 seconds and watching the minutes change is exactly the sort of
/// thing that makes somebody re-check a value they had already set correctly.
/// </summary>
public class DurationPickerTests
{
    [Theory]
    [InlineData(3600, 1, 0, 0)]
    [InlineData(7200, 2, 0, 0)]
    [InlineData(86400, 24, 0, 0)]
    [InlineData(359999, 99, 59, 59)]
    [InlineData(1800, 0, 30, 0)]
    [InlineData(10, 0, 0, 10)]
    public void A_duration_round_trips_through_the_three_columns(
        long seconds, int hours, int minutes, int secs)
    {
        var picker = new DurationPickerViewModel();
        picker.Load(seconds);

        Assert.Equal(hours, picker.Hours);
        Assert.Equal(minutes, picker.Minutes);
        Assert.Equal(secs, picker.Seconds);
        Assert.Equal(seconds, picker.TotalSeconds);
        Assert.True(picker.CanStart);
    }

    [Fact]
    public void Reopening_the_picker_preserves_the_value_that_was_set()
    {
        var picker = new DurationPickerViewModel();
        picker.Load(2 * 3600 + 15 * 60);

        var total = picker.TotalSeconds;
        picker.Load(total);

        Assert.Equal(total, picker.TotalSeconds);
        Assert.Equal("2h 15m", picker.SummaryText);
    }

    [Fact]
    public void Below_ten_seconds_start_is_refused()
    {
        var picker = new DurationPickerViewModel();
        picker.Load(0);

        Assert.False(picker.CanStart);
        Assert.True(picker.HasValidationMessage);

        picker.Seconds = 9;
        Assert.False(picker.CanStart);

        picker.Seconds = 10;
        Assert.True(picker.CanStart);
    }

    [Fact]
    public void Each_column_clamps_to_its_own_range()
    {
        var picker = new DurationPickerViewModel();

        picker.Hours = 500;
        Assert.Equal(99, picker.Hours);

        picker.Hours = -5;
        Assert.Equal(0, picker.Hours);

        picker.Minutes = 120;
        Assert.Equal(59, picker.Minutes);

        picker.Seconds = 99;
        Assert.Equal(59, picker.Seconds);
    }

    [Fact]
    public void A_stepper_never_changes_a_neighbouring_column()
    {
        var picker = new DurationPickerViewModel();
        picker.Load(3600 + 59 * 60 + 59);

        picker.IncrementSecondsCommand.Execute(null);
        Assert.Equal(59, picker.Seconds);
        Assert.Equal(59, picker.Minutes);
        Assert.Equal(1, picker.Hours);

        picker.IncrementMinutesCommand.Execute(null);
        Assert.Equal(59, picker.Minutes);
        Assert.Equal(1, picker.Hours);

        picker.Load(0);
        picker.DecrementSecondsCommand.Execute(null);
        Assert.Equal(0, picker.Seconds);
        Assert.Equal(0, picker.Minutes);
        Assert.Equal(0, picker.Hours);
    }

    [Fact]
    public void The_presets_are_the_four_lengths_a_session_usually_takes()
    {
        var picker = new DurationPickerViewModel();

        Assert.Equal(new[] { "25m", "45m", "1h", "2h" }, picker.Presets.Select(p => p.Label));

        picker.ApplyPresetCommand.Execute(picker.Presets[3]);

        Assert.Equal(7200, picker.TotalSeconds);
        Assert.Equal(2, picker.Hours);
        Assert.Equal(0, picker.Minutes);
    }

    [Fact]
    public void The_maximum_is_accepted_and_one_second_more_is_not()
    {
        var picker = new DurationPickerViewModel();
        picker.Load(FocusDefaults.MaxSeconds);

        Assert.True(picker.CanStart);
        Assert.Equal(FocusDefaults.MaxSeconds, picker.TotalSeconds);

        // Loading past the maximum clamps rather than overflowing.
        picker.Load(FocusDefaults.MaxSeconds + 10_000);
        Assert.Equal(FocusDefaults.MaxSeconds, picker.TotalSeconds);
    }
}

/// <summary>How durations read on screen.</summary>
public class DurationFormattingTests
{
    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(59, "00:59")]
    [InlineData(1800, "30:00")]
    [InlineData(3599, "59:59")]
    [InlineData(3600, "1:00:00")]
    [InlineData(7325, "2:02:05")]
    [InlineData(36000, "10:00:00")]
    [InlineData(359999, "99:59:59")]
    public void A_countdown_grows_a_field_only_when_it_has_to(long seconds, string expected)
        => Assert.Equal(expected, TimeFormat.Countdown(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void A_countdown_is_never_negative()
        => Assert.Equal("00:00", TimeFormat.Countdown(TimeSpan.FromSeconds(-500)));

    [Theory]
    [InlineData(0, "0m")]
    [InlineData(30, "<1m")]
    [InlineData(2520, "42m")]
    [InlineData(5040, "1h 24m")]
    [InlineData(43680, "12h 08m")]
    public void Time_spent_reads_the_way_a_row_needs_it(long seconds, string expected)
        => Assert.Equal(expected, TimeFormat.Spent(seconds));

    [Theory]
    [InlineData(1500, "25m")]
    [InlineData(3600, "1h 00m")]
    [InlineData(7200, "2h 00m")]
    [InlineData(359999, "99h 59m")]
    public void A_planned_duration_reads_compactly(long seconds, string expected)
        => Assert.Equal(expected, TimeFormat.Compact(seconds));

    [Fact]
    public void A_long_duration_survives_being_stored_and_read_back()
    {
        var task = new TaskItem { EstimatedSeconds = FocusDefaults.MaxSeconds };

        Assert.True(TaskValidator.ValidateDuration(task.EstimatedSeconds).IsValid);
        Assert.Equal(359999L, task.EstimatedSeconds);
    }
}
