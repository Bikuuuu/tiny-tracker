namespace TinyTracker.App.Interop;

internal static class Dwm
{
    private const int DWMWA_CLOAK = 13;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    public static void SetCloaked(nint hwnd, bool cloaked)
    {
        var value = cloaked ? 1 : 0;
        NativeMethods.DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, ref value, sizeof(int));
    }

    public static void SetRoundedCorners(nint hwnd)
    {
        var value = DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref value, sizeof(int));
    }
}
