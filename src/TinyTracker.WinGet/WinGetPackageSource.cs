using TinyTracker.Core.Checking;
using TinyTracker.Core.Tracking;
using TinyTracker.Core.Versions;

namespace TinyTracker.WinGet;

// Reads tracked apps from winget: the full installed list first, then id lookups for the apps it missed.
public sealed class WinGetPackageSource(Func<CancellationToken, Task<IWinGetQueries>> open) : IPackageSource
{
    public async Task<CatalogRead> ReadAsync(IReadOnlyList<TrackedApp> apps, CancellationToken ct)
    {
        // Only the winget catalog is used, so apps from other sources can't be found.
        var gone = apps.Where(a => !IsWinGet(a)).Select(a => new PackageKey(a.Id, a.Source)).ToList();
        var wanted = apps.Where(IsWinGet).ToList();
        if (wanted.Count == 0) return new CatalogRead([], gone);

        var queries = await open(ct);
        // Only the tracked apps' updates need their notes and elevation.
        var details = wanted.Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var list = await queries.ListInstalledAsync(details, ct);
        var found = Pick(wanted, list);
        var missing = wanted.Where(a => !found.ContainsKey(a.Id)).ToList();
        if (missing.Count > 0)
        {
            foreach (var (id, package) in Pick(missing, await queries.FindInstalledByIdAsync(Ids(missing), details: true, ct))) found[id] = package;
            missing = wanted.Where(a => !found.ContainsKey(a.Id)).ToList();
        }
        if (missing.Count > 0)
        {
            var known = (await queries.FindCatalogByIdAsync(Ids(missing), ct)).Select(e => e.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            gone.AddRange(missing.Where(a => !known.Contains(a.Id)).Select(a => new PackageKey(a.Id, a.Source)));
        }
        return new CatalogRead([.. found.Values.Select(Snapshot)], gone)
        {
            // As Choose apps lists them.
            Listed = [.. list.Where(p => p.CatalogId is not null && !PackageVersion.Parse(p.Version).IsUnknown).Select(p => new ListedApp(p.CatalogId!, p.Name, p.LocalId))],
        };
    }

    // Restore's question (spec §4.5): the same list and id lookups, with no notes, elevation or catalog lookups.
    public async Task<CatalogRead> ReadInstalledAsync(IReadOnlyList<TrackedApp> apps, CancellationToken ct)
    {
        var wanted = apps.Where(IsWinGet).ToList();
        if (wanted.Count == 0) return new CatalogRead([], []);
        var queries = await open(ct);
        var found = Pick(wanted, await queries.ListInstalledAsync(new HashSet<string>(), ct));
        var missing = wanted.Where(a => !found.ContainsKey(a.Id)).ToList();
        if (missing.Count > 0)
            foreach (var (id, package) in Pick(missing, await queries.FindInstalledByIdAsync(Ids(missing), details: false, ct))) found[id] = package;
        return new CatalogRead([.. found.Values.Select(Snapshot)], []);
    }

    private static bool IsWinGet(TrackedApp app) => string.Equals(app.Source, TrackedApp.WinGet, StringComparison.OrdinalIgnoreCase);

    private static List<string> Ids(IEnumerable<TrackedApp> apps) => apps.Select(a => a.Id).ToList();

    // One package per tracked app; when an id is installed twice, the one with an update.
    private static Dictionary<string, InstalledPackage> Pick(IReadOnlyList<TrackedApp> apps, IReadOnlyList<InstalledPackage> packages)
    {
        var picked = new Dictionary<string, InstalledPackage>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in apps)
        {
            var matches = packages.Where(p => string.Equals(p.CatalogId, app.Id, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count > 0) picked[app.Id] = matches.FirstOrDefault(p => p.UpdateAvailable) ?? matches[0];
        }
        return picked;
    }

    private static PackageSnapshot Snapshot(InstalledPackage p) =>
        new(p.CatalogId!, TrackedApp.WinGet, p.Name, p.Version, p.UpdateAvailable ? p.LatestVersion : null, p.Publisher, p.ReleaseNotesUrl, p.LocalId, p.Scope, p.Elevation,
            p.ReleaseNotes);
}
