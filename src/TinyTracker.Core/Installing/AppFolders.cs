using TinyTracker.Core.Tracking;

namespace TinyTracker.Core.Installing;

// Where Close & update looks for an app's processes (spec §6.3), given this PC's folders: never a system folder or inside one,
// never a shared one itself, though its subfolders may do; an icon in programData is an installer's cache. own is Tiny Tracker's.
public sealed class AppFolders(IEnumerable<string> system, IEnumerable<string> shared, string programData, string own)
{
    private readonly List<string> _system = [.. system.Select(Clean).OfType<string>()];
    private readonly List<string> _shared = [.. shared.Select(Clean).OfType<string>()];
    private readonly string? _programData = Clean(programData);
    private readonly string? _own = Clean(own);

    // The install location, else the folder of the uninstall entry's DisplayIcon unless that's inside ProgramData.
    // Null when neither is usable.
    public string? Choose(string? installLocation, string? displayIcon)
    {
        if (Usable(Clean(installLocation)) is { } folder) return folder;
        if (LocalApp.DisplayIcon(displayIcon) is not (var path, _) || Clean(path) is not { } icon || Path.GetDirectoryName(icon) is not { } iconFolder
            || Inside(_programData, iconFolder)) return null;
        return Usable(iconFolder);
    }

    // The folder itself, or anything inside it.
    public static bool Holds(string folder, string path) =>
        Clean(folder) is { } outer && Clean(path) is { } inner && Inside(outer, inner);

    // Right in the folder, not deeper.
    public static bool DirectlyIn(string folder, string path) =>
        Clean(folder) is { } outer && Clean(path) is { } inner && Same(outer, Path.GetDirectoryName(inner));

    private string? Usable(string? folder)
    {
        if (folder is null || string.Equals(Path.GetPathRoot(folder), folder, StringComparison.OrdinalIgnoreCase) || _system.Any(s => Inside(s, folder))) return null;
        if (_shared.Any(s => Same(s, folder))) return null;
        // Never a folder that holds Tiny Tracker, or one inside it.
        if (Inside(folder, _own) || Inside(_own, folder)) return null;
        return folder;
    }

    // A full path with no quotes, no ".." and no trailing separator; null when it isn't one.
    private static string? Clean(string? path)
    {
        var text = path is null ? null : Environment.ExpandEnvironmentVariables(path).Trim().Trim('"').Trim();
        if (string.IsNullOrEmpty(text) || !Path.IsPathFullyQualified(text)) return null;
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(text));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool Same(string? a, string? b) => a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool Inside(string? outer, string? inner) =>
        outer is not null && inner is not null
        && (Same(outer, inner) || inner.StartsWith(outer.EndsWith(Path.DirectorySeparatorChar) ? outer : outer + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
}
