using System.Diagnostics;
using System.Security.Principal;
using TinyTracker.Core.Elevation;
using TinyTracker.Core.Installing;
using TinyTracker.Core.Tracking;
using TinyTracker.Core.Versions;
using TinyTracker.WinGet.Cli;
using TinyTracker.WinGet.Elevation;
using Xunit;

namespace TinyTracker.WinGet.Tests.Integration;

// Real upgrades under the speed limit, only on GitHub runners, once the CI job has put the old versions back (spec §12).
// Runners are elevated, so these turn winget's proxy option on themselves.
[Collection<UpgradeCollection>]
public class SpeedLimitRunnerTests
{
    private const string NotepadPlusPlus = "Notepad++.Notepad++";
    private const string Vlc = "VideoLAN.VLC";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);
    // VLC's mirrors are of all speeds, and some stall: a try past this is cancelled, and the next one gets another mirror.
    private static readonly TimeSpan TryLimit = TimeSpan.FromMinutes(5);
    private const int Tries = 3;
    private static readonly string HelperPath = HelperExe.Location;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void RunnerOnly() =>
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" && Environment.GetEnvironmentVariable("TINYTRACKER_WINGET_UPGRADE_TESTS") == "1",
            "Speed limit upgrade tests run only on GitHub runners.");

    private static async Task ProxyOptionOnAsync() => Assert.Equal(0, (await WinGetCli.Real.RunAsync(["settings", "--enable", "ProxyCommandLineOptions"], Ct))?.ExitCode);

    // The helper runs with this test's own rights, elevated on runners.
    private static async Task<HelperClient> StartHelperAsync(SpeedLimit limit)
    {
        var pipe = HelperRules.NewPipeName();
        using var process = Process.Start(HelperPath, ["--pipe", pipe, "--user", WindowsIdentity.GetCurrent().User!.Value]);
        return await HelperClient.ConnectAsync(pipe, HelperPath, Environment.IsPrivilegedProcess, Wait, Ct, limit);
    }

    private static async Task<InstalledPackage> InstalledAsync(string id)
    {
        var session = await WinGetSession.OpenAsync(Ct);
        return Assert.Single(await session.FindInstalledByIdAsync([id], details: false, Ct), p => string.Equals(p.CatalogId, id, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<(UpgradeOutcome Outcome, List<(TimeSpan, UpgradeProgress)> Seen)> UpgradeVlcAsync(HelperClient client, string version)
    {
        for (var attempt = 1; attempt <= Tries; attempt++)
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            stop.CancelAfter(TryLimit);
            var seen = new List<(TimeSpan, UpgradeProgress)>();
            var clock = Stopwatch.StartNew();
            var outcome = await client.UpgradeAsync(new PackageKey(Vlc, "winget"), version, new Reported<UpgradeProgress>(p =>
            {
                lock (seen) seen.Add((clock.Elapsed, p));
            }), stop.Token);
            if (outcome.Result is not UpgradeResult.Cancelled || Ct.IsCancellationRequested) return (outcome, seen);
            lock (seen) Note($"Try {attempt} of {Tries} ran past {TryLimit.TotalMinutes} minutes, at {seen.Select(s => s.Item2.BytesDownloaded).DefaultIfEmpty().Max() / 1024} KB.");
        }
        throw new TimeoutException($"VLC's download ran past {TryLimit.TotalMinutes} minutes on each of {Tries} tries.");
    }

    private static void Note(string message) => TestContext.Current.TestOutputHelper?.WriteLine(message);

    [Fact]
    public async Task LimitedUpgradeInTheApp_StaysNearTheLimit_AndThenNothingNewerIsOffered()
    {
        RunnerOnly();
        await ProxyOptionOnAsync();
        var before = await InstalledAsync(NotepadPlusPlus);
        var seen = new List<(TimeSpan, UpgradeProgress)>();
        var clock = Stopwatch.StartNew();
        var upgrader = new WinGetUpgrader(new SpeedLimit(1000));
        var outcome = await upgrader.UpgradeAsync(new PackageKey(NotepadPlusPlus, "winget"), before.LatestVersion!, new Reported<UpgradeProgress>(p =>
        {
            lock (seen) seen.Add((clock.Elapsed, p));
        }), Ct);
        Assert.True(outcome.Result is UpgradeResult.Updated or UpgradeResult.RestartNeeded, outcome.ToString());
        lock (seen)
        {
            Assert.InRange(Downloads.SpeedOf(seen), 300, 1250);
            Assert.Contains(seen, s => s.Item2.Stage == UpgradeStage.Installing);
        }
        var after = await InstalledAsync(NotepadPlusPlus);
        Assert.True(PackageVersion.Same(before.LatestVersion, after.Version));
        // winget's guard refuses what isn't newer, and says what's installed.
        var again = await upgrader.UpgradeAsync(new PackageKey(NotepadPlusPlus, "winget"), before.LatestVersion!, null, Ct);
        Assert.Equal((UpgradeResult.NoUpdate, $"installed {after.Version}"), (again.Result, again.Code));
    }

    [Fact]
    public async Task LimitedUpgradeThroughTheHelper_CancelsCleanly_ThenStaysNearTheLimit()
    {
        RunnerOnly();
        await ProxyOptionOnAsync();
        var before = await InstalledAsync(Vlc);
        var limit = new SpeedLimit(4000);
        using (var client = await StartHelperAsync(limit))
        {
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            cancel.CancelAfter(TryLimit);
            var fired = 0;
            var cancelled = await client.UpgradeAsync(new PackageKey(Vlc, "winget"), before.LatestVersion!, new Reported<UpgradeProgress>(p =>
            {
                if (p is { Stage: UpgradeStage.Downloading, BytesDownloaded: > 1_000_000 } && Interlocked.Exchange(ref fired, 1) == 0) _ = Task.Run(cancel.Cancel, Ct);
            }), cancel.Token);
            Assert.True(cancelled.Result is UpgradeResult.Cancelled, cancelled.ToString());
            if (fired == 0) Note($"The first download ran past {TryLimit.TotalMinutes} minutes before 1 MB.");
            Assert.True(PackageVersion.Same(before.Version, (await InstalledAsync(Vlc)).Version));
            // VLC comes from mirrors of all speeds, some far below any floor, so only the ceiling counts here; Notepad++'s test has
            // the floor.
            limit.Set(1000);
            var (outcome, seen) = await UpgradeVlcAsync(client, before.LatestVersion!);
            Assert.True(outcome.Result is UpgradeResult.Updated or UpgradeResult.RestartNeeded, outcome.ToString());
            lock (seen) Assert.InRange(Downloads.SpeedOf(seen), 0, 1250);
        }
        Assert.True(PackageVersion.Same(before.LatestVersion, (await InstalledAsync(Vlc)).Version));
    }

    // Off first, so the helper has something to do. Runners are elevated, so its launcher shows no prompt.
    [Fact]
    public async Task ProxyOption_TurnsOnThroughTheHelper()
    {
        RunnerOnly();
        Assert.Equal(0, (await WinGetCli.Real.RunAsync(["settings", "--disable", "ProxyCommandLineOptions"], Ct))?.ExitCode);
        Assert.False(await WinGetSettings.ProxyOptionAsync(WinGetCli.Real, Ct));
        var option = new ProxyOption(WinGetCli.Real, new HelperLauncher(HelperPath, () => false, () => 0, demo: false));
        Assert.Equal((SwitchResult.Done, (string?)null), await option.TurnOnAsync(Ct));
        Assert.True(await option.IsOnAsync(Ct));
    }
}

