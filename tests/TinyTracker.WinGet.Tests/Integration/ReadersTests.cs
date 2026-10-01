using TinyTracker.Core.Tracking;
using Xunit;

namespace TinyTracker.WinGet.Tests.Integration;

// Asserts over installed packages print no package data: a failure must not reveal installed apps.
public class ReadersTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<IWinGetQueries> Open(CancellationToken ct) => await RealWinGet.OpenAsync();

    [Fact]
    public async Task PackageSource_ReadsAListedPackage()
    {
        var session = await RealWinGet.OpenAsync();
        var listed = (await session.ListInstalledAsync(RealWinGet.NoDetails, Ct)).First(p => p.CatalogId is not null);
        var read = await new WinGetPackageSource(Open).ReadAsync([new TrackedApp { Id = listed.CatalogId!, Source = "winget" }], Ct);
        var readBack = read.Installed.Count == 1 && string.Equals(read.Installed[0].Id, listed.CatalogId, StringComparison.OrdinalIgnoreCase);
        var gone = read.NotInCatalog.Count;
        Assert.True(readBack, "The listed package wasn't read back as the only snapshot.");
        Assert.Equal(0, gone);
    }

    [Fact]
    public async Task PackageSource_ReportsAnIdTheCatalogDoesNotHave()
    {
        await RealWinGet.OpenAsync();
        var read = await new WinGetPackageSource(Open).ReadAsync([new TrackedApp { Id = "Nobody.NoSuchPackage.Anywhere", Source = "winget" }], Ct);
        Assert.Empty(read.Installed);
        Assert.Equal("Nobody.NoSuchPackage.Anywhere", Assert.Single(read.NotInCatalog).Id);
    }

    [Fact]
    public async Task Inventory_ListsEachAppOnce()
    {
        var session = await RealWinGet.OpenAsync();
        var listed = await session.ListInstalledAsync(RealWinGet.NoDetails, Ct);
        var inventory = await new WinGetInventory(Open).ReadAsync(null, Ct);
        Assert.InRange(inventory.Trackable.Count + inventory.Elsewhere.Count, 1, listed.Count);
        Assert.Equal(inventory.Trackable.Count, inventory.Trackable.Select(a => a.Id.ToUpperInvariant()).Distinct().Count());
    }
}
