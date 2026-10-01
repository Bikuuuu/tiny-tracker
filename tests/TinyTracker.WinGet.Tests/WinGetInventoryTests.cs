using TinyTracker.Core.Inventory;
using Xunit;

namespace TinyTracker.WinGet.Tests;

public class WinGetInventoryTests
{
    private readonly FakeQueries _queries = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static InstalledPackage Unmatched(string localId, string name, string version) => new(localId, name, "Example Publisher", version);

    private Task<AppInventory> Read(IProgress<AppInventory>? firstList = null) =>
        new WinGetInventory(_ => Task.FromResult<IWinGetQueries>(_queries)).ReadAsync(firstList, Ct);

    [Fact]
    public async Task ListedApps_AreTrackableAndTheRestElsewhere()
    {
        _queries.Listed.Add(FakeQueries.Matched("Mozilla.Firefox", "Mozilla Firefox", "130.0"));
        _queries.Listed.Add(Unmatched(@"ARP\Machine\X64\Steam App 12345", "Example Game", "Unknown"));
        var inventory = await Read();
        Assert.Equal(
            new InventoryApp("Mozilla.Firefox", "winget", "Mozilla Firefox", "130.0", "Example Publisher", @"ARP\Machine\X64\Mozilla.Firefox"),
            Assert.Single(inventory.Trackable));
        Assert.Equal(UpdatedBy.Steam, Assert.Single(inventory.Elsewhere).UpdatedBy);
    }

    [Fact]
    public async Task UnknownVersion_IsElsewhereAndUpdatesItself()
    {
        _queries.Listed.Add(FakeQueries.Matched("Example.Launcher", "Example Launcher", "Unknown"));
        var inventory = await Read();
        Assert.Empty(inventory.Trackable);
        Assert.Equal(UpdatedBy.ItSelf, Assert.Single(inventory.Elsewhere).UpdatedBy);
    }

    [Fact]
    public async Task SameIdTwice_IsListedOnce()
    {
        _queries.Listed.Add(FakeQueries.Matched("Mozilla.Firefox", "Mozilla Firefox", "130.0"));
        _queries.Listed.Add(FakeQueries.Matched("Mozilla.Firefox", "Mozilla Firefox", "130.0", localId: @"ARP\User\X64\Mozilla Firefox"));
        Assert.Single((await Read()).Trackable);
    }

    [Fact]
    public async Task ExactNameMatch_MovesTheAppIntoTrackable()
    {
        var editor = Unmatched(@"ARP\Machine\X64\{EDITOR}", "Example Editor", "2.0");
        _queries.Listed.Add(editor);
        _queries.ByName.Add(("Example Editor", editor with { CatalogId = "Example.Editor", CatalogName = "Example Editor", LatestVersion = "2.1", UpdateAvailable = true }));
        var inventory = await Read();
        Assert.Equal(new InventoryApp("Example.Editor", "winget", "Example Editor", "2.0", "Example Publisher", editor.LocalId), Assert.Single(inventory.Trackable));
        Assert.Empty(inventory.Elsewhere);
    }

    [Fact]
    public async Task CatalogSearchMatch_IsConfirmedById()
    {
        var python = Unmatched(@"ARP\Machine\X64\{PYTHON-312}", "Python 3.12.5 (64-bit)", "3.12.5");
        _queries.Listed.Add(python);
        _queries.Catalog.Add(new CatalogEntry("Python.Python.3.12", "Python 3.12", "3.12.10"));
        _queries.Catalog.Add(new CatalogEntry("Python.Python.3.13", "Python 3.13", "3.13.7"));
        _queries.ById.Add(python with { CatalogId = "Python.Python.3.12", CatalogName = "Python 3.12", LatestVersion = "3.12.10" });
        _queries.ById.Add(python with { CatalogId = "Python.Python.3.13", CatalogName = "Python 3.13", LatestVersion = "3.13.7" });
        var inventory = await Read();
        Assert.Equal("Python.Python.3.12", Assert.Single(inventory.Trackable).Id);
        Assert.Contains("catalog search Python", _queries.Asked);
        Assert.Contains("installed ids Python.Python.3.12,Python.Python.3.13", _queries.Asked);
    }

    // Choose apps shows no notes and routes no update, so it asks winget for neither.
    [Fact]
    public async Task NoDetails_AreAskedFor()
    {
        var python = Unmatched(@"ARP\Machine\X64\{PYTHON-312}", "Python 3.12.5 (64-bit)", "3.12.5");
        _queries.Listed.Add(FakeQueries.Matched("Mozilla.Firefox", "Mozilla Firefox", "130.0", latest: "131.0", update: true, notes: "- Fixed a crash"));
        _queries.Listed.Add(python);
        _queries.Catalog.Add(new CatalogEntry("Python.Python.3.12", "Python 3.12", "3.12.10"));
        _queries.ById.Add(python with { CatalogId = "Python.Python.3.12", CatalogName = "Python 3.12", LatestVersion = "3.12.10" });
        await Read();
        Assert.Contains("list", _queries.Asked);
        Assert.Contains("installed ids Python.Python.3.12", _queries.Asked);
        Assert.DoesNotContain(_queries.Asked, q => q.Contains("with details", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OtherEdition_StaysElsewhere()
    {
        var firefox = Unmatched(@"ARP\Machine\X64\Mozilla Firefox", "Mozilla Firefox", "131.0");
        _queries.Listed.Add(firefox);
        _queries.ByName.Add(("Mozilla Firefox", firefox with { CatalogId = "Mozilla.Firefox.Beta", CatalogName = "Mozilla Firefox Beta", LatestVersion = "132.0b3" }));
        var inventory = await Read();
        Assert.Empty(inventory.Trackable);
        var elsewhere = Assert.Single(inventory.Elsewhere);
        Assert.Equal(("Mozilla Firefox", UpdatedBy.NoExactMatch), (elsewhere.Name, elsewhere.UpdatedBy));
    }

    // winget has packages of a similar name, but the lookup can't take one (spec §4.4).
    [Fact]
    public async Task SimilarCatalogNames_SayNoExactMatch()
    {
        _queries.Listed.Add(Unmatched(@"ARP\Machine\X64\{STUDIO}", "Fabrikam Studio (64-bit)", "26.2"));
        _queries.Catalog.Add(new CatalogEntry("Fabrikam.Studio.Reader", "Fabrikam Studio Reader (64-bit)", "26.2"));
        _queries.Catalog.Add(new CatalogEntry("Fabrikam.Studio.Pro", "Fabrikam Studio Pro", "26.2"));
        var inventory = await Read();
        Assert.Empty(inventory.Trackable);
        Assert.Equal(UpdatedBy.NoExactMatch, Assert.Single(inventory.Elsewhere).UpdatedBy);
    }

    // A short search term isn't searched, so it can't borrow another app's hits: "Ex" is in "Example Maps Pro" (spec §4.4).
    [Fact]
    public async Task ShortName_IsntSimilarToAnotherAppsHits()
    {
        _queries.Listed.Add(Unmatched(@"ARP\Machine\X64\{EX}", "Ex 2.1", "2.1"));
        _queries.Listed.Add(Unmatched(@"ARP\Machine\X64\{MAPS}", "Example Maps", "3.0"));
        _queries.Catalog.Add(new CatalogEntry("Example.Maps.Pro", "Example Maps Pro", "3.0"));
        var inventory = await Read();
        Assert.Equal([("Ex 2.1", UpdatedBy.Unknown), ("Example Maps", UpdatedBy.NoExactMatch)], inventory.Elsewhere.Select(a => (a.Name, a.UpdatedBy)));
    }

    [Fact]
    public async Task NothingSimilar_SaysNotInWinGet()
    {
        _queries.Listed.Add(Unmatched(@"ARP\Machine\X64\{STUDIO}", "Fabrikam Studio (64-bit)", "26.2"));
        _queries.Catalog.Add(new CatalogEntry("Contoso.Paint", "Contoso Paint", "7.0"));
        var inventory = await Read();
        Assert.Equal(UpdatedBy.Unknown, Assert.Single(inventory.Elsewhere).UpdatedBy);
    }

    [Fact]
    public async Task IdsTheListHas_AreNotLookedUpAgain()
    {
        _queries.Listed.Add(FakeQueries.Matched("Example.Editor", "Example Editor", "2.0"));
        _queries.Listed.Add(Unmatched(@"ARP\Machine\X64\{VIEWER}", "Example Viewer 3.0", "3.0"));
        _queries.Catalog.Add(new CatalogEntry("Example.Editor", "Example Viewer Suite", "2.1"));
        await Read();
        Assert.Contains("catalog search Example Viewer", _queries.Asked);
        Assert.DoesNotContain(_queries.Asked, q => q.StartsWith("installed ids", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GamesStoreAppsAndUnknownVersions_AreNotLookedUp()
    {
        _queries.Listed.Add(Unmatched(@"ARP\Machine\X64\Steam App 12345", "Example Game", "1.0"));
        _queries.Listed.Add(Unmatched(@"MSIX\Example.App_1.0.0.0_x64__abcdefgh", "Example App", "1.0.0.0"));
        _queries.Listed.Add(Unmatched(@"ARP\Machine\X64\Example Launcher", "Example Launcher", "Unknown"));
        var inventory = await Read();
        Assert.Equal(["list"], _queries.Asked);
        Assert.Equal(3, inventory.Elsewhere.Count);
    }

    [Fact]
    public async Task StoreEntry_IsNeverMatched()
    {
        var store = Unmatched(@"MSIX\Example.App_1.0.0.0_x64__abcdefgh", "Example App", "1.0.0.0");
        _queries.Listed.Add(store);
        _queries.Listed.Add(Unmatched(@"ARP\Machine\X64\{EXAMPLE-APP}", "Example App", "1.0"));
        _queries.ByName.Add(("Example App", store with { CatalogId = "Example.App", CatalogName = "Example App", LatestVersion = "1.1" }));
        var inventory = await Read();
        Assert.Empty(inventory.Trackable);
        Assert.Contains(inventory.Elsewhere, a => a.LocalId == store.LocalId && a.UpdatedBy == UpdatedBy.MicrosoftStore);
    }

    [Fact]
    public async Task FirstList_IsReportedBeforeTheLookups()
    {
        var editor = Unmatched(@"ARP\Machine\X64\{EDITOR}", "Example Editor", "2.0");
        _queries.Listed.Add(editor);
        _queries.ByName.Add(("Example Editor", editor with { CatalogId = "Example.Editor", CatalogName = "Example Editor", LatestVersion = "2.1" }));
        var reported = new List<AppInventory>();
        var inventory = await Read(new Reported<AppInventory>(reported.Add));
        Assert.Single(Assert.Single(reported).Elsewhere);
        Assert.Single(inventory.Trackable);
    }

    [Fact]
    public async Task Lists_AreSortedByName()
    {
        _queries.Listed.Add(FakeQueries.Matched("Example.Zeta", "zeta", "1.0"));
        _queries.Listed.Add(FakeQueries.Matched("Example.Alpha", "Alpha", "1.0"));
        _queries.Listed.Add(Unmatched(@"ARP\Machine\X64\Steam App 2", "Zed Game", "Unknown"));
        _queries.Listed.Add(Unmatched(@"ARP\Machine\X64\Steam App 1", "another game", "Unknown"));
        var inventory = await Read();
        Assert.Equal(["Alpha", "zeta"], inventory.Trackable.Select(a => a.Name));
        Assert.Equal(["another game", "Zed Game"], inventory.Elsewhere.Select(a => a.Name));
    }
}
