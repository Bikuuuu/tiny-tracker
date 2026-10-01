using System.Net;
using Microsoft.Extensions.Time.Testing;
using TinyTracker.WinGet.ReleaseDates;
using Xunit;

namespace TinyTracker.WinGet.Tests.ReleaseDates;

public sealed class GitHubReleaseDatesTests : IDisposable
{
    private const string Yaml = "PackageIdentifier: Mozilla.Firefox\nPackageVersion: 131.0\nReleaseDate: 2026-09-20\nManifestType: installer\n";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 25, 8, 0, 0, TimeSpan.Zero));
    private readonly List<HttpClient> _clients = [];

    public void Dispose() => _clients.ForEach(c => c.Dispose());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private (GitHubReleaseDates Dates, FakeHttp Http) Create(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer)
    {
        var http = new FakeHttp(answer);
        var client = new HttpClient(http);
        _clients.Add(client);
        return (new GitHubReleaseDates(client, _time), http);
    }

    [Fact]
    public async Task ReleaseDate_ComesFromTheInstallerManifest()
    {
        var (dates, http) = Create((_, _) => FakeHttp.Text(Yaml));
        Assert.Equal(new DateOnly(2026, 9, 20), await dates.GetAsync("Mozilla.Firefox", "131.0", Ct));
        Assert.Equal(
            "https://raw.githubusercontent.com/microsoft/winget-pkgs/master/manifests/m/Mozilla/Firefox/131.0/Mozilla.Firefox.installer.yaml",
            Assert.Single(http.Requests).AbsoluteUri);
    }

    [Fact]
    public async Task SameVersion_IsAskedOnce()
    {
        var (dates, http) = Create((_, _) => FakeHttp.Text(Yaml));
        await dates.GetAsync("Mozilla.Firefox", "131.0", Ct);
        Assert.Equal(new DateOnly(2026, 9, 20), await dates.GetAsync("Mozilla.Firefox", "131.0", Ct));
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task MissingManifest_IsNullAndAskedOnce()
    {
        var (dates, http) = Create((_, _) => FakeHttp.Status(HttpStatusCode.NotFound));
        Assert.Null(await dates.GetAsync("Mozilla.Firefox", "131.0", Ct));
        Assert.Null(await dates.GetAsync("Mozilla.Firefox", "131.0", Ct));
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task ManifestWithoutADate_IsNull()
    {
        var (dates, _) = Create((_, _) => FakeHttp.Text("PackageIdentifier: Mozilla.Firefox\nManifestType: installer\n"));
        Assert.Null(await dates.GetAsync("Mozilla.Firefox", "131.0", Ct));
    }

    [Fact]
    public async Task NetworkFailure_PausesRequestsForTenMinutes()
    {
        var calls = 0;
        var (dates, http) = Create((_, _) => Interlocked.Increment(ref calls) == 1
            ? Task.FromException<HttpResponseMessage>(new HttpRequestException("offline"))
            : FakeHttp.Text(Yaml));
        Assert.Null(await dates.GetAsync("Mozilla.Firefox", "131.0", Ct));
        Assert.Null(await dates.GetAsync("VideoLAN.VLC", "3.0.21", Ct));
        Assert.Single(http.Requests);
        _time.Advance(GitHubReleaseDates.Backoff);
        Assert.Equal(new DateOnly(2026, 9, 20), await dates.GetAsync("Mozilla.Firefox", "131.0", Ct));
    }

    [Fact]
    public async Task ServerError_PausesRequestsToo()
    {
        var (dates, http) = Create((_, _) => FakeHttp.Status(HttpStatusCode.ServiceUnavailable));
        Assert.Null(await dates.GetAsync("Mozilla.Firefox", "131.0", Ct));
        Assert.Null(await dates.GetAsync("VideoLAN.VLC", "3.0.21", Ct));
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task SlowServer_GivesUpAfterTenSeconds()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (dates, _) = Create(async (_, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var pending = dates.GetAsync("Mozilla.Firefox", "131.0", Ct);
        await entered.Task.WaitAsync(Wait, Ct);
        _time.Advance(GitHubReleaseDates.RequestTimeout);
        Assert.Null(await pending.WaitAsync(Wait, Ct));
    }

    [Fact]
    public async Task CallerCancellation_IsNotSwallowed_AndAsksNothing()
    {
        var (dates, http) = Create((_, _) => FakeHttp.Text(Yaml));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dates.GetAsync("Mozilla.Firefox", "131.0", cancelled.Token));
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task ConcurrentAsks_ShareOneRequest()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (dates, http) = Create(async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Yaml) };
        });
        var first = dates.GetAsync("Mozilla.Firefox", "131.0", Ct);
        await entered.Task.WaitAsync(Wait, Ct);
        var second = dates.GetAsync("Mozilla.Firefox", "131.0", Ct);
        release.SetResult();
        Assert.Equal([new DateOnly(2026, 9, 20), new DateOnly(2026, 9, 20)], await Task.WhenAll(first, second).WaitAsync(Wait, Ct));
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task CallerThatGivesUp_DoesNotFailTheOthers()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestToken = CancellationToken.None;
        var (dates, http) = Create(async (_, ct) =>
        {
            requestToken = ct;
            entered.TrySetResult();
            await release.Task;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Yaml) };
        });
        using var leaving = new CancellationTokenSource();
        var first = dates.GetAsync("Mozilla.Firefox", "131.0", leaving.Token);
        await entered.Task.WaitAsync(Wait, Ct);
        var second = dates.GetAsync("Mozilla.Firefox", "131.0", Ct);
        await leaving.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(Wait, Ct));
        Assert.False(requestToken.IsCancellationRequested);
        release.SetResult();
        Assert.Equal(new DateOnly(2026, 9, 20), await second.WaitAsync(Wait, Ct));
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task InvalidId_MakesNoRequest()
    {
        var (dates, http) = Create((_, _) => FakeHttp.Text(Yaml));
        Assert.Null(await dates.GetAsync("../Firefox", "131.0", Ct));
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task OddFirstCharacter_IsEscapedNotThrown()
    {
        var (dates, http) = Create((_, _) => FakeHttp.Status(HttpStatusCode.NotFound));
        Assert.Null(await dates.GetAsync("%Example.Tool", "1.0", Ct));
        Assert.Equal(
            "https://raw.githubusercontent.com/microsoft/winget-pkgs/master/manifests/%25/%25Example/Tool/1.0/%25Example.Tool.installer.yaml",
            Assert.Single(http.Requests).AbsoluteUri);
    }
}
