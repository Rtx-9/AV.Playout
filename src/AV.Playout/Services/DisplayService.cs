using System.Runtime.InteropServices;
using AV.Playout.Interop;

namespace AV.Playout.Services;

/// <summary>A physical display. Coordinates are physical pixels on the virtual desktop.</summary>
public sealed record DisplayInfo(IntPtr HMonitor, string DeviceName, int X, int Y, int Width, int Height, bool IsPrimary)
{
    public override string ToString() =>
        $"{DeviceName}  {Width}×{Height} @ {X},{Y}{(IsPrimary ? "  (primary)" : "")}";
}

public static class DisplayService
{
    /// <summary>All attached displays, primary first, then left to right.</summary>
    public static IReadOnlyList<DisplayInfo> GetDisplays()
    {
        var displays = new List<DisplayInfo>();

        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr hMonitor, IntPtr hdc, ref NativeMethods.RECT bounds, IntPtr data) =>
            {
                var info = new NativeMethods.MONITORINFOEX { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
                if (NativeMethods.GetMonitorInfo(hMonitor, ref info))
                {
                    var r = info.rcMonitor;
                    displays.Add(new DisplayInfo(
                        hMonitor,
                        info.szDevice.TrimStart('\\', '.'),   // "\\.\DISPLAY2" -> "DISPLAY2"
                        r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top,
                        (info.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0));
                }
                return true;
            },
            IntPtr.Zero);

        return displays
            .OrderByDescending(d => d.IsPrimary)
            .ThenBy(d => d.X)
            .ThenBy(d => d.Y)
            .ToList();
    }

    public static IntPtr MonitorOf(IntPtr hwnd) =>
        NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
}
