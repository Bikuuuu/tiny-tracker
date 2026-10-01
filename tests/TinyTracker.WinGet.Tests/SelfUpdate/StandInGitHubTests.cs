using TinyTracker.Core.Installing;
using TinyTracker.Core.SelfUpdate;
using TinyTracker.WinGet.SelfUpdate;
using Xunit;

namespace TinyTracker.WinGet.Tests.SelfUpdate;

// The stand-in GitHub that the runner's self-update test sets before the installed app (spec §12), checked with the real release
// reader and download: its hosts are reached on this machine's loopback, and only its own certificate is taken.
public sealed class StandInGitHubTests : IDisposable
{
    private static readonly SelfVersion Version = new(0, 2, 0);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "tinytracker-tests-" + Guid.NewGuid().ToString("N"));
    private readonly StartedSetups _system = new();

    public StandInGitHubTests() => Directory.CreateDirectory(Path.Combine(_folder, "app"));

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string App => Path.Combine(_folder, "app");

    private string NewSetup()
    {
        var setup = Path.Combine(_folder, "setup.exe");
        var bytes = new byte[300 * 1024];
        new Random(3).NextBytes(bytes);
        File.WriteAllBytes(setup, bytes);
        return setup;
    }

    private Task<UpgradeOutcome> HandOffAsync(HttpClient http) =>
        new SetupHandOff(new GitHubReleases(http, TimeProvider.System), http, App, _system, TimeProvider.System)
            .RunAsync(Version, new SpeedLimit(), new Reported<UpgradeProgress>(_ => { }), Ct);

    [Fact]
    public async Task ReleaseReaderAndDownload_GetItsSetup_ThroughItsRedirect()
    {
        await using var github = StandInGitHub.Start();
        var setup = NewSetup();
        github.Serve(Version, setup);
        using var http = github.Client();

        var latest = await new GitHubReleases(http, TimeProvider.System).LatestAsync(Ct);
        var outcome = await HandOffAsync(http);

        Assert.Equal(Version, latest?.Version);
        Assert.Equal(UpgradeResult.Updated, outcome.Result);
        Assert.Equal(File.ReadAllBytes(setup), Assert.Single(_system.Started));
        Assert.Equal(
        [
            "api.github.com /repos/Bikuuuu/tiny-tracker/releases/latest",
            "api.github.com /repos/Bikuuuu/tiny-tracker/releases/tags/v0.2.0",
            "github.com /Bikuuuu/tiny-tracker/releases/download/v0.2.0/TinyTracker-Setup-0.2.0-x64.exe",
            "release-assets.githubusercontent.com",
        ], github.Requests.Select((r, i) => i < 3 ? $"{r.Host} {r.Path}" : r.Host));
    }

    [Fact]
    public async Task WrongDigest_IsRefused_AndStartsNothing()
    {
        await using var github = StandInGitHub.Start();
        github.Serve(Version, NewSetup(), digestMatches: false);
        using var http = github.Client();

        var outcome = await HandOffAsync(http);

        Assert.Equal((UpgradeResult.Failed, UpgradeFailure.DigestMismatch), (outcome.Result, outcome.Failure));
        Assert.Empty(_system.Started);
        Assert.Empty(Directory.GetFiles(Path.Combine(App, "update")));
    }

    [Fact]
    public async Task NothingServed_IsNoReleaseYet()
    {
        await using var github = StandInGitHub.Start();
        using var http = github.Client();

        Assert.Null(await new GitHubReleases(http, TimeProvider.System).LatestAsync(Ct));
    }
}
