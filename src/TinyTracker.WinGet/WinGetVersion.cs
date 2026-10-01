using TinyTracker.Core.Versions;

namespace TinyTracker.WinGet;

// winget 1.29.280 has a security fix; anything older gets the "winget needs an update" banner.
public static class WinGetVersion
{
    public const string Minimum = "1.29.280";

    public static bool IsSupported(string? version)
    {
        var parsed = PackageVersion.Parse(version?.Trim().TrimStart('v', 'V'));
        return !parsed.IsUnknown && parsed.CompareTo(PackageVersion.Parse(Minimum)) >= 0;
    }
}
