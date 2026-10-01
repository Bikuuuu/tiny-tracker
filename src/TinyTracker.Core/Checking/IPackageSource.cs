using TinyTracker.Core.Tracking;

namespace TinyTracker.Core.Checking;

// An installed app that winget's full list matched to the catalog. LocalId: its uninstall entry.
public sealed record ListedApp(string Id, string Name, string LocalId);

// Installed: the tracked apps found installed. NotInCatalog: tracked apps the catalog no longer has.
public sealed record CatalogRead(IReadOnlyList<PackageSnapshot> Installed, IReadOnlyList<PackageKey> NotInCatalog)
{
    // Every app in the full list, for new apps (spec §4.3); empty when nothing was listed.
    public IReadOnlyList<ListedApp> Listed { get; init; } = [];
}

// Reads the tracked apps from winget. Throws PackageSourceException when winget can't answer.
public interface IPackageSource
{
    Task<CatalogRead> ReadAsync(IReadOnlyList<TrackedApp> apps, CancellationToken ct);

    // Only which of the apps are installed, for Restore (spec §4.5): a source may leave out notes, elevation and the catalog.
    Task<CatalogRead> ReadInstalledAsync(IReadOnlyList<TrackedApp> apps, CancellationToken ct) => ReadAsync(apps, ct);
}
