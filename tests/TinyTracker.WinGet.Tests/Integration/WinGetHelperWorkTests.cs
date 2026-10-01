using System.Security.Principal;
using TinyTracker.Core.Installing;
using TinyTracker.Core.Tracking;
using TinyTracker.WinGet.Elevation;
using Xunit;

namespace TinyTracker.WinGet.Tests.Integration;

// The helper's winget, read-only: what it refuses never reaches an installer. Asserts print no package data.
public class WinGetHelperWorkTests
{
    private static readonly Reported<UpgradeProgress> Ignored = new(_ => { });
    private static readonly SecurityIdentifier Me = WindowsIdentity.GetCurrent().User!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Open_SaysWinGetAnswers()
    {
        await RealWinGet.OpenAsync();
        Assert.Null(await new WinGetHelperWork(Me).OpenAsync(Ct));
    }

    [Fact]
    public async Task PackageThatIsntInstalled_IsNotUpgraded()
    {
        await RealWinGet.OpenAsync();
        var outcome = await new WinGetHelperWork(Me).UpgradeAsync(new PackageKey("Nobody.NoSuchPackage.Anywhere", "winget"), "1.0", new SpeedLimit(), Ignored, Ct);
        Assert.Equal(UpgradeResult.NotInstalled, outcome.Result);
    }

    // Under the limit the same checks come first, so winget's command line never runs for it.
    [Fact]
    public async Task LimitedUpgradeOfAPackageThatIsntInstalled_IsRefusedFirst()
    {
        await RealWinGet.OpenAsync();
        var outcome = await new WinGetHelperWork(Me).UpgradeAsync(new PackageKey("Nobody.NoSuchPackage.Anywhere", "winget"), "1.0", new SpeedLimit(1000), Ignored, Ct);
        Assert.Equal(UpgradeResult.NotInstalled, outcome.Result);
    }

    [Fact]
    public async Task VersionWinGetDoesntOffer_IsNotUpgraded()
    {
        var session = await RealWinGet.OpenAsync();
        var matched = (await session.ListInstalledAsync(RealWinGet.NoDetails, Ct)).First(p => p.CatalogId is not null);
        var outcome = await new WinGetHelperWork(Me).UpgradeAsync(new PackageKey(matched.CatalogId!, "winget"), "0.0.0.1", new SpeedLimit(), Ignored, Ct);
        Assert.True(outcome.Result == UpgradeResult.NoUpdate, "An installed package took a version its catalog doesn't offer.");
        // The refusal says what's installed, for the log.
        Assert.True(outcome.Code is { } code && code.StartsWith("installed ", StringComparison.Ordinal) && code.EndsWith(", not in winget", StringComparison.Ordinal),
            "The refusal doesn't say what's installed.");
    }

    [Fact]
    public async Task OtherSource_IsNotUpgraded()
    {
        await RealWinGet.OpenAsync();
        var outcome = await new WinGetHelperWork(Me).UpgradeAsync(new PackageKey("Mozilla.Firefox", "msstore"), "131.0", new SpeedLimit(), Ignored, Ct);
        Assert.Equal(UpgradeResult.NotInstalled, outcome.Result);
    }
}
