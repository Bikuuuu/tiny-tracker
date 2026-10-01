using TinyTracker.Core.Installing;
using TinyTracker.Core.Tracking;
using TinyTracker.WinGet.Throttling;

namespace TinyTracker.WinGet.Cli;

// An upgrade through winget's command line, its downloads through a relay for this winget only, held to the limit (spec §6.4).
// relayPorts: where the relay may connect, for tests; endAfter: how long winget has to end once a download fails after a cancel.
public sealed class LimitedUpgrade(WinGetCli cli, Func<Uri, CancellationToken, Task<ulong>> sizeOf, TimeProvider time, IReadOnlySet<int>? relayPorts = null,
    TimeSpan? endAfter = null)
{
    // How often the bytes through the relay are looked at.
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(250);
    // And winget's own processes, which takes longer.
    private static readonly TimeSpan ChildrenEvery = TimeSpan.FromSeconds(1);
    // How long its output may go on after it exits.
    private static readonly TimeSpan Drain = TimeSpan.FromSeconds(1);
    private readonly TimeSpan _endAfter = endAfter ?? TimeSpan.FromSeconds(10);

    public static LimitedUpgrade Real { get; } = new(WinGetCli.Real, (url, ct) => DownloadSize.OfAsync(DownloadSize.Shared, url, ct), TimeProvider.System);

    // Like IPackageUpgrader: never throws. A cancel stops the relay, so winget's downloads fail and it ends by itself before it
    // installs anything more. An installer already running finishes, and its outcome stands. One that doesn't end is ended.
    public async Task<UpgradeOutcome> RunAsync(string id, string version, SpeedLimit limit, IProgress<UpgradeProgress>? progress, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return new UpgradeOutcome(UpgradeResult.Cancelled);
        var served = 0;
        await using var relay = new ThrottlingRelay(time, ThrottlingRelay.Only(() => Volatile.Read(ref served)), relayPorts);
        var following = new Lock();
        void Follow(object? sender, EventArgs e)
        {
            lock (following) relay.LimitBytesPerSecond = limit.BytesPerSecond;
        }
        limit.Changed += Follow;
        try
        {
            Follow(null, EventArgs.Empty);
            relay.Start();
            using var winget = cli.Start(
                ["upgrade", "--id", id, "--exact", "--source", TrackedApp.WinGet, "--version", version, "--silent", "--accept-package-agreements", "--accept-source-agreements",
                    "--disable-interactivity", "--proxy", relay.ProxyUri.ToString().TrimEnd('/')],
                process => Volatile.Write(ref served, process));
            if (winget is null) return new UpgradeOutcome(UpgradeResult.Failed, UpgradeFailure.WinGetUnavailable);
            return await FollowAsync(winget, relay, progress, ct);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.Net.Sockets.SocketException)
        {
            return new UpgradeOutcome(UpgradeResult.Failed, UpgradeFailure.Other, $"0x{e.HResult:X8}");
        }
        finally
        {
            limit.Changed -= Follow;
        }
    }

    // Waiting until winget says it downloads the installer, downloading until its next line, or until it starts the installer,
    // then installing. A dependency goes through the same.
    private async Task<UpgradeOutcome> FollowAsync(WinGetProcess winget, ThrottlingRelay relay, IProgress<UpgradeProgress>? progress, CancellationToken ct)
    {
        var never = new TaskCompletionSource().Task;
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = ct.Register(() => cancelled.TrySetResult());
        Task cancelling = cancelled.Task;
        long? cancelledAt = null;
        // Since a cancel, when a download began to fail; winget has endAfter from then to end by itself.
        long? failing = null;
        var stage = UpgradeStage.Queued;
        var from = 0L;
        ulong required = 0;
        Task<ulong>? size = null;
        // An address winget printed counts as a download once bytes move or a tick passes with no other line: an installer's help
        // link comes right before the line saying it failed.
        (Uri Url, long From, long At)? address = null;
        var before = 0L;
        var childrenSeen = 0L;
        var ended = false;
        var restart = false;
        UpgradeProgress? shown = Progress(stage, 0, 0, 0);
        progress?.Report(shown.Value);
        Task more = winget.Lines.WaitToReadAsync(CancellationToken.None).AsTask();
        void Show()
        {
            if (ended) return;
            var now = Progress(stage, relay.BytesDownloaded, from, required);
            if (now == shown) return;
            shown = now;
            progress?.Report(now);
        }
        // Shown at once, as a short download may be over before the next look.
        void Downloading((Uri Url, long From, long At) found)
        {
            (stage, from, required, size) = (UpgradeStage.Downloading, found.From, 0, sizeOf(found.Url, ct));
            Show();
        }
        while (true)
        {
            await Task.WhenAny(winget.Exited, more, cancelling, Task.Delay(Tick, time, CancellationToken.None));
            if (cancelling.IsCompleted)
            {
                relay.Refuse();
                cancelledAt = time.GetTimestamp();
                if (stage == UpgradeStage.Downloading || address is not null) failing = cancelledAt;
                cancelling = never;
            }
            while (winget.Lines.TryRead(out var line))
            {
                restart |= WinGetOutput.AsksForRestart(line);
                if (address is { } last && relay.BytesDownloaded > last.From) Downloading(last);
                address = null;
                if (WinGetOutput.DownloadOf(line) is { } url)
                {
                    address = (url, before, time.GetTimestamp());
                    if (cancelledAt is not null) failing ??= address.Value.At;
                }
                else if (stage == UpgradeStage.Downloading) stage = UpgradeStage.Installing;
            }
            if (address is { } next && (relay.BytesDownloaded > next.From || time.GetElapsedTime(next.At) >= Tick))
            {
                Downloading(next);
                address = null;
            }
            before = relay.BytesDownloaded;
            if (size is { IsCompleted: true })
            {
                if (size.IsCompletedSuccessfully && stage == UpgradeStage.Downloading) required = size.Result;
                size = null;
            }
            if (stage != UpgradeStage.Installing && time.GetElapsedTime(childrenSeen) >= ChildrenEvery)
            {
                childrenSeen = time.GetTimestamp();
                if (winget.HasChildren()) stage = UpgradeStage.Installing;
            }
            // Whatever it printed since, unless it runs an installer.
            if (failing is { } since && !ended && time.GetElapsedTime(since) >= _endAfter && !winget.HasChildren())
            {
                winget.End();
                ended = true;
            }
            Show();
            if (winget.Exited.IsCompleted) break;
            // Once its output has ended, only its exit is left to wait for.
            if (more.IsCompleted) more = more is Task<bool> { IsCompletedSuccessfully: true, Result: true } ? winget.Lines.WaitToReadAsync(CancellationToken.None).AsTask() : never;
        }
        // Its last lines, such as the restart one, can come just after its exit; a program that took its output may keep it open.
        using (var drain = new CancellationTokenSource(Drain, time))
        {
            try
            {
                await foreach (var line in winget.Lines.ReadAllAsync(drain.Token)) restart |= WinGetOutput.AsksForRestart(line);
            }
            catch (OperationCanceledException) { }
        }
        var outcome = ErrorMap.ForExitCode(await winget.Exited);
        if (restart && outcome.Result == UpgradeResult.Updated) outcome = new UpgradeOutcome(UpgradeResult.RestartNeeded);
        // What the cancel's failed downloads ended it with; an installer's own outcome stands.
        return cancelledAt is not null && outcome is { Result: UpgradeResult.Failed, Failure: UpgradeFailure.DownloadFailed or UpgradeFailure.Other }
            ? new UpgradeOutcome(UpgradeResult.Cancelled)
            : outcome;
    }

    // Before the download, bytes still move as winget reads its catalog; they keep the queue's stall watch quiet.
    private static UpgradeProgress Progress(UpgradeStage stage, long through, long from, ulong required)
    {
        if (stage == UpgradeStage.Queued) return new UpgradeProgress(UpgradeStage.Queued, (ulong)through, 0, 0, 0);
        var done = (ulong)Math.Max(0, through - from);
        if (required > 0) done = Math.Min(done, required);
        return stage == UpgradeStage.Downloading
            ? new UpgradeProgress(UpgradeStage.Downloading, done, required, required > 0 ? (double)done / required : 0, 0)
            : new UpgradeProgress(UpgradeStage.Installing, done, required, 1, 0);
    }
}
