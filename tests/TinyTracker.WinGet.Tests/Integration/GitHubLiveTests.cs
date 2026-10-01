using System.Net.Http.Headers;
using TinyTracker.Core.Installing;
using TinyTracker.Core.SelfUpdate;
using TinyTracker.WinGet.SelfUpdate;
using TinyTracker.WinGet.Tests.SelfUpdate;
using Xunit;

namespace TinyTracker.WinGet.Tests.Integration;

// GitHub's real latest release of Tiny Tracker, read only, on CI (spec §12): the release the self-update would take, with its one
// Setup downloaded through GitHub's real redirect and checked against the release's digest. Skipped until the first release.
public sealed class GitHubLiveTests : IDisposable
{
    private readonly string _app = Path.Combine(Path.GetTempPath(), "tinytracker-tests-" + Guid.NewGuid().ToString("N"));

    public GitHubLiveTests() => Directory.CreateDirectory(_app);

    public void Dispose() => Directory.Delete(_app, recursive: true);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task LatestRelease_HasTheSetupTheSelfUpdateTakes()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("TINYTRACKER_GITHUB_LIVE") == "1", "The live check of GitHub's releases runs on CI.");
        using var http = new HttpClient(new ApiToken(Environment.GetEnvironmentVariable("GH_TOKEN")) { InnerHandler = new SocketsHttpHandler { AllowAutoRedirect = false } });
        var releases = new GitHubReleases(http, TimeProvider.System);

        var latest = await releases.LatestAsync(Ct);
        Assert.SkipWhen(latest is null, "Tiny Tracker has no release yet.");
        var system = new StartedSetups();
        var outcome = await new SetupHandOff(releases, http, _app, system, TimeProvider.System).RunAsync(latest.Version, new SpeedLimit(), new Reported<UpgradeProgress>(_ => { }), Ct);

        Assert.Equal(UpgradeResult.Updated, outcome.Result);
        Assert.Single(system.Started);
    }

    // The job's token for GitHub's API, so the runners' shared limit can't fail the check. Nothing else gets it.
    private sealed class ApiToken(string? token) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (token is { Length: > 0 } && request.RequestUri?.Host == "api.github.com") request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
