using TinyTracker.Core.Installing;
using TinyTracker.Core.SelfUpdate;
using TinyTracker.Core.Tracking;

namespace TinyTracker.Presentation.Demo;

// The app's demo starts the real helper, whose winget is faked (spec §12). What it installs is written into the demo's apps,
// as winget would, so the row that follows doesn't look like a phantom.
public sealed class DemoHelperBridge(IElevation helper, DemoWinGet demo) : IElevation
{
    public bool Prompts => helper.Prompts;

    public Task<HelperStart> StartAsync(bool mayPrompt, CancellationToken ct) => StartAsync(mayPrompt, _ => { }, ct);

    public async Task<HelperStart> StartAsync(bool mayPrompt, Action<bool> prompting, CancellationToken ct)
    {
        var start = await helper.StartAsync(mayPrompt, prompting, ct);
        return start.Session is { } session ? start with { Session = new Session(session, demo) } : start;
    }

    private sealed class Session(IHelperSession helper, DemoWinGet demo) : IHelperSession
    {
        public bool WinGetAvailable => helper.WinGetAvailable;

        public async Task<UpgradeOutcome> UpgradeAsync(PackageKey package, string version, IProgress<UpgradeProgress>? progress, CancellationToken ct)
        {
            var outcome = await helper.UpgradeAsync(package, version, progress, ct);
            if (outcome.Result == UpgradeResult.Updated) demo.Installed(package, version);
            return outcome;
        }

        public Task<string?> EnableProxyOptionAsync(CancellationToken ct) => helper.EnableProxyOptionAsync(ct);

        public void Stay() => helper.Stay();

        public Task<UpgradeOutcome> SelfUpdateAsync(SelfVersion version, IProgress<UpgradeProgress>? progress, CancellationToken ct) => helper.SelfUpdateAsync(version, progress, ct);

        public void Dispose() => helper.Dispose();
    }
}
