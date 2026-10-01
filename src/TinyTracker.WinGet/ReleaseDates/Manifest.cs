using System.Globalization;
using System.Text.RegularExpressions;

namespace TinyTracker.WinGet.ReleaseDates;

// Where a version's installer manifest lives in microsoft/winget-pkgs, and its ReleaseDate.
public static partial class Manifest
{
    // winget's id grammar: 2 to 8 dot-separated parts, no spaces or path characters.
    [GeneratedRegex(@"^[^\.\s\\/:\*\?""<>\|\x01-\x1f]{1,32}(\.[^\.\s\\/:\*\?""<>\|\x01-\x1f]{1,32}){1,7}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^[^\\/:\*\?""<>\|\x01-\x1f]{1,128}$")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"^[ \t-]*ReleaseDate:[ \t]*[""']?(\d{4}-\d{2}-\d{2})[""']?[ \t]*(#.*)?\r?$", RegexOptions.Multiline)]
    private static partial Regex ReleaseDateLine();

    // Relative to the repository's manifests folder, or null when the id or version isn't valid.
    public static string? InstallerPath(string id, string version)
    {
        if (id.Length > 128 || !IdPattern().IsMatch(id) || !VersionPattern().IsMatch(version) || version.Trim('.').Length == 0) return null;
        var first = Uri.EscapeDataString(char.ToLowerInvariant(id[0]).ToString());
        var folders = string.Join('/', id.Split('.').Select(Uri.EscapeDataString));
        return $"{first}/{folders}/{Uri.EscapeDataString(version)}/{Uri.EscapeDataString(id)}.installer.yaml";
    }

    public static DateOnly? ReleaseDate(string yaml)
    {
        var match = ReleaseDateLine().Match(yaml);
        return match.Success && DateOnly.TryParseExact(match.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }
}
