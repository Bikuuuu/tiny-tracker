namespace TinyTracker.App.Interop;

// The Windows (taskbar) mode, which tray flyouts and menus follow instead of the app mode (spec §4.1).
internal static class WindowsMode
{
    private const int Default = 0, ForceDark = 2, ForceLight = 3;

    public static bool IsLight()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
    }

    // Before a menu opens. High contrast keeps Windows' own colors; a Windows without the switch keeps menus light.
    public static void ApplyToMenus()
    {
        var mode = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast ? Default : IsLight() ? ForceLight : ForceDark;
        try
        {
            NativeMethods.SetPreferredAppMode(mode);
            NativeMethods.FlushMenuThemes();
        }
        catch (EntryPointNotFoundException)
        {
        }
    }
}
