using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using TinyTracker.Core.Installing;
using TinyTracker.WinGet.Cli;
using Xunit;
using static TinyTracker.WinGet.Tests.Cli.FakeWinGet;

namespace TinyTracker.WinGet.Tests.Cli;

// A limited upgrade against the fake winget, which downloads from a local server through the upgrade's relay (spec §6.4).
public sealed class LimitedUpgradeTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);
    private readonly HttpClient _http = new();
    private readonly List<(TimeSpan At, UpgradeProgress Progress)> _seen = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public void Dispose() => _http.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private LimitedUpgrade Upgrade(FakeWinGet fake, FakeServer server, TimeSpan? endAfter = null) =>
        new(fake.Cli, (url, ct) => DownloadSize.OfAsync(_http, url, ct), TimeProvider.System, new HashSet<int> { server.Port }, endAfter);

    private IProgress<UpgradeProgress> Recorded(Action<UpgradeProgress>? also = null) => new Reported<UpgradeProgress>(p =>
    {
        lock (_seen) _seen.Add((_clock.Elapsed, p));
        also?.Invoke(p);
    });

    private List<UpgradeStage> Stages()
    {
        lock (_seen)
        {
            var stages = new List<UpgradeStage>();
            foreach (var (_, progress) in _seen)
                if (stages.Count == 0 || stages[^1] != progress.Stage) stages.Add(progress.Stage);
            return stages;
        }
    }

    private List<(TimeSpan At, UpgradeProgress Progress)> Seen(UpgradeStage stage)
    {
        lock (_seen) return [.. _seen.Where(s => s.Progress.Stage == stage)];
    }

    private static object[] Installs(FakeServer server) =>
    [
        Say("Found Example Editor [Example.Editor] Version 2.5.0"),
        Download(server.Url().ToString()),
        Say("Successfully verified installer hash"),
        Say("Starting package install..."),
        Touch("installed.txt"),
        Sleep(300),
        Say("Successfully installed"),
    ];

    [Fact]
    public async Task Upgrade_WaitsThenDownloadsThenInstalls_WithTheSizeFromHead()
    {
        using var server = new FakeServer(300_000);
        using var fake = new FakeWinGet(Scenario(0, [Sleep(300), .. Installs(server)]));
        var outcome = await Upgrade(fake, server).RunAsync("Example.Editor", "2.5.0", new SpeedLimit(200), Recorded(), Ct);
        Assert.Equal(new UpgradeOutcome(UpgradeResult.Updated), outcome);
        Assert.Equal([UpgradeStage.Queued, UpgradeStage.Downloading, UpgradeStage.Installing], Stages());
        Assert.All(Seen(UpgradeStage.Downloading), s => Assert.True(s.Progress.BytesRequired is 0 or 300_000));
        Assert.Contains(Seen(UpgradeStage.Downloading), s => s.Progress.BytesRequired == 300_000);
        Assert.Equal(1, server.Heads);
    }

    [Fact]
    public async Task CommandLine_IsExactlyTheSpecs()
    {
        using var server = new FakeServer(1000);
        using var fake = new FakeWinGet(Scenario(0, Installs(server)));
        await Upgrade(fake, server).RunAsync("Example.Editor", "2.5.0 RC", new SpeedLimit(100_000), null, Ct);
        var arguments = fake.Arguments;
        Assert.Equal(["upgrade", "--id", "Example.Editor", "--exact", "--source", "winget", "--version", "2.5.0 RC", "--silent", "--accept-package-agreements",
            "--accept-source-agreements", "--disable-interactivity", "--proxy"], arguments.Take(13));
        Assert.Matches(@"\Ahttp://127\.0\.0\.1:\d+\z", Assert.Single(arguments.Skip(13)));
    }

    [Fact]
    public async Task Download_StaysNearTheLimit()
    {
        using var server = new FakeServer(200_000);
        using var fake = new FakeWinGet(Scenario(0, Installs(server)));
        await Upgrade(fake, server).RunAsync("Example.Editor", "2.5.0", new SpeedLimit(100), Recorded(), Ct);
        var installing = Seen(UpgradeStage.Installing)[0];
        // Over the bytes between progress reports, as the first can come late on a busy machine; the relay's own tests pin the
        // start with no burst.
        lock (_seen) Assert.InRange(Downloads.SpeedOf(_seen), 50, 125);
        // The server's answer holds its headers too.
        Assert.InRange(installing.Progress.BytesDownloaded, 200_000UL, 201_000UL);
    }

    [Fact]
    public async Task NewLimit_ReachesTheRunningDownload()
    {
        using var server = new FakeServer(2_000_000);
        using var fake = new FakeWinGet(Scenario(0, Installs(server)));
        var limit = new SpeedLimit(100);
        var lifted = 0;
        var outcome = await Upgrade(fake, server).RunAsync("Example.Editor", "2.5.0", limit, Recorded(p =>
        {
            if (p is { Stage: UpgradeStage.Downloading, BytesDownloaded: > 0 } && Interlocked.Exchange(ref lifted, 1) == 0) limit.Set(0);
        }), Ct);
        Assert.Equal(UpgradeResult.Updated, outcome.Result);
        // At 100 KB/s the 2 MB would take 20 s.
        Assert.True((Seen(UpgradeStage.Installing)[0].At - Seen(UpgradeStage.Downloading)[0].At).TotalSeconds < 8);
    }

    // The relay stops, so the download fails, and winget ends by itself, as it does when a download fails.
    [Fact]
    public async Task CancelWhileDownloading_FailsTheDownload_SoWinGetEndsBeforeItInstalls()
    {
        using var server = new FakeServer(2_000_000);
        using var fake = new FakeWinGet(Scenario(0, Installs(server)));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var outcome = await Upgrade(fake, server).RunAsync("Example.Editor", "2.5.0", new SpeedLimit(100), Recorded(p =>
        {
            if (p is { Stage: UpgradeStage.Downloading, BytesDownloaded: > 0 }) cancel.Cancel();
        }), cancel.Token);
        Assert.Equal(new UpgradeOutcome(UpgradeResult.Cancelled), outcome);
        Assert.True(fake.Touched("failed.txt"));
        await Task.Delay(500, Ct);
        Assert.False(fake.Touched("installed.txt"));
    }

    // Before the download, winget may already be installing a copy it kept, so it isn't ended: its download fails instead.
    [Fact]
    public async Task CancelBeforeTheDownload_FailsTheDownload_WithoutEndingWinGet()
    {
        using var server = new FakeServer(2_000_000);
        using var fake = new FakeWinGet(Scenario(0, [Say("Found Example Editor [Example.Editor] Version 2.5.0"), Sleep(1500), Touch("waited.txt"), .. Installs(server).Skip(1)]));
        using var cancel = new CancellationTokenSource();
        var run = Upgrade(fake, server).RunAsync("Example.Editor", "2.5.0", new SpeedLimit(100), Recorded(), cancel.Token);
        await cancel.CancelAsync();
        Assert.Equal(new UpgradeOutcome(UpgradeResult.Cancelled), await run.WaitAsync(Wait, Ct));
        Assert.True(fake.Touched("waited.txt") && fake.Touched("failed.txt"));
        Assert.False(fake.Touched("installed.txt"));
    }

    // Its sources come through the relay too, so a cancel while winget reads them ends the wait there.
    [Fact]
    public async Task CancelWhileWinGetReadsItsSources_EndsTheUpgradeThere()
    {
        using var server = new FakeServer(2_000_000);
        using var fake = new FakeWinGet(Scenario(0, [Fetch(server.Url().ToString()), .. Installs(server)]));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var clock = Stopwatch.StartNew();
        var outcome = await Upgrade(fake, server).RunAsync("Example.Editor", "2.5.0", new SpeedLimit(100), Recorded(p =>
        {
            if (p is { Stage: UpgradeStage.Queued, BytesDownloaded: > 0 }) cancel.Cancel();
        }), cancel.Token);
        Assert.Equal(new UpgradeOutcome(UpgradeResult.Cancelled), outcome);
        // At 100 KB/s, reading the sources alone would take 20 s.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(8), $"took {clock.Elapsed}");
        Assert.False(fake.Touched("installed.txt"));
    }

    // One that says its download failed, then hangs on instead of ending, is ended before it could install.
    [Fact]
    public async Task WinGetThatHangsOnAfterItsDownloadFailed_IsEnded()
    {
        using var server = new FakeServer(2_000_000);
        using var fake = new FakeWinGet(Scenario(0, [Say("Found Example Editor [Example.Editor] Version 2.5.0"), Download(server.Url().ToString(), stubborn: true), Touch("installed.txt")]));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var outcome = await Upgrade(fake, server, endAfter: TimeSpan.FromSeconds(1)).RunAsync("Example.Editor", "2.5.0", new SpeedLimit(100), Recorded(p =>
        {
            if (p is { Stage: UpgradeStage.Downloading, BytesDownloaded: > 0 }) cancel.Cancel();
        }), cancel.Token).WaitAsync(Wait, Ct);
        Assert.Equal(new UpgradeOutcome(UpgradeResult.Cancelled), outcome);
        Assert.True(fake.Touched("failed.txt"));
        Assert.False(fake.Touched("installed.txt"));
    }

    // The same once a download that starts after the cancel fails. A refused connection takes Windows about 2 s to fail.
    [Fact]
    public async Task WinGetThatHangsOnAfterALaterDownloadFailed_IsEnded()
    {
        using var server = new FakeServer(2_000_000);
        using var fake = new FakeWinGet(Scenario(0, [Say("Found Example Editor [Example.Editor] Version 2.5.0"), Sleep(500), Download(server.Url().ToString(), stubborn: true), Touch("installed.txt")]));
        using var cancel = new CancellationTokenSource();
        var run = Upgrade(fake, server, endAfter: TimeSpan.FromSeconds(5)).RunAsync("Example.Editor", "2.5.0", new SpeedLimit(100), Recorded(), cancel.Token);
        await cancel.CancelAsync();
        Assert.Equal(new UpgradeOutcome(UpgradeResult.Cancelled), await run.WaitAsync(Wait, Ct));
        Assert.True(fake.Touched("failed.txt"));
        Assert.False(fake.Touched("installed.txt"));
    }

    // With no download since the cancel, winget may be installing a copy it kept, with no program of its own: it's never ended.
    [Fact]
    public async Task CancelWhileAKeptCopyInstalls_NeverEndsWinGet()
    {
        using var server = new FakeServer(1000);
        using var fake = new FakeWinGet(Scenario(0, [Say("Found Example Editor [Example.Editor] Version 2.5.0"), Sleep(2500), Touch("installed.txt"), Say("Successfully installed")]));
        using var cancel = new CancellationTokenSource();
        var run = Upgrade(fake, server, endAfter: TimeSpan.FromSeconds(1)).RunAsync("Example.Editor", "2.5.0", new SpeedLimit(100), Recorded(), cancel.Token);
        await cancel.CancelAsync();
        Assert.Equal(new UpgradeOutcome(UpgradeResult.Updated), await run.WaitAsync(Wait, Ct));
        Assert.True(fake.Touched("installed.txt"));
    }

    [Fact]
    public async Task CancelWhileInstalling_LetsItFinish()
    {
        using var server = new FakeServer(1000);
        using var fake = new FakeWinGet(Scenario(0, [.. Installs(server).SkipLast(1), Sleep(1500), Say("Successfully installed")]));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var outcome = await Upgrade(fake, server).RunAsync("Example.Editor", "2.5.0", new SpeedLimit(100_000), Recorded(p =>
        {
            if (p.Stage == UpgradeStage.Installing) cancel.Cancel();
        }), cancel.Token);
        Assert.Equal(new UpgradeOutcome(UpgradeResult.Updated), outcome);
        Assert.True(fake.Touched("installed.txt"));
    }

    // winget prints an installer's help link right before it says the installer failed, much as it prints a download.
    private static object[] FailsInUse(FakeServer server, int pause = 0) =>
    [
        .. Installs(server).Take(4),
        Installer(1000),
        Say("The application is currently running. Exit the application then try again."),
        Say($"Related Link {server.Url()}"),
        Sleep(pause),
        Say("Installer failed with exit code: 1"),
    ];

    [Fact]
    public async Task LinkPrintedAsTheInstallerFails_IsntADownload()
    {
        using var server = new FakeServer(1000);
        using var fake = new FakeWinGet(Scenario(unchecked((int)0x8A150101), FailsInUse(server)));
        var outcome = await Upgrade(fake, server).RunAsync("Example.Editor", "2.5.0", new SpeedLimit(100_000), Recorded(), Ct);
        Assert.Equal((UpgradeResult.AppInUse, "0x8A150101"), (outcome.Result, outcome.Code));
        Assert.Equal([UpgradeStage.Queued, UpgradeStage.Downloading, UpgradeStage.Installing], Stages());
        // No HEAD for the link, even a late one.
        await Task.Delay(500, Ct);
        Assert.Equal(1, server.Heads);
    }

    // The cancel waits on the installer, which fails: that's the outcome, even when the link comes a moment before the rest.
    [Fact]
    public async Task CancelWaitingOnAnInstallerThatFails_KeepsItsOutcome()
    {
        using var server = new FakeServer(1000);
        using var fake = new FakeWinGet(Scenario(unchecked((int)0x8A150101), FailsInUse(server, pause: 50)));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var outcome = await Upgrade(fake, server).RunAsync("Example.Editor", "2.5.0", new SpeedLimit(100_000), Recorded(p =>
        {
            if (p.Stage == UpgradeStage.Installing) cancel.Cancel();
        }), cancel.Token);
        Assert.Equal((UpgradeResult.AppInUse, "0x8A150101"), (outcome.Result, outcome.Code));
    }

    [Fact]
    public async Task WebPageInsteadOfTheFile_LeavesTheSizeUnknown()
    {
        using var server = new FakeServer(50_000, "text/html");
        using var fake = new FakeWinGet(Scenario(0, Installs(server)));
        await Upgrade(fake, server).RunAsync("Example.Editor", "2.5.0", new SpeedLimit(100_000), Recorded(), Ct);
        Assert.All(Seen(UpgradeStage.Downloading), s => Assert.Equal((0UL, 0.0), (s.Progress.BytesRequired, s.Progress.DownloadFraction)));
    }

    [Fact]
    public async Task Dependency_RepeatsTheDownloadAndTheInstall()
    {
        using var server = new FakeServer(20_000);
        using var fake = new FakeWinGet(Scenario(0, [.. Installs(server), .. Installs(server)]));
        await Upgrade(fake, server).RunAsync("Example.Editor", "2.5.0", new SpeedLimit(100_000), Recorded(), Ct);
        Assert.Equal([UpgradeStage.Queued, UpgradeStage.Downloading, UpgradeStage.Installing, UpgradeStage.Downloading, UpgradeStage.Installing], Stages());
    }

    // No line comes between the download and the installer: the installer itself tells.
    [Fact]
    public async Task InstallerStartingAfterTheDownload_CountsAsInstalling()
    {
        using var server = new FakeServer(20_000);
        using var fake = new FakeWinGet(Scenario(0, Download(server.Url().ToString()), Installer(3000)));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var outcome = await Upgrade(fake, server).RunAsync("Example.Editor", "2.5.0", new SpeedLimit(100_000), Recorded(p =>
        {
            if (p.Stage == UpgradeStage.Installing) cancel.Cancel();
        }), cancel.Token);
        Assert.Equal(UpgradeResult.Updated, outcome.Result);
        Assert.Equal(UpgradeStage.Installing, Stages()[^1]);
    }

    // winget exits with success; only its words say the installer needs a restart (spec §6.3).
    [Fact]
    public async Task InstallerThatNeedsARestart_IsRestartNeeded()
    {
        using var server = new FakeServer(1000);
        using var fake = new FakeWinGet(Scenario(0, [.. Installs(server), Say("Restart your PC to finish installation.")]));
        var outcome = await Upgrade(fake, server).RunAsync("Example.Editor", "2.5.0", new SpeedLimit(100_000), null, Ct);
        Assert.Equal(UpgradeResult.RestartNeeded, outcome.Result);
    }

    // Its last line can come just after its exit.
    [Fact]
    public async Task RestartSaidAsItExits_IsStillSeen()
    {
        using var server = new FakeServer(1000);
        for (var i = 0; i < 5; i++)
        {
            using var fake = new FakeWinGet(Scenario(0, Say("Starting package install..."), Say("PC を再起動してインストールを完了します。")));
            var outcome = await Upgrade(fake, server).RunAsync("Example.Editor", "2.5.0", new SpeedLimit(100_000), null, Ct);
            Assert.Equal(UpgradeResult.RestartNeeded, outcome.Result);
        }
    }

    [Theory]
    [InlineData(0x8A150101u, UpgradeResult.AppInUse, UpgradeFailure.None)]
    [InlineData(0x8A150002u, UpgradeResult.Failed, UpgradeFailure.ProxyRefused)]
    [InlineData(0x8A150105u, UpgradeResult.Failed, UpgradeFailure.DiskFull)]
    public async Task ExitCode_IsTheOutcome(uint code, UpgradeResult result, UpgradeFailure failure)
    {
        using var server = new FakeServer(1000);
        using var fake = new FakeWinGet(Scenario(unchecked((int)code), Say("Found Example Editor [Example.Editor] Version 2.5.0")));
        var outcome = await Upgrade(fake, server).RunAsync("Example.Editor", "2.5.0", new SpeedLimit(100_000), null, Ct);
        Assert.Equal((result, failure, $"0x{code:X8}"), (outcome.Result, outcome.Failure, outcome.Code));
    }

    [Fact]
    public async Task WinGetThatIsntAppInstallers_FailsWithoutRunning()
    {
        using var server = new FakeServer(1000);
        using var fake = new FakeWinGet(Scenario(0, Installs(server)));
        var upgrade = new LimitedUpgrade(new WinGetCli(fake.Exe, _ => false), (url, ct) => DownloadSize.OfAsync(_http, url, ct), TimeProvider.System);
        Assert.Equal(new UpgradeOutcome(UpgradeResult.Failed, UpgradeFailure.WinGetUnavailable),
            await upgrade.RunAsync("Example.Editor", "2.5.0", new SpeedLimit(100_000), null, Ct));
        Assert.False(fake.Ran);
    }

    [Fact]
    public async Task Relay_GoesWithTheUpgrade()
    {
        using var server = new FakeServer(1000);
        using var fake = new FakeWinGet(Scenario(0, Installs(server)));
        await Upgrade(fake, server).RunAsync("Example.Editor", "2.5.0", new SpeedLimit(100_000), null, Ct);
        var proxy = new Uri(fake.Arguments[^1]);
        using var client = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(() => client.ConnectAsync(IPAddress.Loopback, proxy.Port, Ct).AsTask());
    }

    [Fact]
    public async Task CancelledBeforeItStarts_RunsNothing()
    {
        using var server = new FakeServer(1000);
        using var fake = new FakeWinGet(Scenario(0, Installs(server)));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        Assert.Equal(UpgradeResult.Cancelled, (await Upgrade(fake, server).RunAsync("Example.Editor", "2.5.0", new SpeedLimit(100_000), null, cancelled.Token)).Result);
        Assert.False(fake.Ran);
    }
}
