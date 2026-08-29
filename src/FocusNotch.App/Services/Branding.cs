using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;

namespace FocusNotch.App.Services;

/// <summary>
/// The application's mark, and the one place it comes from.
///
/// The artwork is `Assets/logo.png`, compiled into the assembly, so the icon in the tray, the one
/// on the taskbar, the one in the Start menu and the one the installer stamps on its own window
/// are all the same image rather than four that happen to look alike until somebody edits one.
/// An application whose taskbar icon does not match its tray icon looks like two applications.
///
/// The source of the artwork is `Assets/logo.svg`, which is what to edit; export it to
/// `Assets/logo.png` at 1024 and run `tools/New-AppIcon.ps1` to rebuild the icon. A test compares
/// the committed icon against what this code produces and fails the build if they have drifted.
/// </summary>
public static class Branding
{
    /// <summary>The sizes Windows actually asks for, from the tray up to the Start tile.</summary>
    public static readonly int[] IconSizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };

    /// <summary>The compiled name of the artwork. Set explicitly so no build layout can move it.</summary>
    private const string ResourceName = "FocusNotch.Logo.png";

    private static readonly object Gate = new();
    private static Bitmap? _source;

    /// <summary>The artwork at full size, decoded once and kept.</summary>
    private static Bitmap Source()
    {
        lock (Gate)
        {
            if (_source is not null)
            {
                return _source;
            }

            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
                ?? throw new InvalidOperationException(
                    "The application logo is missing from the assembly: " + ResourceName + ".");

            // Copied out of the stream rather than wrapping it: a Bitmap built straight from a
            // stream keeps that stream alive for as long as the bitmap, and disposing either one
            // first is a class of bug that only shows up under memory pressure.
            using var decoded = new Bitmap(stream);
            _source = new Bitmap(decoded);

            return _source;
        }
    }

    /// <summary>
    /// Renders the mark at one size.
    ///
    /// Every frame is resampled from the full-resolution artwork rather than from the frame above
    /// it, so a sixteen pixel icon is one careful downscale instead of nine successive ones. The
    /// wrap mode matters more than it looks: without it, the resampler reads past the edge of the
    /// source and leaves a pale halo around all four sides of every small frame.
    /// </summary>
    public static Bitmap Render(int size)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);

        using (var graphics = Graphics.FromImage(bitmap))
        using (var attributes = new ImageAttributes())
        {
            graphics.Clear(Color.Transparent);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            graphics.CompositingQuality = CompositingQuality.HighQuality;

            attributes.SetWrapMode(WrapMode.TileFlipXY);

            graphics.DrawImage(
                Source(),
                new Rectangle(0, 0, size, size),
                0, 0, Source().Width, Source().Height,
                GraphicsUnit.Pixel,
                attributes);
        }

        return bitmap;
    }

    /// <summary>
    /// Writes a Windows icon containing every size in <see cref="IconSizes"/>.
    ///
    /// The format is assembled by hand because .NET has no icon writer: a six byte directory, a
    /// sixteen byte entry per image, then the images themselves. Each frame is stored as a PNG,
    /// which Windows has understood since Vista and which keeps a 256 pixel icon to a few
    /// kilobytes instead of a quarter of a megabyte of uncompressed bitmap.
    /// </summary>
    public static void WriteIcon(Stream destination)
    {
        var frames = new List<byte[]>();

        foreach (var size in IconSizes)
        {
            using var bitmap = Render(size);
            using var buffer = new MemoryStream();
            bitmap.Save(buffer, ImageFormat.Png);
            frames.Add(buffer.ToArray());
        }

        using var writer = new BinaryWriter(destination, System.Text.Encoding.UTF8, leaveOpen: true);

        writer.Write((ushort)0);                 // reserved
        writer.Write((ushort)1);                 // 1 = icon, 2 = cursor
        writer.Write((ushort)frames.Count);

        // Every entry has to know where its image starts, so the offset runs from the end of the
        // directory rather than from the start of the file.
        var offset = 6 + (16 * frames.Count);

        for (var index = 0; index < frames.Count; index++)
        {
            var size = IconSizes[index];

            // Zero means 256 in this field, which is the whole reason the format tops out there.
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0);               // palette entries, zero for a true colour image
            writer.Write((byte)0);               // reserved
            writer.Write((ushort)1);             // colour planes
            writer.Write((ushort)32);            // bits per pixel
            writer.Write(frames[index].Length);
            writer.Write(offset);

            offset += frames[index].Length;
        }

        foreach (var frame in frames)
        {
            writer.Write(frame);
        }

        writer.Flush();
    }

    /// <summary>The icon bytes, which is what the test compares against the committed file.</summary>
    public static byte[] IconBytes()
    {
        using var buffer = new MemoryStream();
        WriteIcon(buffer);
        return buffer.ToArray();
    }
}
