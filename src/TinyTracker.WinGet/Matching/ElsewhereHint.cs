using System.Text.RegularExpressions;
using TinyTracker.Core.Inventory;
using TinyTracker.Core.Versions;

namespace TinyTracker.WinGet.Matching;

// What updates an app winget can't, from winget's local id and the app's name and publisher.
public static partial class ElsewhereHint
{
    // Steam registers each game as a "Steam App <number>" uninstall entry.
    [GeneratedRegex(@"^ARP\\[^\\]+\\[^\\]+\\Steam App \d+$", RegexOptions.IgnoreCase)]
    private static partial Regex SteamGame();

    [GeneratedRegex(@"\bdrivers?\b", RegexOptions.IgnoreCase)]
    private static partial Regex Driver();

    public static UpdatedBy For(InstalledPackage package)
    {
        if (SteamGame().IsMatch(package.LocalId)) return UpdatedBy.Steam;
        if (package.LocalId.StartsWith(@"MSIX\", StringComparison.OrdinalIgnoreCase)) return UpdatedBy.MicrosoftStore;
        if (Driver().IsMatch(package.Name)) return UpdatedBy.DriverTool;
        if (IsMicrosoft(package.Publisher) && IsWindowsPart(package.Name)) return UpdatedBy.WindowsUpdate;
        return PackageVersion.Parse(package.Version).IsUnknown ? UpdatedBy.ItSelf : UpdatedBy.Unknown;
    }

    private static bool IsMicrosoft(string publisher) =>
        publisher.Equals("Microsoft", StringComparison.OrdinalIgnoreCase) || publisher.StartsWith("Microsoft ", StringComparison.OrdinalIgnoreCase);

    private static bool IsWindowsPart(string name) =>
        name.StartsWith("Windows ", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Microsoft Windows ", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Microsoft Update ", StringComparison.OrdinalIgnoreCase);
}
