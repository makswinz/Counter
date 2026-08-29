using System.IO;
using System.Windows.Media.Imaging;
using FocusNotch.App.Services;
using Xunit;

namespace FocusNotch.Tests;

/// <summary>
/// The application's mark, and the one file that is generated from it.
///
/// The icon on the taskbar is a committed binary, which means it is the one thing in the project
/// that can silently stop matching the code that produced it: change the drawing, forget to
/// regenerate, and the tray shows one mark while the Start menu shows another. So the committed
/// file is compared with what the drawing produces now, and the build fails rather than the
/// mismatch shipping.
/// </summary>
public class BrandingTests
{
    private static string IconPath() =>
        Path.Combine(RepositoryRoot(), "Assets", "FocusNotch.ico");

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FocusNotch.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    [Fact]
    public void The_committed_icon_is_what_the_drawing_produces()
    {
        // Byte for byte. If this fails the drawing changed and the icon was not regenerated:
        //   dotnet run --project src/FocusNotch.App -- --write-icon Assets/FocusNotch.ico
        Assert.True(File.Exists(IconPath()), "The application icon is missing from Assets.");

        Assert.Equal(
            Convert.ToHexString(Branding.IconBytes()),
            Convert.ToHexString(File.ReadAllBytes(IconPath())));
    }

    [Fact]
    public void The_icon_carries_every_size_windows_asks_for()
    {
        // Sixteen for the taskbar and the tray, two hundred and fifty six for the Start tile and
        // the large view in Explorer. Missing a size does not fail, it makes Windows scale the
        // nearest one, which is how an icon ends up looking soft in exactly one place.
        var bytes = File.ReadAllBytes(IconPath());

        Assert.Equal(1, BitConverter.ToUInt16(bytes, 2));

        var frames = BitConverter.ToUInt16(bytes, 4);
        Assert.Equal(Branding.IconSizes.Length, frames);

        for (var index = 0; index < frames; index++)
        {
            var entry = 6 + (16 * index);
            var declared = bytes[entry] == 0 ? 256 : bytes[entry];

            Assert.Equal(Branding.IconSizes[index], declared);
            Assert.Equal(declared, bytes[entry + 1] == 0 ? 256 : bytes[entry + 1]);
            Assert.Equal(32, BitConverter.ToUInt16(bytes, entry + 6));

            // Each frame is a PNG, which is what keeps a 256 pixel icon to kilobytes.
            var offset = BitConverter.ToInt32(bytes, entry + 12);
            Assert.Equal(0x89, bytes[offset]);
            Assert.Equal((byte)'P', bytes[offset + 1]);
        }
    }

    [Fact]
    public void Every_frame_is_resampled_from_the_full_artwork()
    {
        // Not from the frame above it. Nine successive halvings of a 1024 pixel image is not the
        // same as one careful downscale to sixteen, and the difference is exactly the size
        // people see most. The check is for the halo the resampler leaves when it reads past the
        // edge of the source: the outermost ring of a small frame should be as opaque as the
        // middle, because the artwork is full-bleed.
        Sta.Run(() =>
        {
            using var small = Branding.Render(16);

            Assert.Equal(255, small.GetPixel(0, 0).A);
            Assert.Equal(255, small.GetPixel(15, 15).A);
            Assert.Equal(255, small.GetPixel(8, 8).A);

            // And it is the logo rather than a blank square: the marks on it are much lighter
            // than the ground behind them.
            using var large = Branding.Render(256);

            var corner = large.GetPixel(8, 8);
            var mark = large.GetPixel(70, 70);

            Assert.True(
                mark.R + mark.G + mark.B > corner.R + corner.G + corner.B,
                "the rendered mark is not lighter than the ground it sits on");
        });
    }

    [Fact]
    public void The_icon_loads_as_an_image()
    {
        // The final word: whatever the bytes say, the platform has to be able to decode it.
        Sta.Run(() =>
        {
            var decoder = new IconBitmapDecoder(
                new Uri(IconPath()),
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);

            Assert.Equal(Branding.IconSizes.Length, decoder.Frames.Count);
        });
    }
}
