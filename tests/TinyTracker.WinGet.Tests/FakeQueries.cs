using TinyTracker.Core.Tracking;

namespace TinyTracker.WinGet.Tests;

// Canned winget answers, with a log of the questions asked.
internal sealed class FakeQueries : IWinGetQueries
{
    public List<InstalledPackage> Listed { get; } = [];
    public List<InstalledPackage> ById { get; } = [];
    public List<(string Name, InstalledPackage Result)> ByName { get; } = [];
    public List<CatalogEntry> Catalog { get; } = [];
    public List<string> Asked { get; } = [];

    public static InstalledPackage Matched(string id, string name, string version, string? latest = null, bool update = false, string? localId = null,
        InstallScope scope = InstallScope.Machine, InstallerElevation elevation = InstallerElevation.Unknown, string? notes = null) =>
        new(localId ?? $@"ARP\Machine\X64\{id}", name, "Example Publisher", version, id, name, latest ?? version, update, update ? "https://example.com/notes" : null, scope, elevation,
            update ? notes : null);

    public Task<IReadOnlyList<InstalledPackage>> ListInstalledAsync(IReadOnlySet<string> details, CancellationToken ct) =>
        Answer("list" + (details.Count == 0 ? "" : $" with details {string.Join(',', details.Order(StringComparer.OrdinalIgnoreCase))}"),
            Listed.Select(p => Detailed(p, p.CatalogId is { } id && details.Contains(id))));

    public Task<IReadOnlyList<InstalledPackage>> FindInstalledByIdAsync(IReadOnlyCollection<string> ids, bool details, CancellationToken ct) =>
        Answer($"installed ids {string.Join(',', ids)}" + (details ? " with details" : ""),
            ById.Where(p => ids.Contains(p.CatalogId!, StringComparer.OrdinalIgnoreCase)).Select(p => Detailed(p, details)));

    // As winget answers: the installed entry of that name comes back too, matched to the catalog or not.
    public Task<IReadOnlyList<InstalledPackage>> FindInstalledByNameAsync(IReadOnlyCollection<string> names, CancellationToken ct) =>
        Answer($"installed names {string.Join(',', names)}", ByName.Where(n => names.Contains(n.Name, StringComparer.OrdinalIgnoreCase)).Select(n => n.Result)
            .Concat(Listed.Where(p => names.Contains(p.Name, StringComparer.OrdinalIgnoreCase) && !ByName.Any(n => n.Result.LocalId == p.LocalId))));

    public Task<IReadOnlyList<CatalogEntry>> FindCatalogByIdAsync(IReadOnlyCollection<string> ids, CancellationToken ct) =>
        Answer($"catalog ids {string.Join(',', ids)}", Catalog.Where(e => ids.Contains(e.Id, StringComparer.OrdinalIgnoreCase)));

    public Task<IReadOnlyList<CatalogEntry>> SearchCatalogByNameAsync(IReadOnlyCollection<string> names, CancellationToken ct) =>
        Answer($"catalog search {string.Join(',', names)}", Catalog.Where(e => names.Any(n => e.Name.Contains(n, StringComparison.OrdinalIgnoreCase))));

    // As winget answers: an update's notes and elevation only when asked for.
    private static InstalledPackage Detailed(InstalledPackage package, bool asked) =>
        asked ? package : package with { ReleaseNotesUrl = null, ReleaseNotes = null, Elevation = InstallerElevation.Unknown };

    private Task<IReadOnlyList<T>> Answer<T>(string question, IEnumerable<T> answer)
    {
        lock (Asked) Asked.Add(question);
        return Task.FromResult<IReadOnlyList<T>>([.. answer]);
    }
}
