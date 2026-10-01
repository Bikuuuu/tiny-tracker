using TinyTracker.Core.Tracking;

namespace TinyTracker.WinGet;

// An installed package as winget lists it: CatalogId is null when unmatched, and Name the catalog's when matched, else the
// installed one. Release notes and elevation are read only for an update whose details were asked for.
public sealed record InstalledPackage(
    string LocalId,
    string Name,
    string Publisher,
    string Version,
    string? CatalogId = null,
    string? CatalogName = null,
    string? LatestVersion = null,
    bool UpdateAvailable = false,
    string? ReleaseNotesUrl = null,
    InstallScope Scope = InstallScope.Unknown,
    InstallerElevation Elevation = InstallerElevation.Unknown,
    string? ReleaseNotes = null);

// A package in the winget catalog.
public sealed record CatalogEntry(string Id, string Name, string LatestVersion);

// Read-only winget queries. Each throws PackageSourceException when winget can't answer.
public interface IWinGetQueries
{
    // Every installed package, matched to the catalog or not, with the details of the updates to the catalog ids in details.
    Task<IReadOnlyList<InstalledPackage>> ListInstalledAsync(IReadOnlySet<string> details, CancellationToken ct);

    // Installed packages winget matches to these catalog ids. Looser than the full list.
    Task<IReadOnlyList<InstalledPackage>> FindInstalledByIdAsync(IReadOnlyCollection<string> ids, bool details, CancellationToken ct);

    // Installed packages matched to the catalog whose name equals one of these.
    Task<IReadOnlyList<InstalledPackage>> FindInstalledByNameAsync(IReadOnlyCollection<string> names, CancellationToken ct);

    // Catalog packages with these ids, installed or not.
    Task<IReadOnlyList<CatalogEntry>> FindCatalogByIdAsync(IReadOnlyCollection<string> ids, CancellationToken ct);

    // Catalog packages whose name contains one of these.
    Task<IReadOnlyList<CatalogEntry>> SearchCatalogByNameAsync(IReadOnlyCollection<string> names, CancellationToken ct);
}
