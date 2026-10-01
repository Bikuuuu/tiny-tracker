using System.Runtime.InteropServices;
using Microsoft.Win32;
using TinyTracker.Core.Launch;

namespace TinyTracker.App.Interop;

// The record Windows keeps of this user's tray icon for this exe, which --cleanup removes (spec §10).
internal static class TrayEntries
{
    private const string Key = @"Control Panel\NotifyIconSettings";

    public static void Remove(string exe)
    {
        using var settings = Registry.CurrentUser.OpenSubKey(Key, writable: true);
        if (settings is null) return;
        foreach (var name in settings.GetSubKeyNames())
        {
            bool ours;
            using (var entry = settings.OpenSubKey(name)) ours = entry?.GetValue("ExecutablePath") is string recorded && TrayEntry.IsFor(recorded, exe, KnownFolder);
            if (ours) settings.DeleteSubKeyTree(name, throwOnMissingSubKey: false);
        }
    }

    // Windows asks for the text to be freed whether it succeeds or not.
    private static string? KnownFolder(Guid id)
    {
        var found = NativeMethods.SHGetKnownFolderPath(ref id, 0, 0, out var path);
        try
        {
            return found == 0 ? Marshal.PtrToStringUni(path) : null;
        }
        finally
        {
            Marshal.FreeCoTaskMem(path);
        }
    }
}
