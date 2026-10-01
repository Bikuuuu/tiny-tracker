using TinyTracker.Core.Installing;
using TinyTracker.Core.SelfUpdate;
using TinyTracker.Core.Tracking;

namespace TinyTracker.Presentation.Tests.Demo;

// The demo's admin helper inside the test: its prompt answers itself after a moment, and the upgrades it runs are the demo's own.
// The app's demo starts the real helper instead.
internal sealed class DemoHelper(IPackageUpgrader demo, TimeProvider time) : IElevation
{
    public static readonly TimeSpan PromptShownFor = TimeSpan.FromSeconds(1.5);

    public async Task<HelperStart> StartAsync(bool mayPrompt, CancellationToken ct)
    {
        if (!mayPrompt) return new HelperStart(HelperStartResult.NeedsPrompt);
        try
        {
            await Task.Delay(PromptShownFor, time, ct);
        }
        catch (OperationCanceledException)
        {
            return new HelperStart(HelperStartResult.Failed);
        }
        return new HelperStart(HelperStartResult.Started, new Session(demo));
    }

    private sealed class Session(IPackageUpgrader demo) : IHelperSession
    {
        public bool WinGetAvailable => true;

        public Task<UpgradeOutcome> UpgradeAsync(PackageKey package, string version, IProgress<UpgradeProgress>? progress, CancellationToken ct) =>
            demo.UpgradeAsync(package, version, progress, ct);

        public Task<string?> EnableProxyOptionAsync(CancellationToken ct) => Task.FromResult<string?>(null);

        // Its pretend Setup starts at once.
        public Task<UpgradeOutcome> SelfUpdateAsync(SelfVersion version, IProgress<UpgradeProgress>? progress, CancellationToken ct) =>
            Task.FromResult(new UpgradeOutcome(UpgradeResult.Updated));

        public void Dispose()
        {
        }
    }
}
