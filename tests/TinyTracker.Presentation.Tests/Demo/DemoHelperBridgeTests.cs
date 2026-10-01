using Microsoft.Extensions.Time.Testing;
using TinyTracker.Core.Installing;
using TinyTracker.Core.SelfUpdate;
using TinyTracker.Core.Tracking;
using TinyTracker.Presentation.Demo;
using Xunit;
using static TinyTracker.Presentation.Tests.Fixtures;

namespace TinyTracker.Presentation.Tests.Demo;

// The app's demo runs the real helper, whose winget is faked. What it installs lands in the demo's apps, as it would with winget.
public sealed class DemoHelperBridgeTests
{
    private readonly FakeTimeProvider _time = new(Now);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(UpgradeResult.Updated, "2025.2")]
    [InlineData(UpgradeResult.Failed, "2025.1")]
    public async Task WhatTheHelperInstalls_LandsInTheDemosApps(UpgradeResult result, string installed)
    {
        var demo = new DemoWinGet(_time);
        var helper = new CannedHelper(HelperStartResult.Started, new UpgradeOutcome(result));
        var start = await new DemoHelperBridge(helper, demo).StartAsync(mayPrompt: true, Ct);
        var session = start.Session!;
        Assert.True(session.WinGetAvailable);
        Assert.Equal(result, (await session.UpgradeAsync(new PackageKey("Proseware.Maps", "winget"), "2025.2", null, Ct)).Result);
        session.Dispose();
        Assert.True(helper.Disposed);
        var read = demo.ReadAsync([App("Proseware.Maps", offer: "2025.2")], Ct);
        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(installed, Assert.Single((await read).Installed).InstalledVersion);
    }

    // The demo's silent mode starts its helper with no prompt, and the queue hears so.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Prompts_AsTheHelpersLauncherSays(bool prompts) =>
        Assert.Equal(prompts, new DemoHelperBridge(new CannedHelper(HelperStartResult.Started) { Prompts = prompts }, new DemoWinGet(_time)).Prompts);

    // The demo's helper shows the real prompt, and whoever started it hears the prompt open and close.
    [Fact]
    public async Task Prompt_IsPassedOn()
    {
        var heard = new List<bool>();
        await new DemoHelperBridge(new CannedHelper(HelperStartResult.Started), new DemoWinGet(_time)).StartAsync(mayPrompt: true, heard.Add, Ct);
        Assert.Equal([true, false], heard);
    }

    [Fact]
    public async Task ProxyOption_IsTheHelpersToTurnOn()
    {
        var start = await new DemoHelperBridge(new CannedHelper(HelperStartResult.Started), new DemoWinGet(_time)).StartAsync(mayPrompt: true, Ct);
        using var session = start.Session!;
        Assert.Equal("from the helper", await session.EnableProxyOptionAsync(Ct));
    }

    // The demo's helper pretends it; the app's demo then pretends the restart.
    [Fact]
    public async Task SelfUpdate_IsTheHelpersToRun()
    {
        var start = await new DemoHelperBridge(new CannedHelper(HelperStartResult.Started), new DemoWinGet(_time)).StartAsync(mayPrompt: true, Ct);
        using var session = start.Session!;
        Assert.Equal(new UpgradeOutcome(UpgradeResult.Updated, Code: "from the helper"), await session.SelfUpdateAsync(new SelfVersion(9, 9, 9), null, Ct));
    }

    [Fact]
    public async Task StartWithoutAHelper_PassesThrough()
    {
        var start = await new DemoHelperBridge(new CannedHelper(HelperStartResult.Declined), new DemoWinGet(_time)).StartAsync(mayPrompt: true, Ct);
        Assert.Equal((HelperStartResult.Declined, (IHelperSession?)null), (start.Result, start.Session));
    }

    private sealed class CannedHelper(HelperStartResult result, UpgradeOutcome? outcome = null) : IElevation, IHelperSession
    {
        public bool Disposed { get; private set; }

        public bool Prompts { get; init; } = true;

        public bool WinGetAvailable => true;

        public Task<HelperStart> StartAsync(bool mayPrompt, CancellationToken ct) => StartAsync(mayPrompt, _ => { }, ct);

        // Its prompt shows and is answered at once.
        public Task<HelperStart> StartAsync(bool mayPrompt, Action<bool> prompting, CancellationToken ct)
        {
            prompting(true);
            prompting(false);
            return Task.FromResult(result == HelperStartResult.Started ? new HelperStart(result, this) : new HelperStart(result));
        }

        public Task<UpgradeOutcome> UpgradeAsync(PackageKey package, string version, IProgress<UpgradeProgress>? progress, CancellationToken ct) =>
            Task.FromResult(outcome!);

        public Task<string?> EnableProxyOptionAsync(CancellationToken ct) => Task.FromResult<string?>("from the helper");

        public Task<UpgradeOutcome> SelfUpdateAsync(SelfVersion version, IProgress<UpgradeProgress>? progress, CancellationToken ct) =>
            Task.FromResult(new UpgradeOutcome(UpgradeResult.Updated, Code: "from the helper"));

        public void Dispose() => Disposed = true;
    }
}
