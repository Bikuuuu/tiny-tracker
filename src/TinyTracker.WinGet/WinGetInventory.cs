using TinyTracker.Core.Inventory;
using TinyTracker.Core.Tracking;
using TinyTracker.Core.Versions;
using TinyTracker.WinGet.Matching;

namespace TinyTracker.WinGet;

// Every installed app for Choose apps: the ones winget can update, and the rest with what updates them.
// The full list misses some apps, so unmatched ones are looked up by name and by a catalog search.
public sealed class WinGetInventory(Func<CancellationToken, Task<IWinGetQueries>> open) : IAppInventory
{
    private const int MinSearchLength = 3;
    // Choose apps shows no notes and routes no update.
    private static readonly IReadOnlySet<string> NoDetails = new HashSet<string>();

    public async Task<AppInventory> ReadAsync(IProgress<AppInventory>? firstList, CancellationToken ct)
    {
        var queries = await open(ct);
        var listed = await queries.ListInstalledAsync(NoDetails, ct);
        var inventory = Build(listed, new Dictionary<string, InstalledPackage>(), new HashSet<string>());
        firstList?.Report(inventory);

        var listedIds = listed.Where(p => p.CatalogId is not null).Select(p => p.CatalogId!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unmatched = listed.Where(p => p.CatalogId is null).ToList();
        var lookable = unmatched.Where(Lookable).ToList();
        if (lookable.Count == 0) return inventory;

        var names = lookable.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var found = new List<InstalledPackage>(await queries.FindInstalledByNameAsync(names, ct));
        var terms = lookable.Select(p => NameKey.SearchTerm(p.Name)).Where(t => t.Length >= MinSearchLength).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        IReadOnlyList<CatalogEntry> hits = [];
        if (terms.Count > 0)
        {
            hits = await queries.SearchCatalogByNameAsync(terms, ct);
            var candidates = hits.Select(e => e.Id).Where(id => !listedIds.Contains(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (candidates.Count > 0) found.AddRange(await queries.FindInstalledByIdAsync(candidates, details: false, ct));
        }
        var matches = Matcher.Accept(lookable, listedIds, found);
        // What the lookups found something like, though nothing they could take (spec §4.4).
        var similar = lookable.Where(p => found.Any(f => f.CatalogId is not null && string.Equals(f.LocalId, p.LocalId, StringComparison.OrdinalIgnoreCase))
                || NameKey.SearchTerm(p.Name) is { Length: >= MinSearchLength } term && hits.Any(e => e.Name.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .Select(p => p.LocalId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return matches.Count == 0 && similar.Count == 0 ? inventory : Build(listed, matches, similar);
    }

    // Steam games, Store apps and apps of unknown version aren't worth a lookup.
    private static bool Lookable(InstalledPackage p) =>
        p.Name.Length > 0
        && !PackageVersion.Parse(p.Version).IsUnknown
        && ElsewhereHint.For(p) is not (UpdatedBy.Steam or UpdatedBy.MicrosoftStore);

    // similar: the local ids winget has something like; a known updater says more.
    private static AppInventory Build(IReadOnlyList<InstalledPackage> listed, IReadOnlyDictionary<string, InstalledPackage> matches, IReadOnlySet<string> similar)
    {
        var trackable = new List<InventoryApp>();
        var elsewhere = new List<ElsewhereApp>();
        foreach (var p in listed)
        {
            var match = p.CatalogId is null ? matches.GetValueOrDefault(p.LocalId) : p;
            if (match?.CatalogId is { } id && !PackageVersion.Parse(p.Version).IsUnknown)
                trackable.Add(new InventoryApp(id, TrackedApp.WinGet, match.Name, p.Version, p.Publisher, p.LocalId));
            else
                elsewhere.Add(new ElsewhereApp(p.Name, p.Version, p.Publisher, p.LocalId,
                    ElsewhereHint.For(p) is UpdatedBy.Unknown && similar.Contains(p.LocalId) ? UpdatedBy.NoExactMatch : ElsewhereHint.For(p)));
        }
        return new AppInventory(
            [.. trackable.DistinctBy(a => a.Id.ToUpperInvariant()).OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)],
            [.. elsewhere.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)]);
    }
}
