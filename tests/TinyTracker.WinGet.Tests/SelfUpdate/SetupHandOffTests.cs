using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using TinyTracker.Core.Installing;
using TinyTracker.Core.SelfUpdate;
using TinyTracker.WinGet.SelfUpdate;
using TinyTracker.WinGet.Tests.ReleaseDates;
using Xunit;

namespace TinyTracker.WinGet.Tests.SelfUpdate;

// The helper's self-update: GitHub's release checked, its Setup downloaded into the update folder and checked, then started with
// the file held against writes (spec §6.5).
public sealed class SetupHandOffTests : IDisposable
{
    private const string Api = "https://api.github.com/repos/Bikuuuu/tiny-tracker/releases/tags/v0.2.0";
    private const string Download = "https://github.com/Bikuuuu/tiny-tracker/releases/download/v0.2.0/TinyTracker-Setup-0.2.0-x64.exe";
    private const string Storage = "https://release-assets.githubusercontent.com/github-production-release-asset/1/2?sig=x";
    private static readonly SelfVersion Version = new(0, 2, 0);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly byte[] Setup = Bytes(300 * 1024, 1);

    private readonly string _app = Path.Combine(Path.GetTempPath(), "tinytracker-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 28, 8, 0, 0, TimeSpan.Zero));
    private readonly FakeSystem _system = new();
    private readonly List<UpgradeProgress> _progress = [];
    private readonly List<HttpClient> _clients = [];

    public SetupHandOffTests() => Directory.CreateDirectory(_app);

    public void Dispose()
    {
        _clients.ForEach(c => c.Dispose());
        if (Directory.Exists(_app)) Directory.Delete(_app, recursive: true);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string SetupPath => Path.Combine(_app, "update", "TinyTracker-Setup-0.2.0-x64.exe");

    private static byte[] Bytes(int count, int seed)
    {
        var bytes = new byte[count];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static string Release(byte[] setup, bool immutable = true) => new JsonObject
    {
        ["tag_name"] = "v0.2.0",
        ["draft"] = false,
        ["prerelease"] = false,
        ["immutable"] = immutable,
        ["published_at"] = "2026-09-20T10:05:00Z",
        ["assets"] = new JsonArray(new JsonObject
        {
            ["name"] = "TinyTracker-Setup-0.2.0-x64.exe",
            ["state"] = "uploaded",
            ["size"] = setup.Length,
            ["digest"] = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(setup)),
            ["browser_download_url"] = Download,
        }),
    }.ToJsonString();

    private static Task<HttpResponseMessage> Moved(string to, UriKind kind = UriKind.Absolute)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(to, kind);
        return Task.FromResult(response);
    }

    private static Task<HttpResponseMessage> Served(byte[] bytes) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });

    // GitHub as it answers: the release, a redirect from github.com to its storage, then the file.
    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> GitHub(byte[]? served = null, string? release = null) => (request, _) =>
        request.RequestUri!.AbsoluteUri switch
        {
            Api => FakeHttp.Text(release ?? Release(Setup)),
            Download => Moved(Storage),
            Storage => Served(served ?? Setup),
            _ => FakeHttp.Status(HttpStatusCode.NotFound),
        };

    private (SetupHandOff HandOff, FakeHttp Http) Create(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer, TimeProvider? time = null)
    {
        var http = new FakeHttp(answer);
        var client = new HttpClient(http);
        _clients.Add(client);
        return (new SetupHandOff(new GitHubReleases(client, time ?? _time), client, _app, _system, time ?? _time), http);
    }

    private Task<UpgradeOutcome> Run(SetupHandOff handOff, SpeedLimit? limit = null, CancellationToken? ct = null) =>
        handOff.RunAsync(Version, limit ?? new SpeedLimit(), new Reported<UpgradeProgress>(p =>
        {
            lock (_progress) _progress.Add(p);
        }), ct ?? Ct);

    // Advances the fake clock until the run ends.
    private async Task<UpgradeOutcome> Finish(Task<UpgradeOutcome> run, TimeSpan step)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!run.IsCompleted)
        {
            Assert.True(DateTime.UtcNow < deadline, "The self-update didn't end.");
            _time.Advance(step);
            await Task.Delay(5, Ct);
        }
        return await run;
    }

    [Fact]
    public async Task GoodRelease_IsDownloadedChecked_AndStartedHeldAgainstWrites()
    {
        var (handOff, http) = Create(GitHub());
        Assert.Equal(new UpgradeOutcome(UpgradeResult.Updated), await Run(handOff));
        var started = Assert.Single(_system.Started);
        Assert.Equal(SetupPath, started.Path);
        Assert.Equal(["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CLOSEAPPLICATIONS"], started.Arguments);
        Assert.Equal(Setup, started.Content);
        Assert.False(started.Writable);
        Assert.Equal([Api, Download, Storage], http.Requests.Select(u => u.AbsoluteUri));
        lock (_progress) Assert.Equal(new UpgradeProgress(UpgradeStage.Downloading, (ulong)Setup.Length, (ulong)Setup.Length, 1, 0), _progress[^1]);
    }

    // Setup must replace this folder's exes, so another account's copy blocks it.
    [Fact]
    public async Task CopyForAnotherAccount_StopsIt_BeforeAnyDownload()
    {
        _system.OtherAccounts = true;
        var (handOff, http) = Create(GitHub());
        Assert.Equal(new UpgradeOutcome(UpgradeResult.Failed, UpgradeFailure.OtherAccounts), await Run(handOff));
        Assert.Equal(Path.Combine(_app, "TinyTracker.exe"), Assert.Single(_system.Looked));
        Assert.Empty(http.Requests);
        Assert.Empty(_system.Started);
    }

    [Fact]
    public async Task UpdateFolder_IsEmptiedFirst()
    {
        var update = Path.Combine(_app, "update");
        Directory.CreateDirectory(Path.Combine(update, "old"));
        File.WriteAllText(Path.Combine(update, "TinyTracker-Setup-0.1.9-x64.exe"), "old");
        File.WriteAllText(Path.Combine(update, "old", "left.txt"), "old");
        var (handOff, _) = Create(GitHub());
        await Run(handOff);
        Assert.Equal([SetupPath], Directory.GetFileSystemEntries(update));
    }

    // Only administrators can make it one, but a link is never followed.
    [Fact]
    public async Task UpdateFolderThatsALink_IsReplaced_AndWhatItPointsToStays()
    {
        var elsewhere = Path.Combine(_app, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, "keep.txt"), "keep");
        using (var link = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", Path.Combine(_app, "update"), elsewhere]) { CreateNoWindow = true, UseShellExecute = false })!)
            await link.WaitForExitAsync(Ct);
        var (handOff, _) = Create(GitHub());
        Assert.Equal(UpgradeResult.Updated, (await Run(handOff)).Result);
        Assert.False(new DirectoryInfo(Path.Combine(_app, "update")).Attributes.HasFlag(FileAttributes.ReparsePoint));
        Assert.Equal([Path.Combine(elsewhere, "keep.txt")], Directory.GetFileSystemEntries(elsewhere));
    }

    // An earlier Setup that still runs holds its file: the folder can't be emptied, so the helper is busy, and nothing starts.
    [Fact]
    public async Task UpdateFolderInUse_IsBusy()
    {
        var old = Path.Combine(_app, "update", "TinyTracker-Setup-0.1.9-x64.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(old)!);
        File.WriteAllText(old, "old");
        using (new FileStream(old, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var (handOff, _) = Create(GitHub());
            Assert.Equal(UpgradeResult.Busy, (await Run(handOff)).Result);
        }
        Assert.Empty(_system.Started);
    }

    [Fact]
    public async Task FileThatDoesntMatchGitHubsDigest_IsDeleted_AndNeverRun()
    {
        var (handOff, _) = Create(GitHub(served: Bytes(Setup.Length, 2)));
        Assert.Equal(UpgradeFailure.DigestMismatch, (await Run(handOff)).Failure);
        Assert.False(File.Exists(SetupPath));
        Assert.Empty(_system.Started);
    }

    [Fact]
    public async Task MoreBytesThanGitHubRecords_DontMatch()
    {
        var (handOff, _) = Create(GitHub(served: [.. Setup, 0]));
        Assert.Equal(UpgradeFailure.DigestMismatch, (await Run(handOff)).Failure);
        Assert.False(File.Exists(SetupPath));
    }

    // A server that never stops can't fill the disk.
    [Fact]
    public async Task EndlessDownload_StopsPastGitHubsSize()
    {
        var (handOff, _) = Create((request, ct) => request.RequestUri!.AbsoluteUri == Storage
            ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new EndlessStream()) })
            : GitHub()(request, ct));
        Assert.Equal(UpgradeFailure.DigestMismatch, (await Run(handOff).WaitAsync(Wait, Ct)).Failure);
        Assert.False(File.Exists(SetupPath));
    }

    [Fact]
    public async Task HandlerThatFollowedARedirectItself_IsRefused()
    {
        var (handOff, _) = Create((request, ct) => request.RequestUri!.AbsoluteUri == Download
            ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Setup), RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://example.com/setup.exe") })
            : GitHub()(request, ct));
        Assert.Equal(UpgradeFailure.DownloadFailed, (await Run(handOff)).Failure);
        Assert.Empty(_system.Started);
    }

    [Fact]
    public async Task DownloadThatEndsEarly_Failed()
    {
        var (handOff, _) = Create(GitHub(served: Setup[..1000]));
        Assert.Equal(UpgradeFailure.DownloadFailed, (await Run(handOff)).Failure);
        Assert.False(File.Exists(SetupPath));
        Assert.Empty(_system.Started);
    }

    // Only GitHub's own hosts, over TLS, on their own port.
    [Theory]
    [InlineData("https://example.com/TinyTracker-Setup-0.2.0-x64.exe")]
    [InlineData("http://release-assets.githubusercontent.com/github-production-release-asset/1/2")]
    [InlineData("https://release-assets.githubusercontent.com:8443/github-production-release-asset/1/2")]
    [InlineData("https://githubusercontent.com.example.com/1/2")]
    [InlineData("https://examplegithubusercontent.com/1/2")]
    public async Task RedirectOffGitHub_IsNeverFollowed(string to)
    {
        var (handOff, http) = Create((request, ct) => request.RequestUri!.AbsoluteUri == Download ? Moved(to) : GitHub()(request, ct));
        Assert.Equal(UpgradeFailure.DownloadFailed, (await Run(handOff)).Failure);
        Assert.Equal([Api, Download], http.Requests.Select(u => u.AbsoluteUri));
        Assert.Empty(_system.Started);
    }

    // A relative one goes on from the host that sent it.
    [Fact]
    public async Task RelativeRedirect_IsFollowedFromItsHost()
    {
        const string moved = "https://github.com/Bikuuuu/tiny-tracker/releases/download/v0.2.0/moved/TinyTracker-Setup-0.2.0-x64.exe";
        var (handOff, http) = Create((request, ct) => request.RequestUri!.AbsoluteUri switch
        {
            Download => Moved("moved/TinyTracker-Setup-0.2.0-x64.exe", UriKind.Relative),
            moved => Served(Setup),
            _ => GitHub()(request, ct),
        });
        Assert.Equal(new UpgradeOutcome(UpgradeResult.Updated), await Run(handOff));
        Assert.Equal([Api, Download, moved], http.Requests.Select(u => u.AbsoluteUri));
    }

    [Fact]
    public async Task EndlessRedirects_Failed()
    {
        var (handOff, _) = Create((request, ct) => request.RequestUri!.AbsoluteUri == Api ? GitHub()(request, ct) : Moved(Download));
        Assert.Equal(UpgradeFailure.DownloadFailed, (await Run(handOff)).Failure);
    }

    [Fact]
    public async Task GitHubThatDoesntAnswer_IsUnreachable()
    {
        var (handOff, _) = Create((_, _) => FakeHttp.Status(HttpStatusCode.ServiceUnavailable));
        Assert.Equal(UpgradeFailure.GitHubUnreachable, (await Run(handOff)).Failure);
    }

    [Fact]
    public async Task ReleaseThatIsntImmutable_IsRefused_BeforeAnyDownload()
    {
        var (handOff, http) = Create(GitHub(release: Release(Setup, immutable: false)));
        var outcome = await Run(handOff);
        Assert.Equal((UpgradeFailure.ReleaseRefused, "not immutable"), (outcome.Failure, outcome.Code));
        Assert.Equal([Api], http.Requests.Select(u => u.AbsoluteUri));
    }

    [Fact]
    public async Task Cancel_StopsTheDownload_AndDeletesWhatCame()
    {
        var halfway = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (handOff, _) = Create((request, ct) => request.RequestUri!.AbsoluteUri == Storage
            ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StuckStream(Setup[..1000], halfway)) })
            : GitHub()(request, ct));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = Run(handOff, ct: cancel.Token);
        await halfway.Task.WaitAsync(Wait, Ct);
        await cancel.CancelAsync();
        Assert.Equal(UpgradeResult.Cancelled, (await run.WaitAsync(Wait, Ct)).Result);
        Assert.False(File.Exists(SetupPath));
        Assert.Empty(_system.Started);
    }

    [Fact]
    public async Task DownloadThatStops_Stalls_After2Minutes()
    {
        var halfway = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (handOff, _) = Create((request, ct) => request.RequestUri!.AbsoluteUri == Storage
            ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StuckStream(Setup[..1000], halfway)) })
            : GitHub()(request, ct));
        var run = Run(handOff);
        await halfway.Task.WaitAsync(Wait, Ct);
        _time.Advance(SetupHandOff.StallAfter - TimeSpan.FromSeconds(1));
        await Task.Delay(50, Ct);
        Assert.False(run.IsCompleted);
        Assert.Equal(UpgradeFailure.Stalled, (await Finish(run, TimeSpan.FromSeconds(1))).Failure);
        Assert.False(File.Exists(SetupPath));
    }

    // Under the limit, 12.5 MB takes over two minutes: that's slow, not stalled.
    [Fact]
    public async Task SlowDownloadThatMoves_NeverStalls()
    {
        var big = Bytes(12_800 * 1024, 3);
        var (handOff, _) = Create(GitHub(served: big, release: Release(big)));
        Assert.Equal(UpgradeResult.Updated, (await Finish(Run(handOff, new SpeedLimit(100)), TimeSpan.FromSeconds(1))).Result);
    }

    // The queue's helper, just hung up on, exits within moments; Setup must replace it.
    [Fact]
    public async Task OtherHelper_IsWaitedFor_ThenSetupStarts()
    {
        _system.OtherHelperLooks = 3;
        var (handOff, _) = Create(GitHub());
        Assert.Equal(UpgradeResult.Updated, (await Finish(Run(handOff), TimeSpan.FromSeconds(1))).Result);
        Assert.Equal(Path.Combine(_app, "TinyTracker.Helper.exe"), _system.HelperLookedFor);
        Assert.Single(_system.Started);
    }

    [Fact]
    public async Task OtherHelperThatStays_IsBusy_AndNothingStarts()
    {
        _system.OtherHelperLooks = int.MaxValue;
        var (handOff, _) = Create(GitHub());
        Assert.Equal(UpgradeResult.Busy, (await Finish(Run(handOff), TimeSpan.FromSeconds(1))).Result);
        Assert.Empty(_system.Started);
        Assert.False(File.Exists(SetupPath));
    }

    [Fact]
    public async Task CancelWhileAnotherHelperRuns_StartsNothing()
    {
        _system.OtherHelperLooks = int.MaxValue;
        var (handOff, _) = Create(GitHub());
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = Run(handOff, ct: cancel.Token);
        while (_system.HelperLookedFor is null) await Task.Delay(5, Ct);
        await cancel.CancelAsync();
        Assert.Equal(UpgradeResult.Cancelled, (await run.WaitAsync(Wait, Ct)).Result);
        Assert.Empty(_system.Started);
        Assert.False(File.Exists(SetupPath));
    }

    [Fact]
    public async Task SetupThatWontStart_Failed_AndIsDeleted()
    {
        _system.StartFails = true;
        var (handOff, _) = Create(GitHub());
        var outcome = await Run(handOff);
        Assert.Equal((UpgradeResult.Failed, UpgradeFailure.InstallerFailed), (outcome.Result, outcome.Failure));
        Assert.False(File.Exists(SetupPath));
    }

    // Held back by the speed limit, the download moves only as the clock does, and follows a change at once.
    [Fact]
    public async Task Download_FollowsTheSpeedLimit()
    {
        var limit = new SpeedLimit(100);
        var (handOff, _) = Create(GitHub());
        var run = Run(handOff, limit);
        await Task.Delay(300, Ct);
        Assert.False(run.IsCompleted);
        limit.Set(0);
        Assert.Equal(UpgradeResult.Updated, (await Finish(run, TimeSpan.FromMilliseconds(100))).Result);
    }

    [Fact]
    public async Task UnlimitedDownload_NeedsNoClock()
    {
        var (handOff, _) = Create(GitHub());
        Assert.Equal(UpgradeResult.Updated, (await Run(handOff).WaitAsync(Wait, Ct)).Result);
    }

    // Gives its bytes, then blocks until the read is cancelled.
    private sealed class StuckStream(byte[] first, TaskCompletionSource reached) : Stream
    {
        private int _given;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_given < first.Length)
            {
                var count = Math.Min(buffer.Length, first.Length - _given);
                first.AsMemory(_given, count).CopyTo(buffer);
                _given += count;
                return count;
            }
            reached.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // Gives bytes for as long as it's read.
    private sealed class EndlessStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Fill(buffer, (byte)7, offset, count);
            return count;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // Windows as the test sets it. Start records what it started, and whether the file could be written to meanwhile.
    private sealed class FakeSystem : ISetupSystem
    {
        private int _helperLooks;

        public bool OtherAccounts { get; set; }
        public int OtherHelperLooks { set => _helperLooks = value; }
        public bool StartFails { get; set; }
        public List<string> Looked { get; } = [];
        public string? HelperLookedFor { get; private set; }
        public List<(string Path, string[] Arguments, byte[] Content, bool Writable)> Started { get; } = [];

        public bool RunsInAnotherSession(string exe)
        {
            Looked.Add(exe);
            return OtherAccounts;
        }

        public bool OtherCopiesRun(string helper)
        {
            HelperLookedFor = helper;
            return Interlocked.Decrement(ref _helperLooks) >= 0;
        }

        public void Start(string setup, IReadOnlyList<string> arguments)
        {
            bool writable;
            try
            {
                using (File.Open(setup, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) writable = true;
            }
            catch (IOException)
            {
                writable = false;
            }
            Started.Add((setup, [.. arguments], File.ReadAllBytes(setup), writable));
            if (StartFails) throw new Win32Exception(2);
        }
    }
}
