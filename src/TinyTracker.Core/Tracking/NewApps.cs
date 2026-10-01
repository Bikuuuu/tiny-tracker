using TinyTracker.Core.Checking;

namespace TinyTracker.Core.Tracking;

// Apps installed after the first check that were never tracked or turned down (spec §4.3).
public static class NewApps
{
    // known: the ids never offered, or null before the first check that listed apps.
    public static IReadOnlyList<ListedApp> Offered(IReadOnlyList<ListedApp> listed, IReadOnlyList<string>? known) =>
        known is null ? []
        : [.. listed.Where(a => !known.Contains(a.Id, StringComparer.OrdinalIgnoreCase) && !a.LocalId.EndsWith(@"\" + AppInfo.UninstallKey, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(a => a.Id.ToUpperInvariant())];
}
