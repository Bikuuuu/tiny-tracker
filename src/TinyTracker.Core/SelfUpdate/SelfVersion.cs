using System.Globalization;
using System.Text.RegularExpressions;

namespace TinyTracker.Core.SelfUpdate;

// Tiny Tracker's own version: plain numbers, as its release tags have them (spec §11).
public sealed partial record SelfVersion(int Major, int Minor, int Patch)
{
    // Null unless it's X.Y.Z, each a whole number without leading zeros.
    public static SelfVersion? Parse(string? text)
    {
        if (text is null || Plain().Match(text) is not { Success: true } match) return null;
        var parts = new int[3];
        for (var i = 0; i < 3; i++)
            if (!int.TryParse(match.Groups[i + 1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out parts[i])) return null;
        return new SelfVersion(parts[0], parts[1], parts[2]);
    }

    // A release tag, such as v0.2.0.
    public static SelfVersion? OfTag(string? tag) => tag is { Length: > 1 } && tag[0] == 'v' ? Parse(tag[1..]) : null;

    public string Tag => "v" + this;

    // For What's new.
    public string ReleasePage => $"{AppInfo.RepositoryUrl}/releases/tag/{Tag}";

    public bool IsNewerThan(SelfVersion other) => (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch)) > 0;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");

    [GeneratedRegex(@"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z")]
    private static partial Regex Plain();
}
