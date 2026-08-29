using System.Globalization;
using System.Windows.Media;

namespace FocusNotch.App.Theme;

/// <summary>
/// Turns whatever somebody typed into a colour, or says it could not.
///
/// The accent engine takes one canonical form and is right to: a derivation that also has to
/// guess what "red" means is a derivation with two jobs. So the guessing happens here, once, at
/// the edge, and what reaches the engine is always eight digits.
///
/// What is accepted is what a person would reasonably type: a name, three digits, six, eight,
/// with or without the hash. What is not accepted fails as a false return rather than an
/// exception, because a half-typed colour is the normal state of a text field somebody is still
/// typing into, and it is not an error.
/// </summary>
public static class ColourInput
{
    /// <summary>Reads a colour and renders it in the eight-digit form the engine expects.</summary>
    public static bool TryNormalise(string? input, out string hex)
    {
        hex = string.Empty;

        var text = (input ?? string.Empty).Trim();

        if (text.Length == 0)
        {
            return false;
        }

        // Three digits is the shorthand every stylesheet in the world uses, and the one form
        // WPF's own parser does not read.
        if (text.StartsWith('#') && text.Length == 4)
        {
            text = "#" + string.Concat(text.Substring(1).Select(c => new string(c, 2)));
        }
        else if (!text.StartsWith('#') && text.Length is 3 or 6 or 8 && IsHex(text))
        {
            text = "#" + (text.Length == 3
                ? string.Concat(text.Select(c => new string(c, 2)))
                : text);
        }

        try
        {
            if (ColorConverter.ConvertFromString(text) is not Color colour)
            {
                return false;
            }

            // The alpha is dropped rather than carried. An accent is a colour, not a colour and
            // a transparency: how much of it shows through is the material's business, and a
            // half-transparent base would quietly desaturate every stop derived from it.
            hex = string.Format(
                CultureInfo.InvariantCulture, "#FF{0:X2}{1:X2}{2:X2}", colour.R, colour.G, colour.B);

            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            // What ColorConverter raises for a string that is not a colour at all.
            return false;
        }
    }

    private static bool IsHex(string text) =>
        text.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'));
}
