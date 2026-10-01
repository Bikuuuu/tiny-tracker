using System.ComponentModel;
using System.Net;
using System.Security.Cryptography;
using TinyTracker.Core.Installing;
using TinyTracker.Core.SelfUpdate;
using TinyTracker.WinGet.Storage;
using TinyTracker.WinGet.Throttling;

namespace TinyTracker.WinGet.SelfUpdate;

// What the self-update asks of Windows; the tests fake it.
public interface ISetupSystem
{
    // That exe runs in another session: another account's copy, whose files Setup couldn't replace.
    bool RunsInAnotherSession(string exe);

    // Copies of that exe run besides this process.
    bool OtherCopiesRun(string exe);

    // Starts Setup, elevated as this process is. Throws when it can't.
    void Start(string setup, IReadOnlyList<string> arguments);
}

// The helper's self-update (spec §6.5): the release checked, Setup downloaded to the admin-only folder at the limit, matched to
// GitHub's digest and started while held against writes; Updated means it started. download mustn't follow redirects itself.
public sealed class SetupHandOff(GitHubReleases releases, HttpClient download, string appFolder, ISetupSystem system, TimeProvider time)
{
    public static readonly IReadOnlyList<string> Arguments = ["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CLOSEAPPLICATIONS"];
    public static readonly TimeSpan StallAfter = TimeSpan.FromMinutes(2);
    // The queue's helper, just hung up on, exits within moments.
    public static readonly TimeSpan HelpersWait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HelpersEvery = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ReportEvery = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaxSleep = TimeSpan.FromMilliseconds(100);
    private const int MaxRedirects = 5;
    private const int DiskFull = unchecked((int)0x80070070);
    private const int HandleDiskFull = unchecked((int)0x80070027);

    // Where Setup is downloaded to and runs from, which only admins may write.
    public static string FolderIn(string appFolder) => Path.Combine(appFolder, "update");

    public async Task<UpgradeOutcome> RunAsync(SelfVersion version, SpeedLimit limit, IProgress<UpgradeProgress> progress, CancellationToken ct)
    {
        if (system.RunsInAnotherSession(Path.Combine(appFolder, "TinyTracker.exe"))) return Failed(UpgradeFailure.OtherAccounts);
        SelfAsset asset;
        try
        {
            asset = await releases.AssetAsync(version, ct);
        }
        catch (GitHubException e)
        {
            return Failed(e.Problem == GitHubProblem.Unreachable ? UpgradeFailure.GitHubUnreachable : UpgradeFailure.ReleaseRefused, e.Message);
        }
        catch (OperationCanceledException)
        {
            return new UpgradeOutcome(UpgradeResult.Cancelled);
        }
        var folder = FolderIn(appFolder);
        var path = Path.Combine(folder, GitHubReleases.SetupName(version));
        try
        {
            // A link is replaced, never followed.
            FolderTree.Delete(folder);
            Directory.CreateDirectory(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // An earlier Setup that still runs holds its file.
            return new UpgradeOutcome(UpgradeResult.Busy, Code: $"0x{e.HResult:X8}");
        }
        var outcome = await DownloadAsync(asset, path, limit, progress, ct) ?? await StartAsync(path, ct);
        if (outcome.Result != UpgradeResult.Updated) TryDelete(path);
        return outcome;
    }

    // Null once the file holds exactly what GitHub records.
    private async Task<UpgradeOutcome?> DownloadAsync(SelfAsset asset, string path, SpeedLimit limit, IProgress<UpgradeProgress> progress, CancellationToken ct)
    {
        using var stalled = new CancellationTokenSource(StallAfter, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, stalled.Token);
        var bucket = new TokenBucket(time);
        var following = new Lock();
        void Follow(object? sender, EventArgs e)
        {
            lock (following) bucket.BytesPerSecond = limit.BytesPerSecond;
        }
        limit.Changed += Follow;
        Follow(null, EventArgs.Empty);
        try
        {
            using var response = await GetAsync(asset.Download, linked.Token);
            await using var source = await response.Content.ReadAsStreamAsync(linked.Token);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var total = (ulong)asset.Size;
            ulong done = 0;
            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                var buffer = new byte[TokenBucket.ChunkSize(0)];
                var reported = time.GetTimestamp();
                progress.Report(Downloading(0, total));
                int read;
                while ((read = await source.ReadAsync(buffer.AsMemory(0, TokenBucket.ChunkSize(bucket.BytesPerSecond)), linked.Token)) > 0)
                {
                    stalled.CancelAfter(StallAfter);
                    done += (ulong)read;
                    if (done > total) return Failed(UpgradeFailure.DigestMismatch, "more bytes than GitHub records");
                    hash.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), linked.Token);
                    // Short sleeps, so a change of the limit applies within about 0.1 s.
                    for (var wait = bucket.Take(read); wait > TimeSpan.Zero; wait = bucket.PendingDelay())
                        await Task.Delay(wait < MaxSleep ? wait : MaxSleep, time, linked.Token);
                    if (time.GetElapsedTime(reported) < ReportEvery) continue;
                    progress.Report(Downloading(done, total));
                    reported = time.GetTimestamp();
                }
                file.Flush(flushToDisk: true);
            }
            if (done < total) return Failed(UpgradeFailure.DownloadFailed, "the download ended early");
            progress.Report(Downloading(done, total));
            return hash.GetHashAndReset().AsSpan().SequenceEqual(asset.Sha256) ? null : Failed(UpgradeFailure.DigestMismatch);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new UpgradeOutcome(UpgradeResult.Cancelled);
        }
        catch (OperationCanceledException)
        {
            return Failed(UpgradeFailure.Stalled);
        }
        catch (Refusal e)
        {
            return Failed(UpgradeFailure.DownloadFailed, e.Message);
        }
        catch (HttpRequestException e)
        {
            return Failed(UpgradeFailure.DownloadFailed, e.Message);
        }
        catch (IOException e)
        {
            return Failed(e.HResult is DiskFull or HandleDiskFull ? UpgradeFailure.DiskFull : UpgradeFailure.DownloadFailed, $"0x{e.HResult:X8}");
        }
        finally
        {
            limit.Changed -= Follow;
        }
    }

    // The file, through redirects to GitHub's own hosts only.
    private async Task<HttpResponseMessage> GetAsync(Uri url, CancellationToken ct)
    {
        for (var hops = 0; ; hops++)
        {
            if (!IsGitHubs(url)) throw new Refusal("a redirect to " + url.Host);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            var response = await download.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.RequestMessage?.RequestUri is { } answered && answered != url)
            {
                response.Dispose();
                throw new Refusal("a redirect it followed by itself");
            }
            if (response.StatusCode is HttpStatusCode.OK) return response;
            var location = response.Headers.Location;
            var status = (int)response.StatusCode;
            response.Dispose();
            if (status is not (301 or 302 or 303 or 307 or 308) || location is null) throw new Refusal($"GitHub answered {status}");
            if (hops == MaxRedirects) throw new Refusal("too many redirects");
            url = location.IsAbsoluteUri ? location : new Uri(url, location);
        }
    }

    // Held against writes until Setup has started, so what runs is what was checked. Setup must replace the helper too.
    private async Task<UpgradeOutcome> StartAsync(string path, CancellationToken ct)
    {
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var helper = Path.Combine(appFolder, "TinyTracker.Helper.exe");
        var since = time.GetTimestamp();
        try
        {
            while (system.OtherCopiesRun(helper))
            {
                if (time.GetElapsedTime(since) >= HelpersWait) return new UpgradeOutcome(UpgradeResult.Busy, Code: "another helper runs");
                await Task.Delay(HelpersEvery, time, ct);
            }
        }
        catch (OperationCanceledException)
        {
            return new UpgradeOutcome(UpgradeResult.Cancelled);
        }
        if (ct.IsCancellationRequested) return new UpgradeOutcome(UpgradeResult.Cancelled);
        try
        {
            system.Start(path, Arguments);
            return new UpgradeOutcome(UpgradeResult.Updated);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
        {
            return Failed(UpgradeFailure.InstallerFailed, $"0x{e.HResult:X8}");
        }
    }

    // GitHub's own hosts, over TLS on its port.
    private static bool IsGitHubs(Uri url) =>
        url.Scheme == Uri.UriSchemeHttps && url.IsDefaultPort && url.UserInfo.Length == 0
        && (url.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || url.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    private static UpgradeProgress Downloading(ulong done, ulong total) => new(UpgradeStage.Downloading, done, total, (double)done / total, 0);

    private static UpgradeOutcome Failed(UpgradeFailure failure, string? code = null) => new(UpgradeResult.Failed, failure, code);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed class Refusal(string why) : Exception(why);
}
