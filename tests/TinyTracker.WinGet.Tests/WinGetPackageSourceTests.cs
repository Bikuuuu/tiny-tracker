using TinyTracker.Core.Checking;
using TinyTracker.Core.Tracking;
using Xunit;

namespace TinyTracker.WinGet.Tests;

public class WinGetPackageSourceTests
{
    private readonly FakeQueries _queries = new();
    private int _opens;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TrackedApp Tracked(string id, string source = "winget") => new() { Id = id, Source = source };

    private Task<CatalogRead> Read(params TrackedApp[] apps) =>
        new WinGetPackageSource(_ =>
        {
            _opens++;
            return Task.FromResult<IWinGetQueries>(_queries);
        }).ReadAsync(apps, Ct);

    // Restore's read (spec §4.5): only which apps are installed, with no notes, elevation or catalog lookups.
    [Fact]
    public async Task InstalledRead_AsksOnlyWhatsInstalled()
    {
        _queries.Listed.Add(FakeQueries.Matched("Mozilla.Firefox", "Mozilla Firefox", "130.0", latest: "131.0", update: true, notes: "- Faster"));
        _queries.ById.Add(FakeQueries.Matched("Example.Editor", "Example Editor", "2.0"));
        IPackageSource source = new WinGetPackageSource(_ => Task.FromResult<IWinGetQueries>(_queries));
        var read = await source.ReadInstalledAsync([Tracked("Mozilla.Firefox"), Tracked("Example.Editor"), Tracked("Example.Gone"), Tracked("9NBLGGH4NNS1", "msstore")], Ct);
        Assert.Equal(["Mozilla.Firefox", "Example.Editor"], read.Installed.Select(p => p.Id));
        Assert.Null(read.Installed[0].ReleaseNotes);
        Assert.Empty(read.NotInCatalog);
        Assert.Equal(["list", "installed ids Example.Editor,Example.Gone"], _queries.Asked);
    }

    private Task<CatalogRead> ReadInstalled(params TrackedApp[] apps) =>
        ((IPackageSource)new WinGetPackageSource(_ =>
        {
            _opens++;
            return Task.FromResult<IWinGetQueries>(_queries);
        })).ReadInstalledAsync(apps, Ct);

    [Fact]
    public async Task InstalledRead_WithoutWinGetApps_OpensNothing()
    {
        Assert.Empty((await ReadInstalled(Tracked("9NBLGGH4NNS1", "msstore"))).Installed);
        Assert.Equal(0, _opens);
    }

    // winget's spelling of the id, and one snapshot for an id installed twice, as the check has them.
    [Fact]
    public async Task InstalledRead_GivesWinGetsIdOnce()
    {
        _queries.Listed.Add(FakeQueries.Matched("Mozilla.Firefox", "Mozilla Firefox", "131.0", localId: @"ARP\User\X64\Mozilla Firefox"));
        _queries.Listed.Add(FakeQueries.Matched("Mozilla.Firefox", "Mozilla Firefox", "130.0", latest: "131.0", update: true));
        Assert.Equal("Mozilla.Firefox", Assert.Single((await ReadInstalled(Tracked("mozilla.firefox"))).Installed).Id);
    }

    [Fact]
    public async Task InstalledRead_PassesWinGetFailuresOn()
    {
        IPackageSource source = new WinGetPackageSource(_ => throw new PackageSourceException(CheckProblem.WinGetTooOld, "old"));
        var error = await Assert.ThrowsAsync<PackageSourceException>(() => source.ReadInstalledAsync([Tracked("Mozilla.Firefox")], Ct));
        Assert.Equal(CheckProblem.WinGetTooOld, error.Problem);
    }

    // New apps (spec §4.3) come from the same list: every app it matched to the catalog, tracked or not, but none whose version is
    // unknown, as Choose apps leaves those out too.
    [Fact]
    public async Task FullList_IsPassedOn()
    {
        _queries.Listed.Add(FakeQueries.Matched("Mozilla.Firefox", "Mozilla Firefox", "130.0"));
        _queries.Listed.Add(FakeQueries.Matched("Example.Editor", "Example Editor", "2.0"));
        _queries.Listed.Add(FakeQueries.Matched("Example.Launcher", "Example Launcher", "Unknown"));
        _queries.Listed.Add(new InstalledPackage(@"ARP\Machine\X64\Legacy", "Legacy Tool", "Example Publisher", "1.0"));
        var read = await Read(Tracked("Mozilla.Firefox"));
        Assert.Equal(
            [new ListedApp("Mozilla.Firefox", "Mozilla Firefox", @"ARP\Machine\X64\Mozilla.Firefox"), new ListedApp("Example.Editor", "Example Editor", @"ARP\Machine\X64\Example.Editor")],
            read.Listed);
    }

    // Nothing tracked from winget: no list is read.
    [Fact]
    public async Task NoWinGetApp_ReadsNoList()
    {
        _queries.Listed.Add(FakeQueries.Matched("Example.Editor", "Example Editor", "2.0"));
        Assert.Empty((await Read(Tracked("9NBLGGH4NNS1", "msstore"))).Listed);
        Assert.Empty(_queries.Asked);
    }

    [Fact]
    public async Task ListedApp_NeedsNoLookup()
    {
        _queries.Listed.Add(FakeQueries.Matched("Mozilla.Firefox", "Mozilla Firefox", "130.0", latest: "131.0", update: true, elevation: InstallerElevation.ElevatesSelf));
        var read = await Read(Tracked("Mozilla.Firefox"));
        Assert.Equal(
            new PackageSnapshot("Mozilla.Firefox", "winget", "Mozilla Firefox", "130.0", "131.0", "Example Publisher", "https://example.com/notes", @"ARP\Machine\X64\Mozilla.Firefox",
                InstallScope.Machine, InstallerElevation.ElevatesSelf),
            Assert.Single(read.Installed));
        Assert.Empty(read.NotInCatalog);
        Assert.Equal(["list with details Mozilla.Firefox"], _queries.Asked);
    }

    // Only the tracked apps' updates need their notes and elevation, so winget reads them for no other package.
    [Fact]
    public async Task Details_AreReadOnlyForTheTrackedApps()
    {
        _queries.Listed.Add(FakeQueries.Matched("Mozilla.Firefox", "Mozilla Firefox", "130.0", latest: "131.0", update: true, notes: "- Fixed a crash"));
        _queries.Listed.Add(FakeQueries.Matched("Example.Editor", "Example Editor", "2.0", latest: "2.1", update: true, notes: "- Faster"));
        var read = await Read(Tracked("Mozilla.Firefox"));
        Assert.Equal("- Fixed a crash", Assert.Single(read.Installed).ReleaseNotes);
        Assert.Equal(["list with details Mozilla.Firefox"], _queries.Asked);
    }

    [Fact]
    public async Task AppTheListMissed_GetsItsNotesFromTheLookup()
    {
        _queries.ById.Add(FakeQueries.Matched("Example.Editor", "Example Editor", "2.0", latest: "2.1", update: true, notes: "- Faster"));
        var package = Assert.Single((await Read(Tracked("Example.Editor"))).Installed);
        Assert.Equal(("- Faster", "https://example.com/notes"), (package.ReleaseNotes, package.ReleaseNotesUrl));
    }

    [Fact]
    public async Task NoUpdate_HasNoAvailableVersion()
    {
        _queries.Listed.Add(FakeQueries.Matched("Mozilla.Firefox", "Mozilla Firefox", "131.0"));
        Assert.Null(Assert.Single((await Read(Tracked("Mozilla.Firefox"))).Installed).AvailableVersion);
    }

    [Fact]
    public async Task AppTheListMissed_IsLookedUpById()
    {
        _queries.ById.Add(FakeQueries.Matched("Example.Editor", "Example Editor", "2.0", latest: "2.1", update: true));
        var read = await Read(Tracked("Example.Editor"));
        Assert.Equal("2.1", Assert.Single(read.Installed).AvailableVersion);
        Assert.Equal(["list with details Example.Editor", "installed ids Example.Editor with details"], _queries.Asked);
    }

    [Fact]
    public async Task AppNotInstalled_IsStillInTheCatalog()
    {
        _queries.Catalog.Add(new CatalogEntry("Example.Editor", "Example Editor", "2.1"));
        var read = await Read(Tracked("Example.Editor"));
        Assert.Empty(read.Installed);
        Assert.Empty(read.NotInCatalog);
        Assert.Equal(["list with details Example.Editor", "installed ids Example.Editor with details", "catalog ids Example.Editor"], _queries.Asked);
    }

    [Fact]
    public async Task AppGoneFromTheCatalog_IsNotInCatalog() =>
        Assert.Equal(new PackageKey("Example.Editor", "winget"), Assert.Single((await Read(Tracked("Example.Editor"))).NotInCatalog));

    [Fact]
    public async Task OtherSource_IsNotInCatalogWithoutAskingWinget()
    {
        var read = await Read(Tracked("XP0000000000", "msstore"));
        Assert.Equal(new PackageKey("XP0000000000", "msstore"), Assert.Single(read.NotInCatalog));
        Assert.Equal(0, _opens);
    }

    [Fact]
    public async Task IdCase_DoesNotMatter()
    {
        _queries.Listed.Add(FakeQueries.Matched("Mozilla.Firefox", "Mozilla Firefox", "131.0"));
        Assert.Equal("Mozilla.Firefox", Assert.Single((await Read(Tracked("mozilla.firefox"))).Installed).Id);
    }

    [Fact]
    public async Task SameIdInstalledTwice_PrefersTheOneWithAnUpdate()
    {
        _queries.Listed.Add(FakeQueries.Matched("Mozilla.Firefox", "Mozilla Firefox", "131.0", localId: @"ARP\User\X64\Mozilla Firefox"));
        _queries.Listed.Add(FakeQueries.Matched("Mozilla.Firefox", "Mozilla Firefox", "130.0", latest: "131.0", update: true));
        Assert.Equal("130.0", Assert.Single((await Read(Tracked("Mozilla.Firefox"))).Installed).InstalledVersion);
    }

    [Fact]
    public async Task WinGetFailure_Propagates()
    {
        var source = new WinGetPackageSource(_ => throw new PackageSourceException(CheckProblem.WinGetTooOld, "old"));
        var error = await Assert.ThrowsAsync<PackageSourceException>(() => source.ReadAsync([Tracked("Mozilla.Firefox")], Ct));
        Assert.Equal(CheckProblem.WinGetTooOld, error.Problem);
    }

    [Fact]
    public async Task MissingApps_AreLookedUpInOneCall()
    {
        await Read(Tracked("Example.Editor"), Tracked("Example.Viewer"));
        Assert.Contains("installed ids Example.Editor,Example.Viewer with details", _queries.Asked);
    }
}
