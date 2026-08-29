using System.Windows;
using System.Windows.Media;
using FocusNotch.App.Services;
using FocusNotch.Core.Abstractions;
using FocusNotch.Core.Models;
using Microsoft.Win32;

namespace FocusNotch.App.Theme;

/// <summary>
/// Applies a theme and an accent by replacing the brush behind each theme key.
///
/// Every reference in the app is a DynamicResource, so replacing an entry re-resolves exactly
/// the visuals that use it. Nothing is rebuilt: no dictionary is swapped, no template is
/// regenerated, no window is recreated, and the panel keeps whatever state it was in, timer
/// included. The replacements are frozen, so rendering afterwards costs no more than before.
///
/// Theme and accent are independent inputs. Switching from Dark to Light keeps the chosen
/// accent; switching from Blue to Orange keeps the chosen theme. Both are recomputed from
/// scratch on every repaint rather than patched, so no combination can leave a stale colour
/// behind from the one before it.
///
/// There is no colour in this file. Every value comes out of <see cref="ThemePalette"/>, which
/// gets its accent values out of the engine; this class knows only about geometry - where the
/// light comes from, where the stops sit, how far a halo reaches.
/// </summary>
public sealed class ThemeService : IDisposable
{
    // ==================================================================== the light source

    /// <summary>
    /// One light, upper left, travelling to the lower right. Every gradient in the application
    /// runs along this vector, which is what stops two adjacent controls looking like they are
    /// lit from opposite sides. On a square control it is the 135-degree direction.
    /// </summary>
    private static readonly Point GradientStart = new(0, 0);
    private static readonly Point GradientEnd = new(1, 1);

    /// <summary>The five offsets every illuminated gradient uses. Never varied per control.</summary>
    public static readonly double[] Offsets = { 0.00, 0.18, 0.48, 0.76, 1.00 };

    /// <summary>Where a specular highlight sits: near the upper left, not in the middle.</summary>
    private static readonly Point HighlightOrigin = new(0.18, 0.06);
    private static readonly Point HighlightCentre = new(0.22, 0.10);

    // ==================================================================== generated keys

    /// <summary>The halo behind an active primary control. Radial, so it is rebuilt not recoloured.</summary>
    public const string HaloKey = "AccentHaloBrush";

    /// <summary>The warm light the glass picks up near something active. Radial as well.</summary>
    public const string AmbientKey = "AccentAmbientBrush";

    /// <summary>The neutral structural edge of a glass panel. Directional, so it is theme-built.</summary>
    public const string ContourKey = "GlassContourBrush";

    /// <summary>The thin second edge just inside it, which is what gives the glass thickness.</summary>
    public const string InnerContourKey = "GlassInnerContourBrush";

    /// <summary>
    /// The light caught along the far edge of the glass, brightest at the corner furthest from
    /// the source and gone by the middle of the surface. It is not a second specular highlight:
    /// the sheen is where the light lands, and this is where it leaves.
    /// </summary>
    public const string EdgeReflectionKey = "GlassEdgeReflectionBrush";

    /// <summary>The soft fall of light down the face of the glass from its top edge.</summary>
    public const string TopSheenKey = "GlassTopSheenBrush";

    /// <summary>
    /// The chosen material, published as a resource so the panel template can switch its layers
    /// on it. A value rather than a brush, and the one entry in the dictionary that is not paint.
    /// </summary>
    public const string MaterialKey = "GlassMaterial";

    private readonly ISettingsStore _settings;
    private readonly ResourceDictionary _resources;
    private bool _listening;
    private bool _disposed;

    public ThemeService(ISettingsStore settings, ResourceDictionary resources)
    {
        _settings = settings;
        _resources = resources;
        Preference = ThemePalette.Parse(_settings.Get(SettingKeys.Theme));
        Accent = AccentPalettes.Parse(_settings.Get(SettingKeys.AccentPalette));
        Material = GlassMaterials.Parse(_settings.Get(SettingKeys.GlassMaterial));
    }

    /// <summary>What the user chose. System on first run.</summary>
    public ThemePreference Preference { get; private set; }

    /// <summary>The accent family. Blue on first run, and on any unreadable stored value.</summary>
    public AccentPalette Accent { get; private set; }

    /// <summary>What the panels are made of. Solid on first run, and on any unreadable value.</summary>
    public GlassMaterial Material { get; private set; }

    /// <summary>
    /// Whether a real blur is being drawn behind the panels right now.
    ///
    /// The glass is mixed differently depending on the answer. The host sets this and asks for a
    /// repaint when it changes, which is also what happens if the compositor turns out not to
    /// give us a backdrop at all: the material stays chosen, and the density falls back to the
    /// one that is legible without one.
    /// </summary>
    public bool Blurred { get; set; }

    /// <summary>What the theme preference currently resolves to.</summary>
    public bool IsLight { get; private set; }

    /// <summary>Raised after the resources have been recoloured.</summary>
    public event Action? Changed;

    /// <summary>Applies the stored preferences and starts following the system when asked to.</summary>
    public void Initialize()
    {
        Apply(Preference, persist: false);

        if (!_listening)
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            _listening = true;
        }
    }

    public void Apply(ThemePreference preference, bool persist = true)
    {
        Preference = preference;

        if (persist)
        {
            Save(SettingKeys.Theme, ThemePalette.Label(preference), "theme");
        }

        Repaint();
    }

    /// <summary>
    /// Switches accent family. Only the identifier is stored: every colour is regenerated from
    /// the palette's one base colour, so a stored setting can never describe a combination
    /// nobody designed, and a palette cannot be internally inconsistent.
    /// </summary>
    public void ApplyAccent(string id, bool persist = true)
    {
        Accent = AccentPalettes.Parse(id);

        if (persist)
        {
            Save(SettingKeys.AccentPalette, Accent.Id, "accent");
        }

        Repaint();
    }

    /// <summary>
    /// Switches the glass. A third independent input: changing the material never touches the
    /// theme or the accent, and is applied by the same repaint every other preference uses, so
    /// the panel keeps its state and nothing is rebuilt.
    /// </summary>
    public void ApplyMaterial(GlassMaterial material, bool persist = true)
    {
        Material = material;

        if (persist)
        {
            Save(SettingKeys.GlassMaterial, material.ToString(), "glass");
        }

        Repaint();
    }

    /// <summary>Re-resolves both preferences and pushes the colours into the resource dictionary.</summary>
    public void Repaint()
    {
        var systemIsLight = SystemUsesLightTheme();
        IsLight = ThemePalette.IsLight(Preference, systemIsLight);

        var applied = 0;
        var missing = 0;

        // The material is a value, not paint, and it is written before the brushes so that a
        // template re-resolving mid-repaint never sees one material's layers over another's.
        _resources[MaterialKey] = Material;

        foreach (var (key, value) in ThemePalette.Solids(IsLight, Accent, Material, Blurred))
        {
            var colour = ThemePalette.ToColor(value);

            if (key == ThemePalette.ShadowKey)
            {
                // Effects take a colour rather than a brush, so this entry is a Color.
                _resources[key] = colour;
                applied++;
                continue;
            }

            if (_resources[key] is not SolidColorBrush)
            {
                missing++;
                continue;
            }

            // The entry is replaced with a fresh frozen brush rather than recoloured in place.
            // Resources declared in a compiled dictionary come back frozen, so they cannot be
            // mutated at all; replacing the entry is what actually works, and every reference in
            // the app is a DynamicResource, so they all re-resolve on the next layout pass. The
            // replacement is frozen too, which is what keeps rendering cheap afterwards.
            var replacement = new SolidColorBrush(colour);
            replacement.Freeze();
            _resources[key] = replacement;
            applied++;
        }

        foreach (var (key, ramp) in ThemePalette.Gradients(Accent))
        {
            if (_resources[key] is not LinearGradientBrush)
            {
                missing++;
                continue;
            }

            _resources[key] = BuildGradient(ramp);
            applied++;
        }

        Replace(HaloKey, () => BuildHalo(Accent), ref applied, ref missing);
        Replace(AmbientKey, () => BuildAmbient(Accent), ref applied, ref missing);
        Replace(ContourKey, () => BuildStructuralContour(IsLight), ref applied, ref missing);
        Replace(InnerContourKey, () => BuildInnerContour(IsLight), ref applied, ref missing);
        Replace(EdgeReflectionKey, BuildEdgeReflection, ref applied, ref missing);
        Replace(TopSheenKey, BuildTopSheen, ref applied, ref missing);

        Diag.Write("theme", "applied", ("preference", Preference), ("light", IsLight),
            ("accent", Accent.Id), ("glass", Material), ("blurred", Blurred),
            ("keys", applied), ("unresolved", missing));

        if (missing > 0)
        {
            Log.Warn(missing + " theme resource(s) could not be recoloured.");
        }

        Changed?.Invoke();
    }

    /// <summary>Swaps one generated brush, provided the dictionary already declares that key.</summary>
    private void Replace(string key, Func<Brush> build, ref int applied, ref int missing)
    {
        if (_resources[key] is GradientBrush)
        {
            _resources[key] = build();
            applied++;
        }
        else
        {
            missing++;
        }
    }

    // ==================================================================== brush construction

    /// <summary>
    /// The far-edge reflection: white at the corner opposite the light, gone by half way across.
    ///
    /// The reference runs it to the upper left, which puts the bright corner at the lower right -
    /// and that is the corner opposite this application's light source too, so the geometry is
    /// kept exactly as written. It stops at the midpoint rather than fading the whole way, which
    /// is what makes it read as an edge catching light rather than as a second gradient over the
    /// surface.
    /// </summary>
    public static LinearGradientBrush BuildEdgeReflection()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(1, 1),
            EndPoint = new Point(0, 0),
            MappingMode = BrushMappingMode.RelativeToBoundingBox
        };

        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x3A, 0xFF, 0xFF, 0xFF), 0.00));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.50));
        brush.Freeze();
        return brush;
    }

    /// <summary>The sheen down the face from the top edge. Straight down, not along the light.</summary>
    public static LinearGradientBrush BuildTopSheen()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            MappingMode = BrushMappingMode.RelativeToBoundingBox
        };

        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x23, 0xFF, 0xFF, 0xFF), 0.00));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1.00));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Builds one illuminated gradient: five stops of a single family, lit from the upper left.
    ///
    /// The offsets are deliberately not evenly spaced. The lit pair sit close together in the
    /// first fifth, the material's own colour lands just before the middle, and the shadowed half
    /// runs long - which is how light actually falls across a curved surface, and what stops the
    /// ramp reading as a flat blend between two colours.
    /// </summary>
    public static LinearGradientBrush BuildGradient(GradientRamp ramp)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = GradientStart,
            EndPoint = GradientEnd,
            MappingMode = BrushMappingMode.RelativeToBoundingBox
        };

        var stops = ramp.Stops;

        for (var index = 0; index < stops.Count; index++)
        {
            brush.GradientStops.Add(new GradientStop(ThemePalette.ToColor(stops[index]), Offsets[index]));
        }

        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// The soft light around an active primary control. A radial fade rather than a blurred
    /// shadow, so it costs one gradient fill instead of a render-target blur on every frame the
    /// panel is resizing, and it is capped at twelve percent so it reads as light rather than as
    /// a second coloured disc.
    /// </summary>
    public static RadialGradientBrush BuildHalo(AccentPalette accent)
    {
        var colour = ThemePalette.ToColor(accent.Ramp.Glow);

        return Radial(
            new Point(0.5, 0.5), new Point(0.5, 0.5), 0.5, 0.5,
            (Alpha(colour, 0x1F), 0.0),
            (Alpha(colour, 0x1F), 0.55),
            (Alpha(colour, 0x00), 1.0));
    }

    /// <summary>
    /// The warm light the glass picks up close to something active: the accent's own colour at
    /// under a tenth, thrown from the same upper-left origin as every other highlight, gone
    /// entirely before the far corner.
    ///
    /// It is a reflection, not a background. Nothing paints it behind an inactive task, behind
    /// the settings text, or across a whole panel.
    /// </summary>
    public static RadialGradientBrush BuildAmbient(AccentPalette accent)
    {
        var colour = ThemePalette.ToColor(accent.Ramp.Base);

        return Radial(
            HighlightCentre, HighlightOrigin, 0.85, 0.95,
            (Alpha(colour, 0x18), 0.0),
            (Alpha(colour, 0x0C), 0.45),
            (Alpha(colour, 0x00), 1.0));
    }

    /// <summary>
    /// The neutral edge of a glass panel, which is what makes it a physical object rather than a
    /// region of tint.
    ///
    /// Both themes describe the same edge lit from the same place, but they have to do it with
    /// opposite materials. On a dark panel the lit edge is white and the far edge falls to black.
    /// On a light panel white on white is nothing at all, so only the very first sliver of the
    /// top-left lip stays white and the rest of the perimeter is a soft dark line - which is what
    /// keeps the tool visible over a bright wallpaper.
    /// </summary>
    public static LinearGradientBrush BuildStructuralContour(bool isLight) => isLight
        ? Linear(
            (Color.FromArgb(0x6B, 0xFF, 0xFF, 0xFF), 0.00),
            (Color.FromArgb(0x1C, 0x00, 0x00, 0x00), 0.16),
            (Color.FromArgb(0x22, 0x00, 0x00, 0x00), 0.48),
            (Color.FromArgb(0x2B, 0x00, 0x00, 0x00), 0.76),
            (Color.FromArgb(0x38, 0x00, 0x00, 0x00), 1.00))
        : Linear(
            (Color.FromArgb(0x42, 0xFF, 0xFF, 0xFF), 0.00),
            (Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF), 0.18),
            (Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF), 0.48),
            (Color.FromArgb(0x14, 0x00, 0x00, 0x00), 0.76),
            (Color.FromArgb(0x26, 0x00, 0x00, 0x00), 1.00));

    /// <summary>
    /// The second, much fainter edge drawn just inside the first. Light along the top and left,
    /// dark along the bottom and right, which is what reads as the thickness of the glass.
    ///
    /// It is never coloured. An inner edge that follows the accent would look like a second
    /// contour rather than like a material, which is exactly the doubled-border effect the whole
    /// construction exists to avoid.
    /// </summary>
    public static LinearGradientBrush BuildInnerContour(bool isLight) => isLight
        ? Linear(
            (Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF), 0.00),
            (Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF), 0.45),
            (Color.FromArgb(0x10, 0x00, 0x00, 0x00), 1.00))
        : Linear(
            (Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF), 0.00),
            (Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF), 0.45),
            (Color.FromArgb(0x10, 0x00, 0x00, 0x00), 1.00));

    private static LinearGradientBrush Linear(params (Color Colour, double Offset)[] stops)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = GradientStart,
            EndPoint = GradientEnd,
            MappingMode = BrushMappingMode.RelativeToBoundingBox
        };

        foreach (var (colour, offset) in stops)
        {
            brush.GradientStops.Add(new GradientStop(colour, offset));
        }

        brush.Freeze();
        return brush;
    }

    private static RadialGradientBrush Radial(
        Point centre, Point origin, double radiusX, double radiusY,
        params (Color Colour, double Offset)[] stops)
    {
        var brush = new RadialGradientBrush
        {
            Center = centre,
            GradientOrigin = origin,
            RadiusX = radiusX,
            RadiusY = radiusY
        };

        foreach (var (colour, offset) in stops)
        {
            brush.GradientStops.Add(new GradientStop(colour, offset));
        }

        brush.Freeze();
        return brush;
    }

    private static Color Alpha(Color colour, byte alpha) =>
        Color.FromArgb(alpha, colour.R, colour.G, colour.B);

    // ==================================================================== preferences

    private void Save(string key, string value, string what)
    {
        try
        {
            _settings.Set(key, value);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not save the " + what + " preference.", ex);
        }
    }

    /// <summary>
    /// The Windows app theme. Read from the registry rather than guessed, and any failure falls
    /// back to dark, which is what the app looked like before there was a choice.
    /// </summary>
    public static bool SystemUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");

            return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read the Windows theme preference.", ex);
            return false;
        }
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (_disposed || Preference != ThemePreference.System)
        {
            return;
        }

        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle
            or UserPreferenceCategory.Color)
        {
            Repaint();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_listening)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _listening = false;
        }
    }
}
