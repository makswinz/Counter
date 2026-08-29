using FocusNotch.App.Interop;

namespace FocusNotch.App.Services;

/// <summary>A physical display, in device pixels, with its own effective DPI scale.</summary>
public sealed record MonitorInfo(
    string DeviceName,
    string DisplayName,
    int Left,
    int Top,
    int Width,
    int Height,
    int WorkLeft,
    int WorkTop,
    int WorkWidth,
    int WorkHeight,
    bool IsPrimary,
    double Scale);

/// <summary>Enumerates displays and resolves the one the notch should anchor to.</summary>
public static class MonitorService
{
    private const uint MONITORINFOF_PRIMARY = 0x00000001;

    public static IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var monitors = new List<MonitorInfo>();
        var index = 0;

        bool Callback(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data)
        {
            var info = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
            if (!NativeMethods.GetMonitorInfo(hMonitor, ref info))
            {
                return true;
            }

            index++;
            var isPrimary = (info.dwFlags & MONITORINFOF_PRIMARY) != 0;
            var scale = GetScale(hMonitor);
            var label = "Display " + index + " (" + info.rcMonitor.Width + " x " + info.rcMonitor.Height + ")";
            if (isPrimary)
            {
                label += " - primary";
            }

            monitors.Add(new MonitorInfo(
                info.szDevice ?? string.Empty,
                label,
                info.rcMonitor.Left,
                info.rcMonitor.Top,
                info.rcMonitor.Width,
                info.rcMonitor.Height,
                info.rcWork.Left,
                info.rcWork.Top,
                info.rcWork.Width,
                info.rcWork.Height,
                isPrimary,
                scale));

            return true;
        }

        try
        {
            NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Callback, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not enumerate displays.", ex);
        }

        if (monitors.Count == 0)
        {
            // Fall back to a single virtual display so the window still lands somewhere sane.
            monitors.Add(new MonitorInfo(
                "\\\\.\\DISPLAY1", "Primary display", 0, 0, 1920, 1080, 0, 0, 1920, 1040, true, 1d));
        }

        return monitors;
    }

    /// <summary>
    /// Resolves the saved monitor, falling back to the primary display when that monitor has
    /// been disconnected or its device name no longer matches anything attached.
    /// </summary>
    public static MonitorInfo Resolve(string? preferredDeviceName)
    {
        var monitors = GetMonitors();

        if (!string.IsNullOrEmpty(preferredDeviceName))
        {
            var match = monitors.FirstOrDefault(m =>
                string.Equals(m.DeviceName, preferredDeviceName, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }

            Log.Info("Saved display " + preferredDeviceName + " is not attached; using the primary display.");
        }

        return monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
    }

    private static double GetScale(IntPtr hMonitor)
    {
        try
        {
            if (NativeMethods.GetDpiForMonitor(hMonitor, MonitorDpiType.Effective, out var dpiX, out _) == 0
                && dpiX > 0)
            {
                return dpiX / 96d;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read the monitor DPI; assuming 100 percent.", ex);
        }

        return 1d;
    }
}
