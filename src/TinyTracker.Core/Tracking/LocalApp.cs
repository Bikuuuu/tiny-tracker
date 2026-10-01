using System.Globalization;

namespace TinyTracker.Core.Tracking;

// Where winget found an installed app, from its local id:
// ARP\<Machine|User>\<architecture>\<uninstall key>, or MSIX\<package full name>.
public abstract record LocalApp
{
    public static LocalApp? Parse(string? localId)
    {
        var parts = (localId ?? "").Split('\\', 4);
        if (parts is ["MSIX" or "msix", { Length: > 0 } fullName]) return new MsixPackage(fullName);
        if (parts is not [var arp, var scope, var architecture, { Length: > 0 } key] || !arp.Equals("ARP", StringComparison.OrdinalIgnoreCase)) return null;
        var user = scope.Equals("User", StringComparison.OrdinalIgnoreCase);
        if (!user && !scope.Equals("Machine", StringComparison.OrdinalIgnoreCase)) return null;
        return new UninstallEntry(user, !user && architecture.Equals("X86", StringComparison.OrdinalIgnoreCase), key);
    }

    // An uninstall entry's DisplayIcon: a path, maybe quoted, maybe followed by ",index".
    public static (string Path, int Index)? DisplayIcon(string? value)
    {
        var text = value?.Trim() ?? "";
        var index = 0;
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            if (end < 0) return null;
            var rest = text[(end + 1)..].Trim();
            text = text[1..end];
            if (rest.StartsWith(',') && int.TryParse(rest[1..], NumberStyles.AllowLeadingSign | NumberStyles.AllowLeadingWhite, CultureInfo.InvariantCulture, out var quoted)) index = quoted;
        }
        else
        {
            var comma = text.LastIndexOf(',');
            if (comma > 0 && int.TryParse(text[(comma + 1)..], NumberStyles.AllowLeadingSign | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, CultureInfo.InvariantCulture, out var plain))
            {
                index = plain;
                text = text[..comma];
            }
        }
        text = text.Trim();
        return text.Length == 0 ? null : (text, index);
    }
}

// A Windows uninstall entry: in HKCU for per-user apps, else in HKLM, in its 32-bit view for x86 apps.
public sealed record UninstallEntry(bool PerUser, bool Wow64, string KeyName) : LocalApp
{
    public string KeyPath => @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + KeyName;
}

// An MSIX package, by its full name.
public sealed record MsixPackage(string FullName) : LocalApp;
