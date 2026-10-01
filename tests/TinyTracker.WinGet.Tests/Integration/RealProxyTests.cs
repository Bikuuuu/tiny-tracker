using TinyTracker.Core.Installing;
using TinyTracker.WinGet.Cli;
using Xunit;

namespace TinyTracker.WinGet.Tests.Integration;

// Real winget, read-only: while its proxy option is off, it refuses the speed limit's --proxy before doing anything (spec §6.4).
public class RealProxyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ProxyOptionOff_RefusesTheLimit()
    {
        await RealWinGet.RequireCliAsync();
        if (await WinGetSettings.ProxyOptionAsync(WinGetCli.Real, Ct) != false) Assert.Skip("winget's proxy option isn't off here.");
        // No such package: even a winget that took the proxy would find nothing to do.
        var outcome = await LimitedUpgrade.Real.RunAsync("Contoso.NoSuchPackage", "1.0", new SpeedLimit(1000), null, Ct);
        Assert.Equal((UpgradeResult.Failed, UpgradeFailure.ProxyRefused), (outcome.Result, outcome.Failure));
    }
}
