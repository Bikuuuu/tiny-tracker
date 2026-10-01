using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;
using TinyTracker.Core.Installing;
using TinyTracker.Core.Logging;
using TinyTracker.Core.Storage;
using TinyTracker.Core.Tracking;
using Xunit;

namespace TinyTracker.Core.Tests.Installing;

// Close & update: the app closes at its turn, the user is asked before a force close, and it reopens whatever happens (spec §6.3).
public sealed class InstallQueueClosingTests : IAsyncDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly TrackedApp Firefox = App("Mozilla.Firefox", "Firefox", "131.0");
    private static readonly TrackedApp Vlc = App("VideoLAN.VLC", "VLC", "3.0.21");

    private readonly TempFolder _folder = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 26, 8, 0, 0, TimeSpan.Zero));
    private readonly FakeUpgrader _upgrader = new();
    private readonly FakeSource _source = new();
    private readonly FakeCloser _closer = new();
    private readonly Channel<InstallItem> _items = Channel.CreateUnbounded<InstallItem>();
    private readonly SettingsStore _settings;
    private readonly HistoryStore _history;
    private readonly FileLog _log;
    private readonly InstallQueue _queue;

    public InstallQueueClosingTests()
    {
        _log = new FileLog(_folder.PathOf("app.log"), _time);
        _settings = new SettingsStore(_folder.PathOf("settings.json"));
        _settings.Update(f => f with { Apps = [Firefox, Vlc] });
        _history = new HistoryStore(_folder.PathOf("history.json"), _time);
        // Each app reads back at its old version, so a cancel stays a cancel.
        _source.Reply = apps => new([.. apps.Select(a => new PackageSnapshot(a.Id, a.Source, a.Name, "1.0", Of(a.Id).Offer!.Version))], []);
        _queue = new InstallQueue(_upgrader, _source, _settings, _history, _time, _log, closer: _closer);
        _queue.Changed += (_, item) => _items.Writer.TryWrite(item);
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Dispose();
        await _queue.Stopped.WaitAsync(Wait);
        _log.Close();
        _folder.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TrackedApp App(string id, string name, string offer) =>
        new() { Id = id, Source = "winget", Name = name, Offer = new Offer { Version = offer, FirstSeen = DateTimeOffset.UnixEpoch } };

    private static TrackedApp Of(string id) => new[] { Firefox, Vlc }.Single(a => a.Id == id);

    private static InstallRequest Request(TrackedApp app, bool closeFirst = false) =>
        new(new PackageKey(app.Id, app.Source), app.Name, "1.0", app.Offer!.Version) { LocalId = $@"ARP\User\X64\{app.Name}", CloseFirst = closeFirst };

    private static PackageKey Key(TrackedApp app) => new(app.Id, app.Source);

    private async Task<InstallItem> Next(Func<InstallItem, bool> match)
    {
        while (true)
        {
            var item = await _items.Reader.ReadAsync(Ct).AsTask().WaitAsync(Wait, Ct);
            if (match(item)) return item;
        }
    }

    private Task<InstallItem> Stage(InstallStage stage) => Next(i => i.Stage == stage);

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting.");
            await Task.Delay(5, Ct);
        }
    }

    private async Task<InstallDone> DoneOf(TrackedApp app) => (await Next(i => i.Stage == InstallStage.Done && i.Request.Package.Id == app.Id)).Done!;

    // Starts Close & update and lets the app stay open for the 10 seconds.
    private async Task<FakeClosing> NotClosed(TrackedApp app)
    {
        _queue.Enqueue([Request(app, closeFirst: true)]);
        var closing = await _closer.NextClose(Ct);
        await Stage(InstallStage.Closing);
        _time.Advance(InstallTimings.Default.CloseWait);
        await Stage(InstallStage.NotClosed);
        return closing;
    }

    [Fact]
    public async Task CloseAndUpdate_ClosesTheApp_Updates_ThenReopensIt()
    {
        _queue.Enqueue([Request(Firefox, closeFirst: true)]);
        var closing = await _closer.NextClose(Ct);
        await Stage(InstallStage.Closing);
        Assert.Equal(0, _upgrader.Count);
        closing.Close();
        var call = await _upgrader.NextCall(Ct);
        Assert.False(closing.Reopened.IsCompleted);
        call.Finish(UpgradeResult.Updated);
        Assert.Equal(UpgradeResult.Updated, (await DoneOf(Firefox)).Outcome.Result);
        Assert.True(closing.Reopened.IsCompleted);
        Assert.Equal([@"ARP\User\X64\Firefox"], _closer.Asked);
    }

    [Fact]
    public async Task AppStillOpenAfter10Seconds_WaitsForTheUser_AndForceCloseGoesOn()
    {
        var closing = await NotClosed(Firefox);
        Assert.Equal(0, _upgrader.Count);
        _queue.ForceClose(Key(Firefox));
        var call = await _upgrader.NextCall(Ct);
        Assert.Equal(1, closing.ForceCloses);
        call.Finish(UpgradeResult.Updated);
        Assert.Equal(UpgradeResult.Updated, (await DoneOf(Firefox)).Outcome.Result);
        await closing.Reopened.WaitAsync(Wait, Ct);
    }

    [Fact]
    public async Task AppThatClosed_WaitsForItsUpdate_WithNothingToAnswer()
    {
        await NotClosed(Firefox);
        _queue.ForceClose(Key(Firefox));
        Assert.Equal(InstallStage.Waiting, (await Next(i => i.Stage != InstallStage.NotClosed)).Stage);
        (await _upgrader.NextCall(Ct)).Finish(UpgradeResult.Updated);
        Assert.Equal(UpgradeResult.Updated, (await DoneOf(Firefox)).Outcome.Result);
    }

    [Fact]
    public async Task ForceCloseThatDoesntEndTheApp_SaysToCloseItFirst()
    {
        var closing = await NotClosed(Firefox);
        closing.EndsWhenForced = false;
        _queue.ForceClose(Key(Firefox));
        await Until(() => closing.ForceCloses == 1);
        _time.Advance(InstallTimings.Default.CloseWait);
        Assert.Equal(UpgradeResult.CouldNotClose, (await DoneOf(Firefox)).Outcome.Result);
        await closing.Reopened.WaitAsync(Wait, Ct);
        Assert.Equal(0, _upgrader.Count);
    }

    [Fact]
    public async Task InstallerStillRunningAtTheCap_ReopensTheAppOnceItEnds()
    {
        _queue.Enqueue([Request(Firefox, closeFirst: true)]);
        var closing = await _closer.NextClose(Ct);
        closing.Close();
        var call = await _upgrader.NextCall(Ct);
        call.Install();
        await Stage(InstallStage.Installing);
        _time.Advance(InstallTimings.Default.Cap);
        Assert.Equal(UpgradeFailure.TookTooLong, (await DoneOf(Firefox)).Outcome.Failure);
        Assert.False(closing.Reopened.IsCompleted);
        call.Finish(UpgradeResult.Updated);
        await closing.Reopened.WaitAsync(Wait, Ct);
    }

    [Fact]
    public async Task HelperInstallerStillRunningAtTheCap_KeepsTheHelper_AndReopensTheAppOnceItEnds()
    {
        var elevation = new FakeElevation();
        using var queue = new InstallQueue(_upgrader, _source, _settings, _history, _time, _log, elevation: elevation, closer: _closer);
        queue.Changed += (_, item) => _items.Writer.TryWrite(item);
        queue.Enqueue([Request(Firefox, closeFirst: true) with { Route = InstallRoute.Helper }, Request(Vlc)]);
        var session = (await elevation.NextCall(Ct)).Approve();
        var closing = await _closer.NextClose(Ct);
        closing.Close();
        var call = await session.Upgrades.NextCall(Ct);
        call.Install();
        await Stage(InstallStage.Installing);
        _time.Advance(InstallTimings.Default.Cap);
        Assert.Equal(UpgradeFailure.TookTooLong, (await DoneOf(Firefox)).Outcome.Failure);
        // VLC's turn: the queue has moved on.
        var next = await _upgrader.NextCall(Ct);
        Assert.False(session.Disposed);
        Assert.False(closing.Reopened.IsCompleted);
        call.Finish(UpgradeResult.Updated);
        await closing.Reopened.WaitAsync(Wait, Ct);
        await session.Gone.WaitAsync(Wait, Ct);
        next.Finish(UpgradeResult.Updated);
    }

    [Fact]
    public async Task QuitWhileTheHelperInstalls_LeavesTheAppClosed()
    {
        var elevation = new FakeElevation();
        using var queue = new InstallQueue(_upgrader, _source, _settings, _history, _time, _log, elevation: elevation, closer: _closer);
        queue.Changed += (_, item) => _items.Writer.TryWrite(item);
        queue.Enqueue([Request(Firefox, closeFirst: true) with { Route = InstallRoute.Helper }]);
        var session = (await elevation.NextCall(Ct)).Approve();
        var closing = await _closer.NextClose(Ct);
        closing.Close();
        var call = await session.Upgrades.NextCall(Ct);
        call.Install();
        await Stage(InstallStage.Installing);
        queue.Dispose();
        await queue.Stopped.WaitAsync(Wait, Ct);
        Assert.True(session.Disposed);
        // Hanging up ended the helper's upgrade, not its installer.
        await Task.Delay(200, Ct);
        Assert.False(closing.Reopened.IsCompleted);
    }

    [Fact]
    public async Task DeclinedPrompt_LeavesTheAppOpen()
    {
        var elevation = new FakeElevation();
        using var queue = new InstallQueue(_upgrader, _source, _settings, _history, _time, _log, elevation: elevation, closer: _closer);
        var done = new TaskCompletionSource<InstallItem>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Changed += (_, item) =>
        {
            if (item.Stage == InstallStage.Done) done.TrySetResult(item);
        };
        queue.Enqueue([Request(Firefox, closeFirst: true) with { Route = InstallRoute.Helper }]);
        (await elevation.NextCall(Ct)).Answer(HelperStartResult.Declined);
        Assert.Equal(UpgradeResult.PermissionDeclined, (await done.Task.WaitAsync(Wait, Ct)).Done!.Outcome.Result);
        Assert.Empty(_closer.Asked);
    }

    [Fact]
    public async Task AppThatClosesWhileTheRowAsks_UpdatesWithoutAForceClose()
    {
        var closing = await NotClosed(Firefox);
        closing.Close();
        (await _upgrader.NextCall(Ct)).Finish(UpgradeResult.Updated);
        Assert.Equal(UpgradeResult.Updated, (await DoneOf(Firefox)).Outcome.Result);
        Assert.Equal(0, closing.ForceCloses);
    }

    [Fact]
    public async Task NoAnswerFor2Minutes_CountsAsCancel_AndReopensWhatClosed()
    {
        var closing = await NotClosed(Firefox);
        _time.Advance(InstallTimings.Default.CloseAnswerWait);
        Assert.Equal(UpgradeResult.Cancelled, (await DoneOf(Firefox)).Outcome.Result);
        await closing.Reopened.WaitAsync(Wait, Ct);
        Assert.Equal(0, _upgrader.Count);
        Assert.Empty(_history.Entries);
    }

    [Fact]
    public async Task CancelWhileTheRowAsks_ReopensWhatClosed()
    {
        var closing = await NotClosed(Firefox);
        _queue.Cancel(Key(Firefox));
        Assert.Equal(UpgradeResult.Cancelled, (await DoneOf(Firefox)).Outcome.Result);
        await closing.Reopened.WaitAsync(Wait, Ct);
        Assert.Equal(0, _upgrader.Count);
    }

    [Fact]
    public async Task ForceCloseThatCantEndTheApp_SaysToCloseItFirst()
    {
        var closing = await NotClosed(Firefox);
        closing.CanEnd = false;
        _queue.ForceClose(Key(Firefox));
        Assert.Equal(UpgradeResult.CouldNotClose, (await DoneOf(Firefox)).Outcome.Result);
        await closing.Reopened.WaitAsync(Wait, Ct);
        Assert.Equal(0, _upgrader.Count);
        Assert.Equal("CouldNotClose", Assert.Single(_history.Entries).Reason);
    }

    [Fact]
    public async Task AppWhoseFolderIsUnknown_SaysToCloseItFirst()
    {
        _closer.Findable = false;
        _queue.Enqueue([Request(Firefox, closeFirst: true)]);
        Assert.Equal(UpgradeResult.CouldNotClose, (await DoneOf(Firefox)).Outcome.Result);
        Assert.Empty(_closer.Asked);
        Assert.Equal(0, _upgrader.Count);
    }

    [Fact]
    public async Task AppStillInUseAfterClosing_SaysToCloseItFirst()
    {
        _queue.Enqueue([Request(Firefox, closeFirst: true)]);
        var closing = await _closer.NextClose(Ct);
        closing.Close();
        (await _upgrader.NextCall(Ct)).Finish(UpgradeResult.AppInUse, code: "0x8A150101");
        Assert.Equal(new UpgradeOutcome(UpgradeResult.CouldNotClose, Code: "0x8A150101"), (await DoneOf(Firefox)).Outcome);
        await closing.Reopened.WaitAsync(Wait, Ct);
    }

    [Theory]
    [InlineData(true, UpgradeResult.AppInUse)]
    [InlineData(false, UpgradeResult.CouldNotClose)]
    public async Task UpdateThatFindsTheAppInUse_OffersCloseAndUpdate_OnlyWhenItsFolderIsKnown(bool findable, UpgradeResult result)
    {
        _closer.Findable = findable;
        _queue.Enqueue([Request(Firefox)]);
        (await _upgrader.NextCall(Ct)).Finish(UpgradeResult.AppInUse, code: "0x8A150101");
        Assert.Equal(result, (await DoneOf(Firefox)).Outcome.Result);
        Assert.Empty(_closer.Asked);
    }

    [Fact]
    public async Task FailedUpdate_StillReopensTheApp()
    {
        _queue.Enqueue([Request(Firefox, closeFirst: true)]);
        var closing = await _closer.NextClose(Ct);
        closing.Close();
        (await _upgrader.NextCall(Ct)).Finish(UpgradeResult.Failed, UpgradeFailure.DiskFull);
        Assert.Equal(UpgradeFailure.DiskFull, (await DoneOf(Firefox)).Outcome.Failure);
        Assert.True(closing.Reopened.IsCompleted);
    }

    [Fact]
    public async Task AppStaysOpen_WhileAnotherInstallsFirst()
    {
        _queue.Enqueue([Request(Vlc), Request(Firefox, closeFirst: true)]);
        var vlc = await _upgrader.NextCall(Ct);
        Assert.Empty(_closer.Asked);
        vlc.Finish(UpgradeResult.Updated);
        (await _closer.NextClose(Ct)).Close();
        Assert.Equal(Firefox.Id, (await _upgrader.NextCall(Ct)).Package.Id);
    }

    [Fact]
    public async Task QuitWhileTheRowAsks_ReopensWhatClosed()
    {
        var closing = await NotClosed(Firefox);
        _queue.Dispose();
        await _queue.Stopped.WaitAsync(Wait, Ct);
        Assert.True(closing.Reopened.IsCompleted);
        Assert.Equal(0, _upgrader.Count);
    }

    [Fact]
    public async Task WithoutACloser_CloseAndUpdateSaysToCloseItFirst()
    {
        using var queue = new InstallQueue(_upgrader, _source, _settings, _history, _time, _log);
        var done = new TaskCompletionSource<InstallItem>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Changed += (_, item) =>
        {
            if (item.Stage == InstallStage.Done) done.TrySetResult(item);
        };
        queue.Enqueue([Request(Firefox, closeFirst: true)]);
        Assert.Equal(UpgradeResult.CouldNotClose, (await done.Task.WaitAsync(Wait, Ct)).Done!.Outcome.Result);
    }
}
