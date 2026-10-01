using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using TinyTracker.Core.SelfUpdate;
using TinyTracker.WinGet.SelfUpdate;
using TinyTracker.WinGet.Tests.ReleaseDates;
using Xunit;

namespace TinyTracker.WinGet.Tests.SelfUpdate;

// Tiny Tracker's releases, as GitHub's API tells them (spec §6.5).
public sealed class GitHubReleasesTests : IDisposable
{
    private const string Hex = "0362a383ed217d4c4239b5933866dd96d3eb2102737da92f80f6057a4b40df2f";
    private const string Download = "https://github.com/Bikuuuu/tiny-tracker/releases/download/v0.2.0/TinyTracker-Setup-0.2.0-x64.exe";
    private static readonly SelfVersion Version = new(0, 2, 0);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 28, 8, 0, 0, TimeSpan.Zero));
    private readonly List<HttpClient> _clients = [];

    public void Dispose() => _clients.ForEach(c => c.Dispose());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private (GitHubReleases Releases, FakeHttp Http) Create(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer)
    {
        var http = new FakeHttp(answer);
        var client = new HttpClient(http);
        _clients.Add(client);
        return (new GitHubReleases(client, _time), http);
    }

    // A release as GitHub's API returns it, changed as a test needs.
    private static string Release(Action<JsonObject>? release = null, Action<JsonObject>? asset = null)
    {
        var setup = new JsonObject
        {
            ["url"] = "https://api.github.com/repos/Bikuuuu/tiny-tracker/releases/assets/2",
            ["id"] = 2,
            ["name"] = "TinyTracker-Setup-0.2.0-x64.exe",
            ["content_type"] = "application/x-msdownload",
            ["state"] = "uploaded",
            ["size"] = 48_234_567,
            ["digest"] = "sha256:" + Hex,
            ["browser_download_url"] = Download,
        };
        asset?.Invoke(setup);
        var json = new JsonObject
        {
            ["url"] = "https://api.github.com/repos/Bikuuuu/tiny-tracker/releases/1",
            ["html_url"] = "https://github.com/Bikuuuu/tiny-tracker/releases/tag/v0.2.0",
            ["id"] = 1,
            ["tag_name"] = "v0.2.0",
            ["name"] = "Tiny Tracker 0.2.0",
            ["draft"] = false,
            ["prerelease"] = false,
            ["immutable"] = true,
            ["created_at"] = "2026-09-20T10:00:00Z",
            ["published_at"] = "2026-09-20T10:05:00Z",
            ["assets"] = new JsonArray(setup),
        };
        release?.Invoke(json);
        return json.ToJsonString();
    }

    [Fact]
    public async Task Latest_IsTheNewestPublishedRelease()
    {
        var (releases, http) = Create((_, _) => FakeHttp.Text(Release()));
        Assert.Equal(new SelfRelease(Version, new DateTimeOffset(2026, 9, 20, 10, 5, 0, TimeSpan.Zero)), await releases.LatestAsync(Ct));
        Assert.Equal("https://api.github.com/repos/Bikuuuu/tiny-tracker/releases/latest", Assert.Single(http.Requests).AbsoluteUri);
    }

    [Fact]
    public async Task NoReleaseYet_IsNone()
    {
        var (releases, _) = Create((_, _) => FakeHttp.Status(HttpStatusCode.NotFound));
        Assert.Null(await releases.LatestAsync(Ct));
    }

    // GitHub refuses requests that don't say who asks.
    [Fact]
    public async Task Requests_SayWhoAsks_AndAskForGitHubsJson()
    {
        HttpRequestMessage? seen = null;
        var (releases, _) = Create((request, _) =>
        {
            seen = request;
            return FakeHttp.Text(Release());
        });
        await releases.LatestAsync(Ct);
        Assert.Equal("TinyTracker", Assert.Single(seen!.Headers.UserAgent).Product?.Name);
        Assert.Equal("application/vnd.github+json", Assert.Single(seen.Headers.Accept).MediaType);
    }

    public static TheoryData<HttpStatusCode> NoAnswers() => new() { HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests, HttpStatusCode.InternalServerError, HttpStatusCode.BadGateway };

    [Theory]
    [MemberData(nameof(NoAnswers))]
    public async Task ServerThatWontAnswer_IsUnreachable(HttpStatusCode status)
    {
        var (releases, _) = Create((_, _) => FakeHttp.Status(status));
        Assert.Equal(GitHubProblem.Unreachable, (await Assert.ThrowsAsync<GitHubException>(() => releases.LatestAsync(Ct))).Problem);
        Assert.Equal(GitHubProblem.Unreachable, (await Assert.ThrowsAsync<GitHubException>(() => releases.AssetAsync(Version, Ct))).Problem);
    }

    [Fact]
    public async Task NoNetwork_IsUnreachable()
    {
        var (releases, _) = Create((_, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("offline")));
        Assert.Equal(GitHubProblem.Unreachable, (await Assert.ThrowsAsync<GitHubException>(() => releases.LatestAsync(Ct))).Problem);
    }

    [Fact]
    public async Task SlowServer_IsUnreachableAfter30Seconds()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (releases, _) = Create(async (_, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var pending = releases.LatestAsync(Ct);
        await entered.Task.WaitAsync(Wait, Ct);
        _time.Advance(GitHubReleases.RequestTimeout);
        Assert.Equal(GitHubProblem.Unreachable, (await Assert.ThrowsAsync<GitHubException>(() => pending.WaitAsync(Wait, Ct))).Problem);
    }

    [Fact]
    public async Task CallerCancellation_IsNotUnreachable()
    {
        var (releases, _) = Create((_, ct) => Task.FromCanceled<HttpResponseMessage>(ct));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => releases.LatestAsync(cancelled.Token));
    }

    public static TheoryData<string> OddLatest() => new()
    {
        Release(r => r["tag_name"] = "v0.2.0-beta"),
        Release(r => r["tag_name"] = "latest"),
        Release(r => r["draft"] = true),
        Release(r => r["prerelease"] = true),
        Release(r => r.Remove("published_at")),
        "[]",
        "{ not json",
    };

    [Theory]
    [MemberData(nameof(OddLatest))]
    public async Task OddLatest_IsRefused(string body)
    {
        var (releases, _) = Create((_, _) => FakeHttp.Text(body));
        Assert.Equal(GitHubProblem.Refused, (await Assert.ThrowsAsync<GitHubException>(() => releases.LatestAsync(Ct))).Problem);
    }

    [Fact]
    public async Task Asset_IsThatReleasesSetup_WithGitHubsSizeAndDigest()
    {
        var (releases, http) = Create((_, _) => FakeHttp.Text(Release()));
        var asset = await releases.AssetAsync(Version, Ct);
        Assert.Equal((Download, 48_234_567L, Hex), (asset.Download.AbsoluteUri, asset.Size, Convert.ToHexStringLower(asset.Sha256)));
        Assert.Equal("https://api.github.com/repos/Bikuuuu/tiny-tracker/releases/tags/v0.2.0", Assert.Single(http.Requests).AbsoluteUri);
    }

    [Fact]
    public async Task ReleaseThatIsntThere_IsRefused()
    {
        var (releases, _) = Create((_, _) => FakeHttp.Status(HttpStatusCode.NotFound));
        Assert.Equal(GitHubProblem.Refused, (await Assert.ThrowsAsync<GitHubException>(() => releases.AssetAsync(Version, Ct))).Problem);
    }

    // The helper installs only what passes every check (spec §6.5).
    public static TheoryData<string, string> Refusals() => new()
    {
        { "not immutable", Release(r => r["immutable"] = false) },
        { "no immutable flag", Release(r => r.Remove("immutable")) },
        { "a draft", Release(r => r["draft"] = true) },
        { "a pre-release", Release(r => r["prerelease"] = true) },
        { "another tag", Release(r => r["tag_name"] = "v0.3.0") },
        { "no assets", Release(r => r.Remove("assets")) },
        { "another asset only", Release(asset: a => a["name"] = "TinyTracker-Setup-0.2.0-arm64.exe") },
        { "two of it", Release(r => r["assets"] = new JsonArray(JsonNode.Parse(Release())!["assets"]![0]!.DeepClone(), JsonNode.Parse(Release())!["assets"]![0]!.DeepClone())) },
        { "not uploaded", Release(asset: a => a["state"] = "starter") },
        { "no digest", Release(asset: a => a.Remove("digest")) },
        { "a null digest", Release(asset: a => a["digest"] = null) },
        { "another hash", Release(asset: a => a["digest"] = "sha512:" + Hex + Hex) },
        { "a short digest", Release(asset: a => a["digest"] = "sha256:" + Hex[..63]) },
        { "an empty file", Release(asset: a => a["size"] = 0) },
        { "200 MB", Release(asset: a => a["size"] = 200L * 1024 * 1024) },
        { "a download elsewhere", Release(asset: a => a["browser_download_url"] = "https://example.com/TinyTracker-Setup-0.2.0-x64.exe") },
        { "a plain http download", Release(asset: a => a["browser_download_url"] = "http://github.com/Bikuuuu/tiny-tracker/releases/download/v0.2.0/TinyTracker-Setup-0.2.0-x64.exe") },
        { "another repository", Release(asset: a => a["browser_download_url"] = "https://github.com/Example/tiny-tracker/releases/download/v0.2.0/TinyTracker-Setup-0.2.0-x64.exe") },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task ReleaseThatFailsACheck_IsRefused(string what, string body)
    {
        var (releases, _) = Create((_, _) => FakeHttp.Text(body));
        var refused = await Assert.ThrowsAsync<GitHubException>(() => releases.AssetAsync(Version, Ct));
        Assert.True(refused.Problem == GitHubProblem.Refused, what);
    }

    [Fact]
    public async Task HugeAnswer_IsRefused()
    {
        var (releases, _) = Create((_, _) => FakeHttp.Text(Release(r => r["body"] = new string('x', 2 * 1024 * 1024))));
        Assert.Equal(GitHubProblem.Refused, (await Assert.ThrowsAsync<GitHubException>(() => releases.AssetAsync(Version, Ct))).Problem);
    }
}
