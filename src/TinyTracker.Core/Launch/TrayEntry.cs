namespace TinyTracker.Core.Launch;

// Windows' record of a tray icon (HKCU\Control Panel\NotifyIconSettings) names its program by path, with a known folder written
// as its id, such as {6D809377-6AF0-444B-8957-A3773F02200E}\Tiny Tracker\TinyTracker.exe for Program Files.
public static class TrayEntry
{
    public static bool IsFor(string recorded, string exe, Func<Guid, string?> knownFolder)
    {
        var path = recorded;
        if (path.StartsWith('{'))
        {
            var end = path.IndexOf('}');
            if (end < 0 || !Guid.TryParse(path[..(end + 1)], out var id) || knownFolder(id) is not { } folder) return false;
            path = folder + path[(end + 1)..];
        }
        return path.Length > 0 && string.Equals(path, exe, StringComparison.OrdinalIgnoreCase);
    }
}
