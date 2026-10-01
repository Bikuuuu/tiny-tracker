using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;
using TinyTracker.Core.Elevation;
using TinyTracker.Core.Installing;
using TinyTracker.Core.Logging;
using TinyTracker.Core.Storage;
using TinyTracker.Core.Tracking;
using Xunit;

namespace TinyTracker.Core.Tests.Installing;

// Admin updates go through the helper: its prompt shows at the click, one serves the batch, and nothing stays stuck (spec §6.3, §7).
public sealed class InstallQueueHelperTests : IAsyncDisposable
{
    private const ulong MB = 1024 * 1024;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly TrackedApp Firefox = App("Mozilla.Firefox", "Firefox", "131.0");
    private static readonly TrackedApp Vlc = App("VideoLAN.VLC", "VLC", "3.0.21");
    private static readonly TrackedApp Paint = App("Example.Paint", "Example Paint", "2.0");

    private readonly TempFolder _folder = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 26, 8, 0, 0, TimeSpan.Zero));
    private readonly FakeUpgrader _upgrader = new();
    private readonly FakeSource _source = new();
    private readonly FakeElevation _elevation = new();
    private readonly Channel<InstallItem> _items = Channel.CreateUnbounded<InstallItem>();
    private readonly SettingsStore _settings;
    private readonly HistoryStore _history;
    private readonly FileLog _log;
    private readonly InstallQueue _queue;

    public InstallQueueHelperTests()
    {
        _log = new FileLog(_folder.PathOf("app.log"), _time);
        _settings = new SettingsStore(_folder.PathOf("settings.json"));
        _settings.Update(f => f with { Apps = [Firefox, Vlc, Paint] });
        _history = new HistoryStore(_folder.PathOf("history.json"), _time);
        // Each app reads back at the version it was offered.
        _source.Reply = apps => new([.. apps.Select(a => new PackageSnapshot(a.Id, a.Source, a.Name, Of(a.Id).Offer!.Version, null))], []);
        _queue = new InstallQueue(_upgrader, _source, _settings, _history, _time, _log, elevation: _elevation);
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

    private static TrackedApp Of(string id) => new[] { Firefox, Vlc, Paint }.Single(a => a.Id == id);

    private static InstallRequest Request(TrackedApp app, InstallRoute route = InstallRoute.App, bool byItself = false) =>
        new(new PackageKey(app.Id, app.Source), app.Name, "1.0", app.Offer!.Version) { Route = route, ByItself = byItself };

    private static PackageKey Key(TrackedApp app) => new(app.Id, app.Source);

    private async Task<InstallItem> Next(Func<InstallItem, bool> match)
    {
        while (true)
        {
            var item = await _items.Reader.ReadAsync(Ct).AsTask().WaitAsync(Wait, Ct);
            if (match(item)) return item;
        }
    }

    private Task<InstallItem> DoneOf(TrackedApp app) => Next(i => i.Stage == InstallStage.Done && i.Request.Package.Id == app.Id);

    // How these apps ended, in whatever order: updates still waiting when the helper's start settles end in queue order.
    private async Task<Dictionary<string, InstallDone>> Ended(params TrackedApp[] apps)
    {
        var ended = new Dictionary<string, InstallDone>();
        while (ended.Count < apps.Length)
        {
            var item = await Next(i => i.Stage == InstallStage.Done && apps.Any(a => a.Id == i.Request.Package.Id));
            ended[item.Request.Package.Id] = item.Done!;
        }
        return ended;
    }

    private string? ReasonOf(TrackedApp app) => _history.Entries.Single(e => e.Id == app.Id).Reason;

    [Fact]
    public async Task AdminUpdate_AsksAtTheClick_EvenBehindAnotherUpdate()
    {
        _queue.Enqueue([Request(Firefox), Request(Vlc, InstallRoute.Helper)]);
        Assert.True((await _elevation.NextCall(Ct)).MayPrompt);
        Assert.Equal(Firefox.Id, (await _upgrader.NextCall(Ct)).Package.Id);
        Assert.True((await Next(i => i.Request.Package.Id == Vlc.Id)).AwaitingPermission);
    }

    [Fact]
    public async Task ApprovedPrompt_RunsTheUpdateThroughTheHelper_ThenLetsItGo()
    {
        _queue.Enqueue([Request(Vlc, InstallRoute.Helper)]);
        Assert.True((await Next(i => i.Request.Package.Id == Vlc.Id)).AwaitingPermission);
        var session = (await _elevation.NextCall(Ct)).Approve();
        Assert.False((await Next(i => !i.AwaitingPermission)).AwaitingPermission);
        var call = await session.Upgrades.NextCall(Ct);
        Assert.Equal((Vlc.Id, "3.0.21"), (call.Package.Id, call.Version));
        call.Download(10 * MB);
        call.Finish(UpgradeResult.Updated);
        Assert.Equal(UpgradeResult.Updated, (await DoneOf(Vlc)).Done!.Outcome.Result);
        await session.Gone.WaitAsync(Wait, Ct);
        Assert.Equal(0, _upgrader.Count);
    }

    [Fact]
    public async Task OnePrompt_ServesTheWholeBatch()
    {
        _queue.Enqueue([Request(Firefox, InstallRoute.Helper), Request(Vlc, InstallRoute.Helper)]);
        var session = (await _elevation.NextCall(Ct)).Approve();
        (await session.Upgrades.NextCall(Ct)).Finish(UpgradeResult.Updated);
        (await session.Upgrades.NextCall(Ct)).Finish(UpgradeResult.Updated);
        await DoneOf(Vlc);
        await session.Gone.WaitAsync(Wait, Ct);
        Assert.Equal(1, _elevation.Count);
    }

    [Fact]
    public async Task UpdateQueuedWhileTheHelperRuns_UsesIt()
    {
        _queue.Enqueue([Request(Firefox, InstallRoute.Helper)]);
        var session = (await _elevation.NextCall(Ct)).Approve();
        var first = await session.Upgrades.NextCall(Ct);
        _queue.Enqueue([Request(Vlc, InstallRoute.Helper)]);
        first.Finish(UpgradeResult.Updated);
        var second = await session.Upgrades.NextCall(Ct);
        Assert.False(session.Disposed);
        second.Finish(UpgradeResult.Updated);
        await session.Gone.WaitAsync(Wait, Ct);
        Assert.Equal(1, _elevation.Count);
    }

    [Fact]
    public async Task HelperWaits_WhileAnotherAppInstallsFirst()
    {
        _queue.Enqueue([Request(Firefox), Request(Vlc, InstallRoute.Helper)]);
        var session = (await _elevation.NextCall(Ct)).Approve();
        var firefox = await _upgrader.NextCall(Ct);
        Assert.Equal(0, session.Upgrades.Count);
        firefox.Finish(UpgradeResult.Updated);
        (await session.Upgrades.NextCall(Ct)).Finish(UpgradeResult.Updated);
        Assert.Equal(UpgradeResult.Updated, (await DoneOf(Vlc)).Done!.Outcome.Result);
    }

    // Meanwhile the helper hears nothing, so the queue tells it to stay, well within its idle wait, until it lets it go (spec §5.1).
    [Fact]
    public async Task HelperThatWaits_IsToldToStay_UntilItGoes()
    {
        _queue.Enqueue([Request(Firefox), Request(Vlc, InstallRoute.Helper)]);
        var session = (await _elevation.NextCall(Ct)).Approve();
        var firefox = await _upgrader.NextCall(Ct);
        var deadline = DateTime.UtcNow + Wait;
        while (session.Stays == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "It was never told to stay.");
            _time.Advance(TimeSpan.FromMinutes(1));
            await Task.Delay(10, Ct);
        }
        var told = session.Stays;
        _time.Advance(HelperRules.StayEvery);
        Assert.Equal(told + 1, session.Stays);
        firefox.Finish(UpgradeResult.Updated);
        (await session.Upgrades.NextCall(Ct)).Finish(UpgradeResult.Updated);
        await session.Gone.WaitAsync(Wait, Ct);
        told = session.Stays;
        _time.Advance(HelperRules.StayEvery * 3);
        Assert.Equal(told, session.Stays);
    }

    // One queued once the prompt closed, while the helper still starts, isn't waiting for permission.
    [Fact]
    public async Task AdminUpdateQueuedAfterThePromptClosed_IsntWaitingForPermission()
    {
        _queue.Enqueue([Request(Firefox, InstallRoute.Helper)]);
        (await _elevation.NextCall(Ct)).ClosePrompt();
        _queue.Enqueue([Request(Vlc, InstallRoute.Helper)]);
        Assert.False((await Next(i => i.Request.Package.Id == Vlc.Id)).AwaitingPermission);
    }

    [Fact]
    public async Task DeclinedPrompt_SendsTheWaitingAdminUpdatesBack_AndTheOthersCarryOn()
    {
        _queue.Enqueue([Request(Firefox), Request(Vlc, InstallRoute.Helper)]);
        var prompt = await _elevation.NextCall(Ct);
        var firefox = await _upgrader.NextCall(Ct);
        prompt.Answer(HelperStartResult.Declined);
        Assert.Equal(UpgradeResult.PermissionDeclined, (await DoneOf(Vlc)).Done!.Outcome.Result);
        Assert.False(firefox.Token.IsCancellationRequested);
        firefox.Finish(UpgradeResult.Updated);
        Assert.Equal(UpgradeResult.Updated, (await DoneOf(Firefox)).Done!.Outcome.Result);
        Assert.Equal("PermissionDeclined", ReasonOf(Vlc));
        Assert.Equal(1, _elevation.Count);
    }

    [Fact]
    public async Task DeclinedPrompt_AtTheFront_AsksAgainOnlyWhenTheUserActs()
    {
        _queue.Enqueue([Request(Vlc, InstallRoute.Helper)]);
        (await _elevation.NextCall(Ct)).Answer(HelperStartResult.Declined);
        Assert.Equal(UpgradeResult.PermissionDeclined, (await DoneOf(Vlc)).Done!.Outcome.Result);
        Assert.Equal(1, _elevation.Count);
        _queue.Enqueue([Request(Vlc, InstallRoute.Helper)]);
        Assert.True((await _elevation.NextCall(Ct)).MayPrompt);
    }

    // The answer can land before or after the worker takes the update; either way there's one prompt.
    [Fact]
    public async Task PromptAnsweredAtOnce_IsNeverShownTwice()
    {
        for (var round = 0; round < 50; round++)
        {
            var elevation = new FakeElevation { AnswerAtOnce = HelperStartResult.Declined };
            using var queue = new InstallQueue(_upgrader, _source, _settings, _history, _time, _log, elevation: elevation);
            var ended = new TaskCompletionSource<InstallItem>(TaskCreationOptions.RunContinuationsAsynchronously);
            queue.Changed += (_, item) =>
            {
                if (item.Stage == InstallStage.Done) ended.TrySetResult(item);
            };
            queue.Enqueue([Request(Vlc, InstallRoute.Helper)]);
            Assert.Equal(UpgradeResult.PermissionDeclined, (await ended.Task.WaitAsync(Wait, Ct)).Done!.Outcome.Result);
            Assert.Equal(1, elevation.Count);
        }
    }

    [Fact]
    public async Task HelperThatDidntStart_FailsItsUpdates()
    {
        _queue.Enqueue([Request(Firefox, InstallRoute.Helper), Request(Vlc, InstallRoute.Helper)]);
        (await _elevation.NextCall(Ct)).Answer(HelperStartResult.Failed, "0x80070002");
        var ended = await Ended(Firefox, Vlc);
        var failed = new UpgradeOutcome(UpgradeResult.Failed, UpgradeFailure.HelperNotStarted, "0x80070002");
        Assert.Equal((failed, failed), (ended[Firefox.Id].Outcome, ended[Vlc.Id].Outcome));
        Assert.Equal(("HelperNotStarted", "HelperNotStarted"), (ReasonOf(Firefox), ReasonOf(Vlc)));
    }

    [Fact]
    public async Task HelperThatStops_FailsTheRunningUpdateAndTheOnesWaitingForIt_AndTheOthersCarryOn()
    {
        _queue.Enqueue([Request(Firefox, InstallRoute.Helper), Request(Vlc, InstallRoute.Helper), Request(Paint)]);
        var session = (await _elevation.NextCall(Ct)).Approve();
        var firefox = await session.Upgrades.NextCall(Ct);
        firefox.Finish(UpgradeResult.Failed, UpgradeFailure.HelperStopped);
        Assert.Equal(UpgradeFailure.HelperStopped, (await DoneOf(Vlc)).Done!.Outcome.Failure);
        var done = (await DoneOf(Firefox)).Done!;
        Assert.Equal(UpgradeFailure.HelperStopped, done.Outcome.Failure);
        // Firefox was read again, as after any install.
        Assert.NotNull(done.After);
        await session.Gone.WaitAsync(Wait, Ct);
        (await _upgrader.NextCall(Ct)).Finish(UpgradeResult.Updated);
        Assert.Equal(UpgradeResult.Updated, (await DoneOf(Paint)).Done!.Outcome.Result);
        Assert.Equal(1, session.Upgrades.Count);
        _queue.Enqueue([Request(Vlc, InstallRoute.Helper)]);
        Assert.True((await _elevation.NextCall(Ct)).MayPrompt);
    }

    [Fact]
    public async Task UpdateThatStartedByItself_NeverShowsAPrompt()
    {
        _queue.Enqueue([Request(Vlc, InstallRoute.Helper, byItself: true)]);
        Assert.False((await Next(i => i.Request.Package.Id == Vlc.Id)).AwaitingPermission);
        var start = await _elevation.NextCall(Ct);
        Assert.False(start.MayPrompt);
        start.Answer(HelperStartResult.NeedsPrompt);
        Assert.Equal(UpgradeResult.NeedsAdmin, (await DoneOf(Vlc)).Done!.Outcome.Result);
        Assert.Equal(1, _elevation.Count);
        Assert.Equal("NeedsAdmin", ReasonOf(Vlc));
    }

    [Fact]
    public async Task UsersUpdate_WhileAStartWithoutAPromptFails_GetsItsPrompt()
    {
        _queue.Enqueue([Request(Vlc, InstallRoute.Helper, byItself: true)]);
        var silent = await _elevation.NextCall(Ct);
        _queue.Enqueue([Request(Firefox, InstallRoute.Helper)]);
        silent.Answer(HelperStartResult.NeedsPrompt);
        Assert.Equal(UpgradeResult.NeedsAdmin, (await DoneOf(Vlc)).Done!.Outcome.Result);
        var prompt = await _elevation.NextCall(Ct);
        Assert.True(prompt.MayPrompt);
        var session = prompt.Approve();
        Assert.Equal(Firefox.Id, (await session.Upgrades.NextCall(Ct)).Package.Id);
    }

    [Fact]
    public async Task UsersUpdateAtTheFront_AfterAStartWithoutAPrompt_GetsItsOwnPrompt()
    {
        _queue.Enqueue([Request(Vlc, InstallRoute.Helper, byItself: true)]);
        var silent = await _elevation.NextCall(Ct);
        _queue.Enqueue([Request(Firefox, InstallRoute.Helper)]);
        // The update that started by itself goes, and the user's takes its place at the front.
        _queue.Cancel(Key(Vlc));
        await DoneOf(Vlc);
        await Task.Delay(100, Ct);
        silent.Answer(HelperStartResult.NeedsPrompt);
        var prompt = await _elevation.NextCall(Ct);
        Assert.True(prompt.MayPrompt);
        await Next(i => i.Request.Package.Id == Firefox.Id && i.AwaitingPermission);
        var session = prompt.Approve();
        (await session.Upgrades.NextCall(Ct)).Finish(UpgradeResult.Updated);
        Assert.Equal(UpgradeResult.Updated, (await DoneOf(Firefox)).Done!.Outcome.Result);
    }

    // "Waiting for permission…" only while the prompt shows: answered, the row waits for the helper as any other does (spec §4.3).
    [Fact]
    public async Task AnsweredPrompt_LeavesTheRowWaiting_WhileTheHelperStarts()
    {
        _queue.Enqueue([Request(Vlc, InstallRoute.Helper)]);
        Assert.True((await Next(i => i.Request.Package.Id == Vlc.Id)).AwaitingPermission);
        var call = await _elevation.NextCall(Ct);
        call.ClosePrompt();
        Assert.False((await Next(i => !i.AwaitingPermission)).AwaitingPermission);
        var session = call.Approve();
        (await session.Upgrades.NextCall(Ct)).Finish(UpgradeResult.Updated);
        Assert.Equal(UpgradeResult.Updated, (await DoneOf(Vlc)).Done!.Outcome.Result);
    }

    // In silent mode a missing task makes the launcher prompt after all, and the row says so while the prompt shows (spec §6.6).
    [Fact]
    public async Task PromptInSilentMode_SaysWaitingForPermission_WhileItShows()
    {
        var silent = new FakeElevation { Prompts = false };
        using var queue = new InstallQueue(_upgrader, _source, _settings, _history, _time, _log, elevation: silent);
        var items = Channel.CreateUnbounded<InstallItem>();
        queue.Changed += (_, item) => items.Writer.TryWrite(item);
        async Task<InstallItem> NextOne(Func<InstallItem, bool> match)
        {
            while (true)
            {
                var item = await items.Reader.ReadAsync(Ct).AsTask().WaitAsync(Wait, Ct);
                if (match(item)) return item;
            }
        }
        queue.Enqueue([Request(Vlc, InstallRoute.Helper)]);
        var call = await silent.NextCall(Ct);
        Assert.False((await NextOne(_ => true)).AwaitingPermission);
        call.OpenPrompt();
        Assert.True((await NextOne(i => i.AwaitingPermission)).AwaitingPermission);
        call.ClosePrompt();
        Assert.False((await NextOne(i => !i.AwaitingPermission)).AwaitingPermission);
        call.Answer(HelperStartResult.Declined);
        Assert.Equal(UpgradeResult.PermissionDeclined, (await NextOne(i => i.Stage == InstallStage.Done)).Done!.Outcome.Result);
    }

    [Fact]
    public async Task StartThroughSilentModesTask_DoesntSayWaitingForPermission()
    {
        var silent = new FakeElevation { Prompts = false };
        using var queue = new InstallQueue(_upgrader, _source, _settings, _history, _time, _log, elevation: silent);
        var items = Channel.CreateUnbounded<InstallItem>();
        queue.Changed += (_, item) => items.Writer.TryWrite(item);
        queue.Enqueue([Request(Vlc, InstallRoute.Helper)]);
        var session = (await silent.NextCall(Ct)).Approve();
        (await session.Upgrades.NextCall(Ct)).Finish(UpgradeResult.Updated);
        InstallItem item;
        do
        {
            item = await items.Reader.ReadAsync(Ct).AsTask().WaitAsync(Wait, Ct);
            Assert.False(item.AwaitingPermission);
        }
        while (item.Stage != InstallStage.Done);
    }

    [Fact]
    public async Task ElevatedWinGetUnavailable_RunsTheUsersUpdatesItself_AndSaysSoOnce()
    {
        var fallbacks = 0;
        _queue.AdminFallback += (_, _) => Interlocked.Increment(ref fallbacks);
        _queue.Enqueue([Request(Firefox, InstallRoute.Helper), Request(Vlc, InstallRoute.Helper, byItself: true)]);
        var session = (await _elevation.NextCall(Ct)).Approve(winGetAvailable: false);
        await session.Gone.WaitAsync(Wait, Ct);
        var firefox = await _upgrader.NextCall(Ct);
        Assert.Equal(Firefox.Id, firefox.Package.Id);
        firefox.Finish(UpgradeResult.Updated);
        Assert.Equal(UpgradeResult.NeedsAdmin, (await DoneOf(Vlc)).Done!.Outcome.Result);
        await DoneOf(Firefox);
        // It's remembered: the next admin update runs in the app at once, with no prompt.
        _queue.Enqueue([Request(Paint, InstallRoute.Helper)]);
        Assert.Equal(Paint.Id, (await _upgrader.NextCall(Ct)).Package.Id);
        Assert.Equal((1, 1), (_elevation.Count, Volatile.Read(ref fallbacks)));
    }

    [Fact]
    public async Task CancelWhileWaitingForPermission_LeavesNoHistory_AndTheHelperGoesWhenItComes()
    {
        _queue.Enqueue([Request(Vlc, InstallRoute.Helper)]);
        var prompt = await _elevation.NextCall(Ct);
        await Next(i => i.AwaitingPermission);
        _queue.Cancel(Key(Vlc));
        Assert.Equal(UpgradeResult.Cancelled, (await DoneOf(Vlc)).Done!.Outcome.Result);
        var session = prompt.Approve();
        await session.Gone.WaitAsync(Wait, Ct);
        Assert.Equal(0, session.Upgrades.Count);
        Assert.Empty(_history.Entries);
    }

    [Fact]
    public async Task CancelOfAnAdminUpdate_ReachesTheHelper()
    {
        _source.Reply = _ => new([new PackageSnapshot(Vlc.Id, Vlc.Source, Vlc.Name, "3.0.20", "3.0.21")], []);
        _queue.Enqueue([Request(Vlc, InstallRoute.Helper)]);
        var session = (await _elevation.NextCall(Ct)).Approve();
        var call = await session.Upgrades.NextCall(Ct);
        call.Download(10 * MB);
        _queue.Cancel(Key(Vlc));
        await call.Cancelled.WaitAsync(Wait, Ct);
        Assert.Equal(UpgradeResult.Cancelled, (await DoneOf(Vlc)).Done!.Outcome.Result);
    }

    [Fact]
    public async Task BusyHelper_IsRetriedLikeTheApp()
    {
        _queue.Enqueue([Request(Vlc, InstallRoute.Helper)]);
        var session = (await _elevation.NextCall(Ct)).Approve();
        (await session.Upgrades.NextCall(Ct)).Finish(UpgradeResult.Busy);
        await Next(i => i.Busy);
        _time.Advance(InstallTimings.Default.BusyRetryDelay);
        (await session.Upgrades.NextCall(Ct)).Finish(UpgradeResult.Updated);
        Assert.Equal(UpgradeResult.Updated, (await DoneOf(Vlc)).Done!.Outcome.Result);
        Assert.Equal(1, _elevation.Count);
    }

    [Fact]
    public async Task Quit_HangsUpOnTheHelper()
    {
        _queue.Enqueue([Request(Vlc, InstallRoute.Helper)]);
        var session = (await _elevation.NextCall(Ct)).Approve();
        var call = await session.Upgrades.NextCall(Ct);
        _queue.Dispose();
        await call.Cancelled.WaitAsync(Wait, Ct);
        await _queue.Stopped.WaitAsync(Wait, Ct);
        Assert.True(session.Disposed);
    }

    // Hanging up ends the helper's upgrade, not its installer, so the update may still land.
    [Fact]
    public async Task HelperUpdateLeftAtQuit_IsLoggedAsUnknown_NotAsFailed()
    {
        _queue.Enqueue([Request(Vlc, InstallRoute.Helper)]);
        var session = (await _elevation.NextCall(Ct)).Approve();
        var call = await session.Upgrades.NextCall(Ct);
        call.Install();
        await Next(i => i.Stage == InstallStage.Installing);
        _queue.Dispose();
        await _queue.Stopped.WaitAsync(Wait, Ct);
        var log = _folder.PathOf("app.log");
        for (var tries = 0; !ReadLog(log).Contains("VideoLAN.VLC ended after the queue moved on", StringComparison.Ordinal); tries++)
        {
            Assert.True(tries < 200, "Nothing was logged.");
            await Task.Delay(10, Ct);
        }
        Assert.Contains("VideoLAN.VLC ended after the queue moved on: unknown, Quit hung up on the admin helper", ReadLog(log));
    }

    // The line comes from a continuation, so the log may be mid-write when it's read.
    private static string ReadLog(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (IOException)
        {
            return "";
        }
    }

    [Fact]
    public async Task QuitWhileThePromptIsOpen_DropsTheHelperWhenItComes()
    {
        _queue.Enqueue([Request(Vlc, InstallRoute.Helper)]);
        var prompt = await _elevation.NextCall(Ct);
        _queue.Dispose();
        await _queue.Stopped.WaitAsync(Wait, Ct);
        var session = prompt.Approve();
        await session.Gone.WaitAsync(Wait, Ct);
        Assert.Equal(0, session.Upgrades.Count);
    }

    [Fact]
    public async Task WithoutAWayToStartTheHelper_AdminUpdatesFail()
    {
        using var queue = new InstallQueue(_upgrader, _source, _settings, _history, _time, _log);
        var items = Channel.CreateUnbounded<InstallItem>();
        queue.Changed += (_, item) => items.Writer.TryWrite(item);
        queue.Enqueue([Request(Vlc, InstallRoute.Helper)]);
        InstallItem item;
        do item = await items.Reader.ReadAsync(Ct).AsTask().WaitAsync(Wait, Ct);
        while (item.Stage != InstallStage.Done);
        Assert.Equal((UpgradeResult.Failed, UpgradeFailure.HelperNotStarted), (item.Done!.Outcome.Result, item.Done.Outcome.Failure));
    }
}
