using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;
using TinyTracker.Core.Checking;
using TinyTracker.Core.History;
using TinyTracker.Core.Installing;
using TinyTracker.Core.Logging;
using TinyTracker.Core.Storage;
using TinyTracker.Core.Tracking;
using Xunit;

namespace TinyTracker.Core.Tests.Installing;

public sealed class InstallQueueTests : IAsyncDisposable
{
    private const ulong MB = 1024 * 1024;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly TrackedApp Firefox = new() { Id = "Mozilla.Firefox", Source = "winget", Name = "Firefox", Offer = new Offer { Version = "131.0", FirstSeen = DateTimeOffset.UnixEpoch } };
    private static readonly TrackedApp Vlc = new() { Id = "VideoLAN.VLC", Source = "winget", Name = "VLC", Offer = new Offer { Version = "3.0.21", FirstSeen = DateTimeOffset.UnixEpoch } };

    private readonly TempFolder _folder = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 25, 8, 0, 0, TimeSpan.Zero));
    private readonly FakeUpgrader _upgrader = new();
    private readonly FakeSource _source = new();
    private readonly Channel<InstallItem> _items = Channel.CreateUnbounded<InstallItem>();
    private readonly SettingsStore _settings;
    private readonly HistoryStore _history;
    private readonly FileLog _log;
    private readonly InstallQueue _queue;

    public InstallQueueTests()
    {
        _log = new FileLog(_folder.PathOf("app.log"), _time);
        _settings = new SettingsStore(_folder.PathOf("settings.json"));
        _settings.Update(f => f with { Apps = [Firefox, Vlc] });
        _history = new HistoryStore(_folder.PathOf("history.json"), _time);
        _source.Reply = apps => Installed(apps[0].Id == Vlc.Id ? Vlc : Firefox, apps[0].Id == Vlc.Id ? "3.0.21" : "131.0");
        _queue = Queue(_history);
    }

    // A download the test left running is cancelled; its last log line must not bring the folder back.
    public async ValueTask DisposeAsync()
    {
        _queue.Dispose();
        await _queue.Stopped.WaitAsync(Wait);
        _log.Close();
        _folder.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private InstallQueue Queue(HistoryStore history)
    {
        var queue = new InstallQueue(_upgrader, _source, _settings, history, _time, _log);
        queue.Changed += (_, item) => _items.Writer.TryWrite(item);
        return queue;
    }

    private static InstallRequest Request(TrackedApp app) =>
        new(new PackageKey(app.Id, app.Source), app.Name, app.Id == Vlc.Id ? "3.0.20" : "130.0", app.Offer!.Version);

    private static CatalogRead Installed(TrackedApp app, string installed, string? available = null) =>
        new([new PackageSnapshot(app.Id, app.Source, app.Name, installed, available)], []);

    private async Task<InstallItem> NextItem() => await _items.Reader.ReadAsync(Ct).AsTask().WaitAsync(Wait, Ct);

    private async Task<InstallItem> NextDone()
    {
        while (true)
        {
            var item = await NextItem();
            if (item.Stage == InstallStage.Done) return item;
        }
    }

    private async Task<InstallItem> Next(Func<InstallItem, bool> match)
    {
        while (true)
        {
            var item = await NextItem();
            if (match(item)) return item;
        }
    }

    private async Task<UpgradeCall> Start(params TrackedApp[] apps)
    {
        _queue.Enqueue(apps.Select(Request));
        return await _upgrader.NextCall(Ct);
    }

    [Fact]
    public async Task Enqueue_ReportsWaitingRightAway()
    {
        _queue.Enqueue([Request(Firefox), Request(Vlc)]);
        var first = await NextItem();
        var second = await NextItem();
        Assert.Equal([(Firefox.Id, InstallStage.Waiting), (Vlc.Id, InstallStage.Waiting)], [(first.Request.Package.Id, first.Stage), (second.Request.Package.Id, second.Stage)]);
    }

    [Fact]
    public async Task Upgrade_AsksForTheOfferedVersion()
    {
        var call = await Start(Firefox);
        Assert.Equal((new PackageKey("Mozilla.Firefox", "winget"), "131.0"), (call.Package, call.Version));
    }

    [Fact]
    public async Task Updated_IsConfirmedSavedAndWrittenToHistory()
    {
        var call = await Start(Firefox);
        call.Download(50 * MB);
        call.Install();
        call.Finish(UpgradeResult.Updated);

        var done = (await NextDone()).Done!;
        Assert.Equal(UpgradeResult.Updated, done.Outcome.Result);
        Assert.False(done.Phantom);
        Assert.Equal(AppStatus.UpToDate, done.After!.Status);
        Assert.Null(_settings.Current.Apps.Single(a => a.Id == Firefox.Id).Offer);
        var entry = Assert.Single(_history.Entries);
        Assert.Equal((HistoryResult.Updated, "130.0", "131.0", null), (entry.Result, entry.FromVersion, entry.ToVersion, entry.Reason));
    }

    [Fact]
    public async Task Progress_ShowsTheStageAndTheSpeed()
    {
        var call = await Start(Firefox);
        call.Download(0);
        _time.Advance(TimeSpan.FromSeconds(1));
        call.Download(10 * MB);
        var downloading = await Next(i => i.Progress.BytesDownloaded == 10 * MB);
        Assert.Equal(InstallStage.Downloading, downloading.Stage);
        Assert.Equal(10 * MB, downloading.BytesPerSecond, 3);
        call.Install(0.5);
        Assert.Equal(InstallStage.Installing, (await Next(i => i.Stage != InstallStage.Downloading)).Stage);
    }

    [Fact]
    public async Task SpeedFallsToZero_WhenBytesStopComing()
    {
        var call = await Start(Firefox);
        call.Download(0);
        _time.Advance(TimeSpan.FromSeconds(1));
        call.Download(10 * MB);
        await Next(i => i.Progress.BytesDownloaded == 10 * MB);
        var speeds = new List<double>();
        for (var second = 0; second < 3; second++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            speeds.Add((await NextItem()).BytesPerSecond);
        }
        Assert.Equal([5 * MB, 10 * MB / 3.0, 0], speeds);
        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.False(_items.Reader.TryRead(out _));
    }

    [Fact]
    public async Task NoSpeedItems_AfterTheDownloadEnds()
    {
        var call = await Start(Firefox);
        call.Download(0);
        _time.Advance(TimeSpan.FromSeconds(1));
        call.Download(10 * MB);
        call.Install();
        await Next(i => i.Stage == InstallStage.Installing);
        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.False(_items.Reader.TryRead(out _));
    }

    [Fact]
    public async Task LongQueuedWait_SaysAnotherInstallIsRunning_WithoutCancelling()
    {
        var call = await Start(Firefox);
        call.Queue();
        _time.Advance(InstallTimings.Default.StallAfter - TimeSpan.FromSeconds(1));
        while (_items.Reader.TryRead(out var early)) Assert.False(early.Busy);
        _time.Advance(TimeSpan.FromSeconds(1));
        var busy = await NextItem();
        Assert.Equal((InstallStage.Waiting, true), (busy.Stage, busy.Busy));
        Assert.False(call.Token.IsCancellationRequested);
        call.Queue();
        Assert.True((await NextItem()).Busy);
        call.Download(10 * MB);
        Assert.False((await Next(i => i.Stage == InstallStage.Downloading)).Busy);
    }

    [Fact]
    public async Task RetryThatWaitsInTheQueue_IsNotASecondStall()
    {
        var call = await Start(Firefox);
        call.Download(10 * MB);
        await Next(i => i.Stage == InstallStage.Downloading);
        _time.Advance(InstallTimings.Default.StallAfter);
        var retry = await _upgrader.NextCall(Ct);
        _time.Advance(InstallTimings.Default.StallAfter);
        var waiting = await Next(i => i.Busy);
        Assert.Equal(InstallStage.Waiting, waiting.Stage);
        Assert.False(retry.Token.IsCancellationRequested);
        retry.Finish(UpgradeResult.Updated);
        Assert.Equal(UpgradeResult.Updated, (await NextDone()).Done!.Outcome.Result);
    }

    [Fact]
    public async Task NoProgressAtAll_AlsoSaysAnotherInstallIsRunning()
    {
        var call = await Start(Firefox);
        _time.Advance(InstallTimings.Default.StallAfter);
        Assert.True((await Next(i => i.Busy)).Stage == InstallStage.Waiting);
        _time.Advance(InstallTimings.Default.Cap);
        var done = (await NextDone()).Done!;
        Assert.Equal((UpgradeResult.Failed, UpgradeFailure.TookTooLong), (done.Outcome.Result, done.Outcome.Failure));
        Assert.True(call.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task Packages_InstallOneAtATime()
    {
        var first = await Start(Firefox, Vlc);
        Assert.Equal(Firefox.Id, first.Package.Id);
        first.Download(10 * MB);
        await Next(i => i.Stage == InstallStage.Downloading);
        Assert.Equal(1, _upgrader.Count);
        first.Finish(UpgradeResult.Updated);
        var second = await _upgrader.NextCall(Ct);
        Assert.Equal(Vlc.Id, second.Package.Id);
        second.Finish(UpgradeResult.Updated);
        await Next(i => i.Stage == InstallStage.Done && i.Request.Package.Id == Vlc.Id);
        Assert.Equal(1, _upgrader.MaxRunning);
    }

    [Fact]
    public async Task SameAppTwice_IsQueuedOnce()
    {
        var call = await Start(Firefox);
        _queue.Enqueue([Request(Firefox)]);
        call.Finish(UpgradeResult.Updated);
        await NextDone();
        Assert.Equal(1, _upgrader.Count);
        Assert.Single(_history.Entries);
    }

    [Fact]
    public async Task CancelWhileWaiting_LeavesTheQueueWithoutHistory()
    {
        var first = await Start(Firefox, Vlc);
        _queue.Cancel(new PackageKey("videolan.vlc", "WINGET"));
        var cancelled = await NextDone();
        Assert.Equal((Vlc.Id, UpgradeResult.Cancelled), (cancelled.Request.Package.Id, cancelled.Done!.Outcome.Result));
        first.Finish(UpgradeResult.Updated);
        await NextDone();
        Assert.Equal(1, _upgrader.Count);
        Assert.Equal([Firefox.Id], _history.Entries.Select(e => e.Id));
    }

    [Fact]
    public async Task CancelWhileDownloading_IsRecordedAsCancelled()
    {
        _source.Reply = _ => Installed(Firefox, "130.0", "131.0");
        var call = await Start(Firefox);
        call.Download(10 * MB);
        _queue.Cancel(new PackageKey(Firefox.Id, Firefox.Source));
        var done = (await NextDone()).Done!;
        Assert.Equal(UpgradeResult.Cancelled, done.Outcome.Result);
        Assert.Equal(AppStatus.Available, done.After!.Status);
        Assert.Equal(HistoryResult.Cancelled, Assert.Single(_history.Entries).Result);
    }

    [Fact]
    public async Task CancelThatLandedAsTheInstallerStarted_CountsAsUpdated()
    {
        var call = await Start(Firefox);
        call.Download(10 * MB);
        _queue.Cancel(new PackageKey(Firefox.Id, Firefox.Source));
        var done = (await NextDone()).Done!;
        Assert.Equal(UpgradeResult.Updated, done.Outcome.Result);
        Assert.Equal(HistoryResult.Updated, Assert.Single(_history.Entries).Result);
    }

    [Fact]
    public async Task UnchangedVersionAfterSuccess_IsFlaggedAsPhantom()
    {
        _source.Reply = _ => Installed(Firefox, "130.0", "131.0");
        var call = await Start(Firefox);
        call.Finish(UpgradeResult.Updated);
        var done = (await NextDone()).Done!;
        Assert.True(done.Phantom);
        Assert.Equal(AppStatus.Phantom, done.After!.Status);
        Assert.True(_settings.Current.Apps.Single(a => a.Id == Firefox.Id).Offer!.Phantom);
        Assert.Equal((HistoryResult.Updated, "Phantom"), (_history.Entries[0].Result, _history.Entries[0].Reason));
    }

    [Fact]
    public async Task RestartNeeded_IsNotCheckedForPhantom()
    {
        _source.Reply = _ => Installed(Firefox, "130.0", "131.0");
        var call = await Start(Firefox);
        call.Finish(UpgradeResult.RestartNeeded, code: "installer 3010");
        var done = (await NextDone()).Done!;
        Assert.False(done.Phantom);
        Assert.False(_settings.Current.Apps.Single(a => a.Id == Firefox.Id).Offer!.Phantom);
        var entry = Assert.Single(_history.Entries);
        Assert.Equal((HistoryResult.Updated, "RestartNeeded", "installer 3010"), (entry.Result, entry.Reason, entry.Code));
    }

    [Fact]
    public async Task Failure_IsWrittenWithItsReasonAndCode()
    {
        _source.Reply = _ => Installed(Firefox, "130.0", "131.0");
        var call = await Start(Firefox);
        call.Finish(UpgradeResult.Failed, UpgradeFailure.DiskFull, "0x8A150105");
        var done = (await NextDone()).Done!;
        Assert.Equal(new UpgradeOutcome(UpgradeResult.Failed, UpgradeFailure.DiskFull, "0x8A150105"), done.Outcome);
        var entry = Assert.Single(_history.Entries);
        Assert.Equal((HistoryResult.Failed, "DiskFull", "0x8A150105"), (entry.Result, entry.Reason, entry.Code));
    }

    [Fact]
    public async Task AppInUse_IsWrittenAsFailed()
    {
        _source.Reply = _ => Installed(Firefox, "130.0", "131.0");
        var call = await Start(Firefox);
        call.Finish(UpgradeResult.AppInUse, code: "0x8A150101");
        Assert.Equal(UpgradeResult.AppInUse, (await NextDone()).Done!.Outcome.Result);
        Assert.Equal((HistoryResult.Failed, "AppInUse"), (_history.Entries[0].Result, _history.Entries[0].Reason));
    }

    [Fact]
    public async Task Busy_IsRetriedThreeTimesTwoMinutesApart()
    {
        _source.Reply = _ => Installed(Firefox, "130.0", "131.0");
        var call = await Start(Firefox);
        for (var retry = 1; retry <= 3; retry++)
        {
            call.Finish(UpgradeResult.Busy);
            Assert.True((await Next(i => i.Busy)).Stage == InstallStage.Waiting);
            _time.Advance(TimeSpan.FromMinutes(2) - TimeSpan.FromSeconds(1));
            Assert.Equal(retry, _upgrader.Count);
            _time.Advance(TimeSpan.FromSeconds(1));
            call = await _upgrader.NextCall(Ct);
        }
        call.Finish(UpgradeResult.Busy);
        var done = (await NextDone()).Done!;
        Assert.Equal(UpgradeResult.Busy, done.Outcome.Result);
        Assert.Equal(4, _upgrader.Count);
        Assert.Equal("Busy", Assert.Single(_history.Entries).Reason);
    }

    [Fact]
    public async Task Busy_ThenFree_Updates()
    {
        var call = await Start(Firefox);
        call.Finish(UpgradeResult.Busy);
        await Next(i => i.Busy);
        _time.Advance(TimeSpan.FromMinutes(2));
        var retry = await _upgrader.NextCall(Ct);
        retry.Download(5 * MB);
        Assert.False((await Next(i => i.Stage == InstallStage.Downloading)).Busy);
        retry.Finish(UpgradeResult.Updated);
        Assert.Equal(UpgradeResult.Updated, (await NextDone()).Done!.Outcome.Result);
    }

    [Fact]
    public async Task CancelWhileBusyWaiting_IsCancelled()
    {
        _source.Reply = _ => Installed(Firefox, "130.0", "131.0");
        var call = await Start(Firefox);
        call.Finish(UpgradeResult.Busy);
        await Next(i => i.Busy);
        _queue.Cancel(new PackageKey(Firefox.Id, Firefox.Source));
        Assert.Equal(UpgradeResult.Cancelled, (await NextDone()).Done!.Outcome.Result);
        Assert.Equal(1, _upgrader.Count);
    }

    [Fact]
    public async Task StalledDownload_IsRetriedOnce_ThenFails()
    {
        _source.Reply = _ => Installed(Firefox, "130.0", "131.0");
        var call = await Start(Firefox);
        call.Download(10 * MB);
        _time.Advance(TimeSpan.FromMinutes(2));
        var retry = await _upgrader.NextCall(Ct);
        Assert.True(call.Token.IsCancellationRequested);
        retry.Download(10 * MB);
        _time.Advance(TimeSpan.FromMinutes(2));
        var done = (await NextDone()).Done!;
        Assert.Equal(new UpgradeOutcome(UpgradeResult.Failed, UpgradeFailure.Stalled), done.Outcome);
        Assert.Equal(2, _upgrader.Count);
        Assert.Equal("Stalled", Assert.Single(_history.Entries).Reason);
    }

    [Fact]
    public async Task MovingDownload_DoesNotStall()
    {
        var call = await Start(Firefox);
        for (var minute = 1; minute <= 5; minute++)
        {
            call.Download((ulong)minute * MB);
            _time.Advance(TimeSpan.FromMinutes(1.5));
        }
        call.Finish(UpgradeResult.Updated);
        Assert.Equal(UpgradeResult.Updated, (await NextDone()).Done!.Outcome.Result);
        Assert.Equal(1, _upgrader.Count);
    }

    [Fact]
    public async Task InstallerPastTheCap_IsLeftRunning_AndTheQueueMovesOn()
    {
        _source.Reply = apps => Installed(Firefox, "130.0", "131.0");
        var call = await Start(Firefox, Vlc);
        call.Install();
        _time.Advance(TimeSpan.FromMinutes(30));
        var done = await NextDone();
        Assert.Equal((Firefox.Id, new UpgradeOutcome(UpgradeResult.Failed, UpgradeFailure.TookTooLong)), (done.Request.Package.Id, done.Done!.Outcome));
        var next = await _upgrader.NextCall(Ct);
        Assert.Equal(Vlc.Id, next.Package.Id);
        call.Finish(UpgradeResult.Updated);
        next.Finish(UpgradeResult.Updated);
        Assert.Equal(Vlc.Id, (await NextDone()).Request.Package.Id);
        Assert.Equal(["TookTooLong", "VideoLAN.VLC"], _history.Entries.Select(e => e.Reason ?? e.Id).Reverse());
    }

    // Under the speed limit a download can take hours. One that stops moving is still caught as stalled.
    [Fact]
    public async Task MovingDownloadPastTheCap_CarriesOn()
    {
        var call = await Start(Firefox);
        for (var minute = 1; minute <= 45; minute++)
        {
            call.Download((ulong)minute * MB);
            _time.Advance(TimeSpan.FromMinutes(1));
        }
        Assert.False(call.Token.IsCancellationRequested);
        call.Install();
        call.Finish(UpgradeResult.Updated);
        Assert.Equal(UpgradeResult.Updated, (await NextDone()).Done!.Outcome.Result);
    }

    [Fact]
    public async Task WaitingAndInstalling_AddUpToTheCap()
    {
        var call = await Start(Firefox);
        call.Queue();
        _time.Advance(TimeSpan.FromMinutes(10));
        for (var minute = 1; minute <= 40; minute++)
        {
            call.Download((ulong)minute * MB);
            _time.Advance(TimeSpan.FromMinutes(1));
        }
        call.Install();
        await Next(i => i.Stage == InstallStage.Installing);
        _time.Advance(TimeSpan.FromMinutes(20) - TimeSpan.FromSeconds(1));
        Assert.False(call.Token.IsCancellationRequested);
        _time.Advance(TimeSpan.FromSeconds(1));
        var done = (await NextDone()).Done!;
        Assert.Equal(new UpgradeOutcome(UpgradeResult.Failed, UpgradeFailure.TookTooLong), done.Outcome);
        Assert.True(call.Token.IsCancellationRequested);
    }

    // winget's proxy option went off behind the app's back: the rest of the batch runs at full speed (spec §6.4).
    [Fact]
    public async Task RefusedSpeedLimit_IsOffForTheNextUpdate_AndSaysSo()
    {
        var limit = new SpeedLimit(17500);
        using var queue = new InstallQueue(_upgrader, _source, _settings, _history, _time, _log, limit: limit);
        var refused = 0;
        queue.SpeedLimitRefused += (_, _) => refused++;
        queue.Changed += (_, item) => _items.Writer.TryWrite(item);
        queue.Enqueue([Request(Firefox), Request(Vlc)]);
        (await _upgrader.NextCall(Ct)).Finish(UpgradeResult.Failed, UpgradeFailure.ProxyRefused, "0x8A150002");
        var next = await _upgrader.NextCall(Ct);
        Assert.Equal((0, 1), (limit.KBps, refused));
        next.Finish(UpgradeResult.Updated);
        await Next(i => i.Stage == InstallStage.Done && i.Request.Package.Id == Vlc.Id);
        Assert.Equal(1, refused);
        Assert.Equal(("ProxyRefused", "0x8A150002"), (_history.Entries[^1].Reason, _history.Entries[^1].Code));
    }

    // What's installed is for the log: Details shows codes only (spec §7).
    [Fact]
    public async Task VersionWinGetNoLongerOffers_IsLoggedWithWhy()
    {
        var call = await Start(Firefox);
        call.Finish(UpgradeResult.NoUpdate, code: "installed 131.0");
        var done = (await NextDone()).Done!;
        Assert.Contains("INFO Mozilla.Firefox 131.0 isn't offered as an update: installed 131.0", File.ReadAllText(_folder.PathOf("app.log")));
        Assert.Null(done.Outcome.Code);
        Assert.Null(_history.Entries[^1].Code);
    }

    [Fact]
    public async Task ReadAgainFailure_KeepsTheOutcome()
    {
        _source.Reply = _ => throw new PackageSourceException(CheckProblem.WinGetUnreachable, "no answer");
        var call = await Start(Firefox);
        call.Finish(UpgradeResult.Updated);
        var done = (await NextDone()).Done!;
        Assert.Equal((UpgradeResult.Updated, false, null), (done.Outcome.Result, done.Phantom, done.After));
        Assert.Equal("131.0", _settings.Current.Apps.Single(a => a.Id == Firefox.Id).Offer!.Version);
        Assert.Single(_history.Entries);
    }

    [Fact]
    public async Task AppUntrackedDuringTheInstall_StaysUntracked()
    {
        var call = await Start(Firefox);
        _settings.Update(f => f with { Apps = [Vlc] });
        call.Finish(UpgradeResult.Updated);
        Assert.Null((await NextDone()).Done!.After);
        Assert.Equal([Vlc.Id], _settings.Current.Apps.Select(a => a.Id));
    }

    [Fact]
    public async Task AppUntrackedDuringTheInstall_LeavesSettingsJsonAlone()
    {
        var call = await Start(Firefox);
        _settings.Update(f => f with { Apps = [Vlc] });
        // Written behind the store's back, so a save would show.
        File.WriteAllText(_folder.PathOf("settings.json"), "{ \"marker\": true }");
        call.Finish(UpgradeResult.Updated);
        await NextDone();
        Assert.Contains("marker", File.ReadAllText(_folder.PathOf("settings.json")));
    }

    [Fact]
    public async Task HistoryThatCantBeSaved_DoesNotStopTheQueue()
    {
        Directory.CreateDirectory(_folder.PathOf("locked.json"));
        var locked = new HistoryStore(_folder.PathOf("locked.json"), _time);
        locked.Load();
        using var queue = Queue(locked);
        queue.Enqueue([Request(Firefox), Request(Vlc)]);
        (await _upgrader.NextCall(Ct)).Finish(UpgradeResult.Updated);
        (await _upgrader.NextCall(Ct)).Finish(UpgradeResult.Updated);
        Assert.Equal(Vlc.Id, (await Next(i => i.Stage == InstallStage.Done && i.Request.Package.Id == Vlc.Id)).Request.Package.Id);
        Assert.Empty(locked.Entries);
    }

    [Fact]
    public async Task HandlerThatThrows_DoesNotStopTheQueueOrTheOtherHandlers()
    {
        using var queue = new InstallQueue(_upgrader, _source, _settings, _history, _time, _log);
        queue.Changed += (_, _) => throw new InvalidOperationException("bug");
        queue.Changed += (_, item) => _items.Writer.TryWrite(item);
        queue.Enqueue([Request(Firefox), Request(Vlc)]);
        (await _upgrader.NextCall(Ct)).Finish(UpgradeResult.Updated);
        (await _upgrader.NextCall(Ct)).Finish(UpgradeResult.Updated);
        Assert.Equal(Vlc.Id, (await Next(i => i.Stage == InstallStage.Done && i.Request.Package.Id == Vlc.Id)).Request.Package.Id);
        Assert.Contains("ERROR Install update of Mozilla.Firefox not delivered", File.ReadAllText(_folder.PathOf("app.log")));
    }

    [Fact]
    public async Task Dispose_StopsTheQueueQuietly()
    {
        var call = await Start(Firefox, Vlc);
        call.Download(10 * MB);
        await Next(i => i.Stage == InstallStage.Downloading);
        _queue.Dispose();
        await call.Cancelled.WaitAsync(Wait, Ct);
        await _queue.Stopped.WaitAsync(Wait, Ct);
        _queue.Enqueue([Request(Firefox)]);
        Assert.Equal(1, _upgrader.Count);
        Assert.Empty(_history.Entries);
    }

    [Fact]
    public async Task DisposeOnTheUiThread_StopsWhileThatThreadWaits()
    {
        var queue = Queue(_history);
        queue.Enqueue([Request(Firefox)]);
        (await _upgrader.NextCall(Ct)).Download(10 * MB);
        await Next(i => i.Stage == InstallStage.Downloading);
        // Quit disposes on the UI thread, then blocks it while it waits for Stopped.
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new BlockedUi());
        try
        {
            queue.Dispose();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
        await queue.Stopped.WaitAsync(Wait, Ct);
    }

    // A UI thread that's blocked: work posted to it never runs.
    private sealed class BlockedUi : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
        }
    }
}
