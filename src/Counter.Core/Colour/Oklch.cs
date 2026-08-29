using System.Globalization;

namespace Counter.Core.Colour;

/// <summary>One colour in the OKLCH cylinder: lightness, chroma and hue.</summary>
/// <param name="L">Perceptual lightness. 0 is black, 1 is white.</param>
/// <param name="C">Chroma. 0 is grey; the sRGB boundary sits somewhere near 0.32.</param>
/// <param name="H">Hue, in radians. Preserved exactly by every derivation.</param>
public readonly record struct Oklch(double L, double C, double H);

/// <summary>
/// sRGB to OKLab and back, plus the one operation that makes a derived palette usable: mapping
/// a colour that has drifted outside sRGB back onto its boundary by giving up chroma and
/// nothing else.
///
/// Why a perceptual space at all. Lightening a colour by pushing its RGB channels toward 255
/// desaturates blue into lavender and turns orange into cream, because the channels do not
/// carry equal perceptual weight and the hue drifts as soon as one of them saturates. In OKLab
/// the three axes are close to independent, so "the same colour, lighter" is one number and it
/// stays the same colour. That is the entire reason the accent engine can take a single base
/// colour and produce a ramp that still looks like one material.
///
/// The transform is Björn Ottosson's, unchanged.
/// </summary>
public static class Perceptual
{
    // ==================================================================== transfer function

    private static double ToLinear(double channel) =>
        channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);

    private static double ToSrgb(double channel) =>
        channel <= 0.0031308 ? channel * 12.92 : (1.055 * Math.Pow(channel, 1.0 / 2.4)) - 0.055;

    // ==================================================================== sRGB to OKLab

    public static (double L, double A, double B) RgbToOklab(double r, double g, double b)
    {
        var lr = ToLinear(r);
        var lg = ToLinear(g);
        var lb = ToLinear(b);

        var l = (0.4122214708 * lr) + (0.5363325363 * lg) + (0.0514459929 * lb);
        var m = (0.2119034982 * lr) + (0.6806995451 * lg) + (0.1073969566 * lb);
        var s = (0.0883024619 * lr) + (0.2817188376 * lg) + (0.6299787005 * lb);

        var lc = Cbrt(l);
        var mc = Cbrt(m);
        var sc = Cbrt(s);

        return (
            (0.2104542553 * lc) + (0.7936177850 * mc) - (0.0040720468 * sc),
            (1.9779984951 * lc) - (2.4285922050 * mc) + (0.4505937099 * sc),
            (0.0259040371 * lc) + (0.7827717662 * mc) - (0.8086757660 * sc));
    }

    public static (double R, double G, double B) OklabToRgb(double L, double a, double b)
    {
        var lc = L + (0.3963377774 * a) + (0.2158037573 * b);
        var mc = L - (0.1055613458 * a) - (0.0638541728 * b);
        var sc = L - (0.0894841775 * a) - (1.2914855480 * b);

        var l = lc * lc * lc;
        var m = mc * mc * mc;
        var s = sc * sc * sc;

        return (
            ToSrgb((+4.0767416621 * l) - (3.3077115913 * m) + (0.2309699292 * s)),
            ToSrgb((-1.2684380046 * l) + (2.6097574011 * m) - (0.3413193965 * s)),
            ToSrgb((-0.0041960863 * l) - (0.7034186147 * m) + (1.7076147010 * s)));
    }

    private static double Cbrt(double value) =>
        value >= 0 ? Math.Pow(value, 1.0 / 3.0) : -Math.Pow(-value, 1.0 / 3.0);

    // ==================================================================== hex in, hex out

    /// <summary>Reads a six or eight digit hex colour. The alpha channel is ignored.</summary>
    public static Oklch FromHex(string hex)
    {
        var digits = hex.TrimStart('#');

        if (digits.Length == 8)
        {
            digits = digits.Substring(2);
        }

        if (digits.Length != 6)
        {
            throw new FormatException("Expected a six or eight digit hex colour, got: " + hex);
        }

        double Channel(int offset) =>
            int.Parse(digits.Substring(offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;

        var (l, a, b) = RgbToOklab(Channel(0), Channel(2), Channel(4));

        return new Oklch(l, Math.Sqrt((a * a) + (b * b)), Math.Atan2(b, a));
    }

    /// <summary>
    /// Renders an OKLCH colour as an opaque eight-digit hex string, giving up chroma - and only
    /// chroma - until it fits inside sRGB.
    ///
    /// This is what keeps a derived stop honest. Asking for a light, vivid blue produces a
    /// colour sRGB cannot show; clipping the channels would shift its hue toward cyan and its
    /// lightness upward, so instead the chroma is bisected down to the largest value the display
    /// can actually reproduce, and the lightness and the hue - the two things the derivation
    /// meant - survive untouched.
    /// </summary>
    public static string ToHex(Oklch colour)
    {
        var chroma = Math.Max(0, colour.C);

        if (!InGamut(colour.L, chroma, colour.H))
        {
            var low = 0.0;
            var high = chroma;

            // Twenty-eight halvings takes the interval below a ten-millionth, which is far
            // finer than the eight bits a channel is about to be rounded to.
            for (var step = 0; step < 28; step++)
            {
                var middle = (low + high) / 2;

                if (InGamut(colour.L, middle, colour.H))
                {
                    low = middle;
                }
                else
                {
                    high = middle;
                }
            }

            chroma = low;
        }

        var (r, g, b) = OklabToRgb(colour.L, Math.Cos(colour.H) * chroma, Math.Sin(colour.H) * chroma);

        return string.Format(
            CultureInfo.InvariantCulture, "#FF{0:X2}{1:X2}{2:X2}", Byte(r), Byte(g), Byte(b));
    }

    private static bool InGamut(double l, double c, double h)
    {
        var (r, g, b) = OklabToRgb(l, Math.Cos(h) * c, Math.Sin(h) * c);
        return Fits(r) && Fits(g) && Fits(b);

        static bool Fits(double channel) => channel >= -1e-4 && channel <= 1 + 1e-4;
    }

    private static int Byte(double channel) =>
        (int)Math.Round(Math.Clamp(channel, 0, 1) * 255, MidpointRounding.AwayFromZero);

    // ==================================================================== readability

    /// <summary>Relative luminance, as WCAG defines it, from an ARGB or RGB hex string.</summary>
    public static double Luminance(string hex)
    {
        var digits = hex.TrimStart('#');

        if (digits.Length == 8)
        {
            digits = digits.Substring(2);
        }

        double Channel(int offset) => ToLinear(
            int.Parse(digits.Substring(offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0);

        return (0.2126 * Channel(0)) + (0.7152 * Channel(2)) + (0.0722 * Channel(4));
    }

    /// <summary>The WCAG contrast ratio between two colours. 1 is identical, 21 is black on white.</summary>
    public static double Contrast(string a, string b)
    {
        var first = Luminance(a);
        var second = Luminance(b);
        var lighter = Math.Max(first, second);
        var darker = Math.Min(first, second);

        return (lighter + 0.05) / (darker + 0.05);
    }
}
