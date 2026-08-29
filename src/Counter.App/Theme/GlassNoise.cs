using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Counter.App.Theme;

/// <summary>
/// The faint grain that stops a glass surface looking like a flat digital fill.
///
/// A perfectly smooth gradient over a perfectly flat tint is the thing that gives away that a
/// surface is a rectangle of colour rather than a material. A couple of percent of high-frequency
/// monochrome noise fixes that, and it has to be exactly that: monochrome, so it does not tint
/// anything; high-frequency, so it reads as texture rather than as mottling; and very faint, so
/// it is felt rather than seen. Anything stronger is film grain, which is a different effect and
/// a worse one.
///
/// One tile, generated once, frozen, tiled in absolute units so it stays fixed relative to the
/// panel instead of stretching when the panel resizes. It is never animated - moving grain is
/// the single most distracting thing an idle window can do.
/// </summary>
public static class GlassNoise
{
    /// <summary>The resource key the glass templates paint with.</summary>
    public const string Key = "GlassNoiseBrush";

    /// <summary>
    /// The tile edge. Large enough that the eye cannot find the repeat at this contrast, small
    /// enough that the whole texture is sixty-four kilobytes and lives in the composition cache.
    /// </summary>
    public const int TileSize = 128;

    /// <summary>
    /// How strongly each grain pixel is laid over the surface, out of 255. Two percent: at this
    /// alpha a full black-to-white spread moves the underlying colour by about one level, which
    /// is the entire point.
    /// </summary>
    public const byte Strength = 5;

    /// <summary>
    /// A fixed seed. The texture is identical on every machine and every launch, so a screenshot
    /// taken today can be compared with one taken next month, and a rendering test can assert on
    /// actual pixels.
    /// </summary>
    public const int Seed = 0x51F0;

    private static ImageBrush? _brush;

    /// <summary>Builds the tile once and hands back the same frozen brush thereafter.</summary>
    public static ImageBrush Brush()
    {
        if (_brush is not null)
        {
            return _brush;
        }

        var pixels = new byte[TileSize * TileSize * 4];
        var random = new Random(Seed);

        for (var index = 0; index < pixels.Length; index += 4)
        {
            // One grey level per pixel, premultiplied by the strength: Bgra32 in WPF is
            // straight alpha, so the three channels carry the grey and the fourth the weight.
            var level = (byte)random.Next(256);

            pixels[index] = level;
            pixels[index + 1] = level;
            pixels[index + 2] = level;
            pixels[index + 3] = Strength;
        }

        var bitmap = BitmapSource.Create(
            TileSize, TileSize, 96, 96, PixelFormats.Bgra32, null, pixels, TileSize * 4);

        bitmap.Freeze();

        var brush = new ImageBrush(bitmap)
        {
            TileMode = TileMode.Tile,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, TileSize, TileSize),
            Stretch = Stretch.None
        };

        brush.Freeze();
        _brush = brush;

        return brush;
    }

    // ==================================================================== the ripple

    /// <summary>The resource key the liquid material paints its unevenness with.</summary>
    public const string RippleKey = "GlassRippleBrush";

    /// <summary>
    /// The ripple tile. Four times the grain, because this texture is the opposite of grain: it
    /// is meant to be seen as slow variation across a whole panel rather than felt as surface.
    /// </summary>
    public const int RippleSize = 256;

    /// <summary>The reference filter's seed, kept so the texture is the one that was designed.</summary>
    public const int RippleSeed = 92;

    /// <summary>
    /// How far the ripple moves the surface, out of 255. Ten percent at its extremes, which on a
    /// translucent panel is about the difference a real pour leaves behind.
    /// </summary>
    public const byte RippleStrength = 26;

    private static ImageBrush? _ripple;

    /// <summary>
    /// The slow unevenness of poured glass: two octaves of fractal noise, smoothed.
    ///
    /// The reference achieves its version by displacing the backdrop through this noise, which
    /// needs to sample what is behind the window. Nothing in WPF can do that on a layered window,
    /// so what is reproduced is the noise field itself, laid over the glass as variation in how
    /// much light it holds. It is the same texture doing a different job: not light bent by an
    /// uneven surface, but an uneven surface catching more light in some places than others.
    ///
    /// The lattice periods divide the tile exactly, so the field wraps and the tile has no seam.
    /// </summary>
    public static ImageBrush Ripple()
    {
        if (_ripple is not null)
        {
            return _ripple;
        }

        // Two octaves: the reference's baseFrequency of 0.008 is a wavelength of about 125
        // pixels, which is the tile halved, and the second octave is that again.
        var field = new double[RippleSize * RippleSize];
        var amplitude = 1.0;
        var total = 0.0;

        for (var octave = 0; octave < 2; octave++)
        {
            var cells = 2 << octave;
            Accumulate(field, cells, amplitude, RippleSeed + octave);
            total += amplitude;
            amplitude /= 2;
        }

        var pixels = new byte[RippleSize * RippleSize * 4];

        for (var index = 0; index < field.Length; index++)
        {
            // The field runs zero to one; the surface is white where the glass is thin and
            // transparent where it is not, so the texture only ever adds light.
            var level = Math.Clamp(field[index] / total, 0, 1);

            pixels[index * 4] = 0xFF;
            pixels[index * 4 + 1] = 0xFF;
            pixels[index * 4 + 2] = 0xFF;
            pixels[index * 4 + 3] = (byte)Math.Round(level * RippleStrength);
        }

        var bitmap = BitmapSource.Create(
            RippleSize, RippleSize, 96, 96, PixelFormats.Bgra32, null, pixels, RippleSize * 4);

        bitmap.Freeze();

        var brush = new ImageBrush(bitmap)
        {
            TileMode = TileMode.Tile,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, RippleSize, RippleSize),
            Stretch = Stretch.None
        };

        brush.Freeze();
        _ripple = brush;

        return brush;
    }

    /// <summary>Adds one octave of periodic value noise into the field.</summary>
    private static void Accumulate(double[] field, int cells, double amplitude, int seed)
    {
        var random = new Random(seed);
        var lattice = new double[cells * cells];

        for (var index = 0; index < lattice.Length; index++)
        {
            lattice[index] = random.NextDouble();
        }

        var step = (double)RippleSize / cells;

        for (var y = 0; y < RippleSize; y++)
        {
            for (var x = 0; x < RippleSize; x++)
            {
                var fx = x / step;
                var fy = y / step;

                var x0 = (int)fx;
                var y0 = (int)fy;

                // Wrapping the far edge back onto the near one is what makes the tile seamless.
                var x1 = (x0 + 1) % cells;
                var y1 = (y0 + 1) % cells;

                var tx = Smooth(fx - x0);
                var ty = Smooth(fy - y0);

                var top = Lerp(lattice[y0 * cells + x0], lattice[y0 * cells + x1], tx);
                var bottom = Lerp(lattice[y1 * cells + x0], lattice[y1 * cells + x1], tx);

                field[y * RippleSize + x] += Lerp(top, bottom, ty) * amplitude;
            }
        }
    }

    private static double Smooth(double t) => t * t * (3 - 2 * t);

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    /// <summary>Installs both tiles over the transparent placeholders the dictionary declares.</summary>
    public static void Install(ResourceDictionary resources)
    {
        resources[Key] = Brush();
        resources[RippleKey] = Ripple();
    }
}
