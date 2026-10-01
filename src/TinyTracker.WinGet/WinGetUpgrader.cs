using TinyTracker.Core.Checking;
using TinyTracker.Core.Installing;
using TinyTracker.Core.Tracking;
using TinyTracker.WinGet.Cli;

namespace TinyTracker.WinGet;

// Upgrades one package in a fresh session: through winget's COM API, or with the speed limit on, through its command line
// (spec §6.4). The limit is read as the update starts; one turned on later waits for the next update.
public sealed class WinGetUpgrader(SpeedLimit? limit = null) : IPackageUpgrader
{
    public async Task<UpgradeOutcome> UpgradeAsync(PackageKey package, string version, IProgress<UpgradeProgress>? progress, CancellationToken ct)
    {
        if (!string.Equals(package.Source, TrackedApp.WinGet, StringComparison.OrdinalIgnoreCase)) return new UpgradeOutcome(UpgradeResult.NotInstalled);
        var limited = limit is { KBps: > 0 };
        try
        {
            var session = await WinGetSession.OpenAsync(ct);
            return await Task.Run(() => session.UpgradeAsync(package.Id, version, progress, ct, limited ? (id, p, c) => LimitedUpgrade.Real.RunAsync(id, version, limit!, p, c) : null),
                CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            return new UpgradeOutcome(UpgradeResult.Cancelled);
        }
        catch (PackageSourceException e)
        {
            return new UpgradeOutcome(UpgradeResult.Failed, UpgradeFailure.WinGetUnavailable, e.Code);
        }
        // IPackageUpgrader never throws.
        catch (Exception e)
        {
            return new UpgradeOutcome(UpgradeResult.Failed, UpgradeFailure.WinGetUnavailable, $"0x{e.HResult:X8}");
        }
    }
}
