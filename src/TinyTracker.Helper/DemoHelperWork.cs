#if DEBUG
using TinyTracker.Core.Installing;
using TinyTracker.Core.SelfUpdate;
using TinyTracker.Core.Tracking;
using TinyTracker.WinGet.Elevation;

namespace TinyTracker.Helper;

// The demo's helper fakes winget, so nothing installs (spec §12). Its downloads run at the speed limit, and Adatum Backup's
// helper quits partway, to show that failure.
internal sealed class DemoHelperWork : IHelperWork
{
    private const ulong MB = 1024 * 1024;
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(250);

    public Task<string?> OpenAsync(CancellationToken ct) => Task.FromResult<string?>(null);

    public async Task<UpgradeOutcome> UpgradeAsync(PackageKey package, string version, SpeedLimit limit, IProgress<UpgradeProgress> progress, CancellationToken ct)
    {
        const ulong total = 80 * MB;
        progress.Report(new UpgradeProgress(UpgradeStage.Queued, 0, 0, 0, 0));
        if (!await DownloadAsync(total, limit, progress, done =>
        {
            if (package.Id == "Adatum.Backup" && done >= 24 * MB) Environment.Exit(4);
        }, ct)) return new UpgradeOutcome(UpgradeResult.Cancelled);
        // Like winget, a started installer can't be cancelled.
        for (var step = 0; step <= 8; step++)
        {
            progress.Report(new UpgradeProgress(UpgradeStage.Installing, total, total, 1, step / 8.0));
            await Task.Delay(Step, CancellationToken.None);
        }
        return new UpgradeOutcome(UpgradeResult.Updated);
    }

    // Nothing is written and nothing starts: the demo's app pretends Setup ran. The helper still leaves as after a real one.
    public async Task<UpgradeOutcome> SelfUpdateAsync(SelfVersion version, SpeedLimit limit, IProgress<UpgradeProgress> progress, CancellationToken ct)
    {
        const ulong total = 48 * MB;
        if (!await DownloadAsync(total, limit, progress, null, ct)) return new UpgradeOutcome(UpgradeResult.Cancelled);
        progress.Report(new UpgradeProgress(UpgradeStage.Downloading, total, total, 1, 0));
        return new UpgradeOutcome(UpgradeResult.Updated);
    }

    // A step's share of the limit, which can change meanwhile. False when cancelled.
    private static async Task<bool> DownloadAsync(ulong total, SpeedLimit limit, IProgress<UpgradeProgress> progress, Action<ulong>? each, CancellationToken ct)
    {
        try
        {
            for (ulong done = 0; done < total; done += limit.KBps > 0 ? (ulong)limit.BytesPerSecond / 4 : 8 * MB)
            {
                each?.Invoke(done);
                progress.Report(new UpgradeProgress(UpgradeStage.Downloading, done, total, (double)done / total, 0));
                await Task.Delay(Step, ct);
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    // The demo never registers a task.
    public string? RegisterTask() => "not in the demo";

    public string? RemoveTask() => "not in the demo";

    // Nor changes winget's settings: the option counts as on.
    public Task<string?> EnableProxyOptionAsync(CancellationToken ct) => Task.FromResult<string?>(null);
}
#endif
