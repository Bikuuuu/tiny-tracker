using TinyTracker.Core.Versions;

namespace TinyTracker.WinGet.Matching;

// Which lookup matches are safe to offer. Targeted lookups correlate loosely,
// and a wrong match could install another edition or track over the app.
public static class Matcher
{
    public static IReadOnlyDictionary<string, InstalledPackage> Accept(
        IReadOnlyList<InstalledPackage> unmatched,
        IReadOnlySet<string> listedIds,
        IReadOnlyList<InstalledPackage> found)
    {
        var listed = new HashSet<string>(listedIds, StringComparer.OrdinalIgnoreCase);
        var entries = unmatched
            .GroupBy(p => p.LocalId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        // Closeness is judged before listed ids are set aside, so a listed closer track blocks a farther one.
        var closest = found
            .Where(f => !string.IsNullOrWhiteSpace(f.CatalogId) && !string.IsNullOrWhiteSpace(f.CatalogName))
            .Where(f => entries.TryGetValue(f.LocalId, out var entry) && Fits(entry, f))
            .DistinctBy(f => (f.LocalId.ToUpperInvariant(), f.CatalogId!.ToUpperInvariant()))
            .GroupBy(f => f.LocalId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => Closest(entries[g.Key], [.. g]), StringComparer.OrdinalIgnoreCase);
        // An id among the closest fits of two installed apps is ambiguous for both.
        var claims = closest.Values
            .SelectMany(fits => fits)
            .GroupBy(f => f.CatalogId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        return closest.Values
            .Where(fits => fits.Count == 1)
            .Select(fits => fits[0])
            .Where(f => !listed.Contains(f.CatalogId!) && claims[f.CatalogId!] == 1)
            .ToDictionary(f => f.LocalId, StringComparer.OrdinalIgnoreCase);
    }

    // Same name, numbers that agree, a known version on the same major track, and not older than the installed one.
    private static bool Fits(InstalledPackage entry, InstalledPackage match)
    {
        var installed = PackageVersion.Parse(entry.Version);
        var latest = PackageVersion.Parse(match.LatestVersion);
        var name = NameKey.Of(entry.Name);
        return !installed.IsUnknown && !latest.IsUnknown && latest.CompareTo(installed) >= 0
            && SharedParts(match.LatestVersion!, entry.Version) >= 1
            && name.Length > 0 && name == NameKey.Of(match.CatalogName)
            && NumbersAgree(match.CatalogName!, entry);
    }

    // A number in the catalog's name, such as a track ("3.13") or a year, must be in the installed name or lead its version.
    private static bool NumbersAgree(string catalogName, InstalledPackage entry)
    {
        var named = NameKey.Numbers(entry.Name);
        var version = NameKey.Numbers(entry.Version).FirstOrDefault()?.Split('.') ?? [];
        return NameKey.Numbers(catalogName).All(n => named.Contains(n) || Leads(n.Split('.'), version));
    }

    private static bool Leads(string[] parts, string[] version) =>
        parts.Length <= version.Length && parts.Select((part, i) => part == version[i]).All(same => same);

    // The fits whose version shares the most leading parts with the installed one.
    private static List<InstalledPackage> Closest(InstalledPackage entry, IReadOnlyList<InstalledPackage> fits)
    {
        var scored = fits.Select(f => (Match: f, Score: SharedParts(f.LatestVersion!, entry.Version))).ToList();
        var top = scored.Max(s => s.Score);
        return scored.Where(s => s.Score == top).Select(s => s.Match).ToList();
    }

    private static int SharedParts(string a, string b)
    {
        var left = a.Split('.');
        var right = b.Split('.');
        var shared = 0;
        while (shared < left.Length && shared < right.Length && Part(left[shared]) == Part(right[shared])) shared++;
        return shared;
    }

    private static string Part(string part)
    {
        var trimmed = part.Trim().TrimStart('0');
        return (trimmed.Length == 0 ? "0" : trimmed).ToUpperInvariant();
    }
}
