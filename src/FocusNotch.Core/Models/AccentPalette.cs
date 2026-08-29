using System.Collections.Concurrent;
using FocusNotch.Core.Colour;

namespace FocusNotch.Core.Models;

/// <summary>
/// One accent choice. A name and a single base colour, and nothing else.
///
/// This is the whole point of the accent system: a palette is not a list of gradient stops that
/// somebody assembled by hand, because a list assembled by hand is how an interface ends up with
/// a blue highlight over a pink base. A palette is one colour, and <see cref="AccentEngine"/>
/// derives every lit, shadowed, glowing and readable value from it by a single rule that has no
/// idea which colour it was handed. Adding a seventh family is one line; there is no second
/// place to keep in step.
/// </summary>
/// <param name="Id">The stored identifier. Lowercase, stable, and the only thing persisted.</param>
/// <param name="DisplayName">What the settings panel calls it.</param>
/// <param name="Base">The chosen colour. Everything else is generated from it.</param>
public sealed record AccentPalette(string Id, string DisplayName, string Base)
{
    private static readonly ConcurrentDictionary<string, AccentRamp> Derived = new();

    /// <summary>
    /// The generated ramp.
    ///
    /// Cached by base colour rather than held on the record, so two palettes describing the same
    /// colour stay equal to each other and a repaint costs a dictionary lookup. Deriving is
    /// cheap, but it happens on every theme change and there is no reason to pay for it twice.
    /// </summary>
    public AccentRamp Ramp => Derived.GetOrAdd(Base, static colour => AccentEngine.Derive(colour));
}

/// <summary>
/// The six families that ship, and the door a custom colour comes through.
///
/// Each preset supplies its base colour and nothing more. Everything the interface paints with -
/// the five gradient stops, the contour, the halo, the tint, the readable foreground - comes out
/// of the engine, so a preset cannot be internally inconsistent and a custom colour is not a
/// second-class citizen: it goes through exactly the same code.
/// </summary>
public static class AccentPalettes
{
    /// <summary>What a first run, an unreadable setting or a removed palette resolves to.</summary>
    public const string DefaultId = "blue";

    /// <summary>The prefix a stored custom colour carries, so it cannot collide with a name.</summary>
    public const string CustomPrefix = "custom:";

    public static readonly AccentPalette Blue = new("blue", "Blue", "#FF438BFF");

    public static readonly AccentPalette Cyan = new("cyan", "Cyan", "#FF23BDD4");

    public static readonly AccentPalette Green = new("green", "Green", "#FF35C77D");

    public static readonly AccentPalette Purple = new("purple", "Purple", "#FF9468F2");

    public static readonly AccentPalette Pink = new("pink", "Pink", "#FFF15C9D");

    public static readonly AccentPalette Orange = new("orange", "Orange", "#FFFF9638");

    /// <summary>In the order the settings panel shows them.</summary>
    public static readonly IReadOnlyList<AccentPalette> All = new[]
    {
        Blue, Cyan, Green, Purple, Pink, Orange
    };

    public static AccentPalette Default => Blue;

    private static readonly ConcurrentDictionary<string, AccentPalette> CustomCache = new();

    /// <summary>
    /// Resolves a stored identifier: one of the six names, or <c>custom:#RRGGBB</c>.
    ///
    /// Anything unrecognised - a typo, a hand-edited database, a palette that used to exist -
    /// falls back to the default rather than throwing, because a bad colour preference is never
    /// a reason to refuse to draw the interface.
    /// </summary>
    public static AccentPalette Parse(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return Default;
        }

        var trimmed = id.Trim();

        foreach (var palette in All)
        {
            if (string.Equals(palette.Id, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return palette;
            }
        }

        if (trimmed.StartsWith(CustomPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return Custom(trimmed.Substring(CustomPrefix.Length));
        }

        return Default;
    }

    /// <summary>
    /// Builds a palette from an arbitrary colour. Used by a colour picker if the settings panel
    /// ever grows one, and by the tests that prove the engine is not tuned to six inputs.
    /// </summary>
    public static AccentPalette Custom(string colour)
    {
        var trimmed = (colour ?? string.Empty).Trim();

        if (trimmed.Length == 0)
        {
            return Default;
        }

        return CustomCache.GetOrAdd(trimmed.ToUpperInvariant(), static key =>
        {
            try
            {
                // Parsing is what proves the string is a colour. A ramp is derived immediately
                // so an unusable value fails here rather than halfway through a repaint.
                var ramp = AccentEngine.Derive(key);
                return new AccentPalette(CustomPrefix + ramp.Base, "Custom", ramp.Base);
            }
            catch (FormatException)
            {
                return Default;
            }
        });
    }

    /// <summary>True when the identifier names a palette that actually ships.</summary>
    public static bool IsKnown(string? id) =>
        !string.IsNullOrWhiteSpace(id)
        && All.Any(palette => string.Equals(palette.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
}
