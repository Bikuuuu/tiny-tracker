using System.Runtime.InteropServices;
using TinyTracker.Core.Layout;

namespace TinyTracker.App.Interop;

internal static class Screens
{
    public static (PixelRect Work, uint Dpi) TaskbarMonitor()
    {
        var taskbar = NativeMethods.FindWindowW("Shell_TrayWnd", null);
        var monitor = NativeMethods.MonitorFromWindow(taskbar, NativeMethods.MONITOR_DEFAULTTOPRIMARY);
        var info = new NativeMethods.MONITORINFO { cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        NativeMethods.GetMonitorInfoW(monitor, ref info);
        if (NativeMethods.GetDpiForMonitor(monitor, 0, out var dpi, out _) != 0 || dpi == 0) dpi = 96;
        var r = info.rcWork;
        return (new PixelRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top), dpi);
    }
}
