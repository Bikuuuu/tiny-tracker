using Microsoft.Extensions.Time.Testing;
using TinyTracker.Core.History;
using TinyTracker.Core.Installing;
using TinyTracker.Core.Logging;
using TinyTracker.Core.SelfUpdate;
using TinyTracker.Core.Storage;
using TinyTracker.Core.Tests.Installing;
using TinyTracker.Core.Tracking;
using Xunit;

namespace TinyTracker.Core.Tests.SelfUpdate;

// Tiny Tracker's own update, from the click or the Auto rules to Setup, and what the restarted app makes of it (spec §6.5).
public sealed class SelfUpdaterTests : IAsyncDisposable
{
    private const ulong MB = 1024 * 1024;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    // Long enough for something wrongly started to show.
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(200);
    private static readonly DateTimeOffset Start = new(2026, 9, 28, 8, 0, 0, TimeSpan.Zero);
    private static readonly SelfVersion Old = new(0, 1, 0);
    private static readonly SelfVersion New = new(0, 2, 0);
    private static readonly SelfRelease Release = new(New, Start.AddDays(-10));
    private static readonly TrackedApp Firefox = new() { Id = "Mozilla.Firefox", Source = "winget", Name = "Firefox", Offer = new Offer { Version = "131.0", FirstSeen = DateTimeOffset.UnixEpoch } };

    private readonly TempFolder _folder = new();
    private readonly FakeTimeProvider _time = new(Start);
    private readonly FakeUpgrader _upgrader = new();
    private readonly FakeSource _source = new();
    private readonly FakeElevation _elevation = new();
    private readonly List<SelfVersion> _toldUpdated = [];
    private readonly SettingsStore _settings;
    private readonly HistoryStore _history;
    private readonly FileLog _log;
    private readonly InstallQueue _queue;
    private readonly List<SelfUpdater> _updaters = [];
    private volatile bool _setupRunning;

    public SelfUpdaterTests()
    {
        _log = new FileLog(_folder.PathOf("app.log"), _time);
        _settings = new SettingsStore(_folder.PathOf("settings.json"));
        _settings.Update(f => f with { Apps = [Firefox] });
        _history = new HistoryStore(_folder.PathOf("history.json"), _time);
        _source.Reply = _ => new([new PackageSnapshot(Firefox.Id, Firefox.Source, Firefox.Name, "131.0", null)], []);
        _queue = new InstallQueue(_upgrader, _source, _settings, _history, _time, _log, elevation: _elevation);
        Updater = Create(Old);
    }

    private SelfUpdater Updater { get; }

    public async ValueTask DisposeAsync()
    {
        foreach (var updater in _updaters) updater.Dispose();
        _queue.Dispose();
        await Task.WhenAll([.. _updaters.Select(u => u.Stopped), _queue.Stopped]).WaitAsync(Wait);
        _log.Close();
        _folder.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SelfUpdater Create(SelfVersion running)
    {
        var updater = new SelfUpdater(running, _elevation, _queue, _settings, _history, _time, _log, () => _setupRunning);
        updater.UpdatedByItself += (_, version) =>
        {
            lock (_toldUpdated) _toldUpdated.Add(version);
        };
        _updaters.Add(updater);
        return updater;
    }

    private static InstallRequest Request(InstallRoute route = InstallRoute.App) =>
        new(new PackageKey(Firefox.Id, Firefox.Source), Firefox.Name, "130.0", "131.0") { Route = route };

    private void Noted(bool automatic, TimeSpan ago, bool setupStarted = true) =>
        _settings.Update(f => f with
        {
            SelfUpdate = new SelfUpdateBook { Note = new SelfUpdateNote { From = "0.1.0", To = "0.2.0", Automatic = automatic, StartedAt = _time.GetUtcNow() - ago, SetupStarted = setupStarted } },
        });

    private async Task<SelfUpdateState> When(Func<SelfUpdateState, bool> condition, SelfUpdater? updater = null)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (true)
        {
            var state = (updater ?? Updater).State;
            if (condition(state)) return state;
            Assert.True(DateTime.UtcNow < deadline, $"Timed out at {state}.");
            await Task.Delay(10, Ct);
        }
    }

    // A click, the prompt approved, and the helper's self-update running.
    private async Task<(FakeSession Session, UpgradeCall Call)> Running(bool automatic = false)
    {
        Updater.Offer(Release);
        Updater.Update(automatic);
        var session = (await _elevation.NextCall(Ct)).Approve();
        return (session, await session.SelfUpdates.NextCall(Ct));
    }

    private async Task Installing()
    {
        var (_, call) = await Running();
        call.Finish(UpgradeResult.Updated);
        await When(s => s.Stage == SelfUpdateStage.Installing);
    }

    // The prompt of an attempt that was cancelled closes late: the next attempt's row still says it waits for permission.
    [Fact]
    public async Task CancelledAttemptsPrompt_ClosingLate_LeavesTheNextOneWaiting()
    {
        Updater.Offer(Release);
        Updater.Update(automatic: false);
        var first = await _elevation.NextCall(Ct);
        Updater.Cancel();
        await When(s => s.Stage == SelfUpdateStage.Available);
        Updater.Update(automatic: false);
        await _elevation.NextCall(Ct);
        await When(s => s.Stage == SelfUpdateStage.AwaitingPermission);
        first.ClosePrompt();
        Assert.Equal(SelfUpdateStage.AwaitingPermission, Updater.State.Stage);
    }

    // The row says "Waiting for permission…" only while the prompt shows (spec §4.3).
    [Fact]
    public async Task AnsweredPrompt_MovesTheRowOnToDownloading()
    {
        Updater.Offer(Release);
        Updater.Update(automatic: false);
        await When(s => s.Stage == SelfUpdateStage.AwaitingPermission);
        var call = await _elevation.NextCall(Ct);
        call.ClosePrompt();
        await When(s => s.Stage == SelfUpdateStage.Downloading);
        var session = call.Approve();
        (await session.SelfUpdates.NextCall(Ct)).Finish(UpgradeResult.Updated);
        await When(s => s.Stage == SelfUpdateStage.Installing);
    }

    // In silent mode a missing task makes the launcher prompt after all; the row says so while the prompt shows (spec §6.6).
    [Fact]
    public async Task PromptInSilentMode_SaysWaitingForPermission_WhileItShows()
    {
        var silent = new FakeElevation { Prompts = false };
        var updater = new SelfUpdater(Old, silent, _queue, _settings, _history, _time, _log, () => _setupRunning);
        _updaters.Add(updater);
        updater.Offer(Release);
        updater.Update(automatic: false);
        var call = await silent.NextCall(Ct);
        Assert.Equal(SelfUpdateStage.Downloading, (await When(s => s.Stage != SelfUpdateStage.WaitsForOthers, updater)).Stage);
        call.OpenPrompt();
        await When(s => s.Stage == SelfUpdateStage.AwaitingPermission, updater);
        call.ClosePrompt();
        await When(s => s.Stage == SelfUpdateStage.Downloading, updater);
        call.Answer(HelperStartResult.Declined);
        await When(s => s is { Stage: SelfUpdateStage.Available, Declined: true }, updater);
    }

    // An error nothing expected ends the attempt as a failure to retry, logged and in History, and Quit still stops cleanly (spec §6.5).
    [Fact]
    public async Task UnexpectedError_IsAFailure_LoggedAndInHistory()
    {
        var (_, call) = await Running();
        call.Result.TrySetException(new InvalidOperationException("Something unexpected."));
        var state = await When(s => s.Stage == SelfUpdateStage.Failed);
        // InvalidOperationException's HRESULT goes under Details.
        Assert.Equal(new UpgradeOutcome(UpgradeResult.Failed, UpgradeFailure.Other, "0x80131509"), state.Outcome);
        var entry = Assert.Single(_history.Entries);
        Assert.Equal((HistoryResult.Failed, "0x80131509"), (entry.Result, entry.Code));
        Assert.Contains("ERROR Self-update failed", File.ReadAllText(_folder.PathOf("app.log")));
        Updater.Dispose();
        await Updater.Stopped.WaitAsync(Wait, Ct);
    }

    [Fact]
    public void NewerRelease_IsAvailable()
    {
        Updater.Offer(Release);
        Assert.Equal(new SelfUpdateState(SelfUpdateStage.Available, Release), Updater.State);
    }

    [Theory]
    [InlineData(0, 1, 0)]
    [InlineData(0, 0, 9)]
    public void ReleaseThatIsntNewer_IsNothing(int major, int minor, int patch)
    {
        Updater.Offer(Release with { Version = new SelfVersion(major, minor, patch) });
        Assert.Equal(SelfUpdateStage.None, Updater.State.Stage);
    }

    [Fact]
    public void ReleaseThatWent_TakesTheRowWithIt()
    {
        Updater.Offer(Release);
        Updater.Offer(null);
        Assert.Equal(SelfUpdateStage.None, Updater.State.Stage);
    }

    // settings.json says what's under way before the helper hears of it, so the app that Setup starts again can tell.
    [Fact]
    public async Task Update_NotesItself_ThenAsksTheHelperForTheVersionAlone()
    {
        Updater.Offer(Release);
        Updater.Update(automatic: false);
        var start = await _elevation.NextCall(Ct);
        Assert.True(start.MayPrompt);
        Assert.Equal(new SelfUpdateNote { From = "0.1.0", To = "0.2.0", Automatic = false, StartedAt = Start }, _settings.Current.SelfUpdate.Note);
        Assert.Equal(("0.2.0", (DateTimeOffset?)Start), (_settings.Current.SelfUpdate.AttemptedVersion, _settings.Current.SelfUpdate.AttemptedAt));
        var call = await start.Approve().SelfUpdates.NextCall(Ct);
        Assert.Equal((SelfUpdater.Key, "0.2.0"), (call.Package, call.Version));
    }

    // Its hour counts from Setup's start, and a restart can tell that Setup started. The note says so before the row does.
    [Fact]
    public async Task SetupThatStarted_MarksTheNote_AsOfThen()
    {
        var noted = new List<SelfUpdateNote?>();
        Updater.Changed += (_, state) =>
        {
            if (state.Stage == SelfUpdateStage.Installing) lock (noted) noted.Add(_settings.Current.SelfUpdate.Note);
        };
        var (_, call) = await Running();
        _time.Advance(TimeSpan.FromMinutes(20));
        call.Finish(UpgradeResult.Updated);
        await When(s => s.Stage == SelfUpdateStage.Installing);
        var marked = new SelfUpdateNote { From = "0.1.0", To = "0.2.0", Automatic = false, StartedAt = Start.AddMinutes(20), SetupStarted = true };
        lock (noted) Assert.Equal([marked], noted);
    }

    // Without its note, the app that Setup starts again couldn't tell what happened, so no helper is asked.
    [Fact]
    public async Task NoteThatCantBeSaved_AsksNoHelper_AndFails()
    {
        Updater.Offer(Release);
        using (new FileStream(_folder.PathOf("settings.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Updater.Update(automatic: false);
            var failed = await When(s => s.Stage == SelfUpdateStage.Failed);
            Assert.Equal(new UpgradeOutcome(UpgradeResult.Failed, UpgradeFailure.Other, "settings not saved"), failed.Outcome);
        }
        Assert.Equal(0, _elevation.Count);
        Assert.Equal("Other", Assert.Single(_history.Entries).Reason);
    }

    [Fact]
    public async Task ThePrompt_TheDownload_ThenSetup_AreShown()
    {
        Updater.Offer(Release);
        Updater.Update(automatic: false);
        Assert.Equal(SelfUpdateStage.AwaitingPermission, Updater.State.Stage);
        var session = (await _elevation.NextCall(Ct)).Approve();
        var call = await session.SelfUpdates.NextCall(Ct);
        call.Download(10 * MB, 50 * MB);
        var downloading = await When(s => s.Progress.BytesDownloaded == 10 * MB);
        Assert.Equal((SelfUpdateStage.Downloading, 50 * MB), (downloading.Stage, downloading.Progress.BytesRequired));
        call.Finish(UpgradeResult.Updated);
        await When(s => s.Stage == SelfUpdateStage.Installing);
        await session.Gone.WaitAsync(Wait, Ct);
        Assert.NotNull(_settings.Current.SelfUpdate.Note);
        Assert.Empty(_history.Entries);
    }

    // Setup can't replace the helper while the queue's runs, and it closes the app: nothing else starts meanwhile (spec §6.5).
    [Fact]
    public async Task WhileItRuns_TheQueueStartsNothing()
    {
        await Running();
        _queue.Enqueue([Request(InstallRoute.Helper)]);
        await Task.Delay(Settle, Ct);
        Assert.Equal((0, 1), (_upgrader.Count, _elevation.Count));
    }

    [Fact]
    public async Task OtherUpdates_AreWaitedFor()
    {
        _queue.Enqueue([Request()]);
        var app = await _upgrader.NextCall(Ct);
        Updater.Offer(Release);
        Updater.Update(automatic: false);
        Assert.Equal(SelfUpdateStage.WaitsForOthers, Updater.State.Stage);
        await Task.Delay(Settle, Ct);
        Assert.Equal(0, _elevation.Count);
        app.Finish(UpgradeResult.Updated);
        await _elevation.NextCall(Ct);
        await When(s => s.Stage == SelfUpdateStage.AwaitingPermission);
    }

    [Fact]
    public async Task DeclinedPrompt_OffersUpdateAgain_AndLetsTheQueueGo()
    {
        Updater.Offer(Release);
        Updater.Update(automatic: false);
        (await _elevation.NextCall(Ct)).Answer(HelperStartResult.Declined);
        Assert.Equal(SelfUpdateStage.Available, (await When(s => s.Declined)).Stage);
        Assert.Null(_settings.Current.SelfUpdate.Note);
        Assert.Equal("PermissionDeclined", Assert.Single(_history.Entries).Reason);
        _queue.Enqueue([Request()]);
        (await _upgrader.NextCall(Ct)).Finish(UpgradeResult.Updated);
    }

    [Fact]
    public async Task HelperThatDidntStart_Failed()
    {
        Updater.Offer(Release);
        Updater.Update(automatic: false);
        (await _elevation.NextCall(Ct)).Answer(HelperStartResult.Failed, "0x80070005");
        var failed = await When(s => s.Stage == SelfUpdateStage.Failed);
        Assert.Equal(new UpgradeOutcome(UpgradeResult.Failed, UpgradeFailure.HelperNotStarted, "0x80070005"), failed.Outcome);
        Assert.Equal(("HelperNotStarted", "0x80070005"), (Assert.Single(_history.Entries).Reason, _history.Entries[0].Code));
    }

    // Whoever hears of the failure finds it in History already.
    [Fact]
    public async Task Failure_IsInHistory_ByTheTimeItShows()
    {
        var inHistory = new List<int>();
        Updater.Changed += (_, state) =>
        {
            if (state.Stage == SelfUpdateStage.Failed) lock (inHistory) inHistory.Add(_history.Entries.Count);
        };
        var (_, call) = await Running();
        call.Finish(UpgradeResult.Failed, UpgradeFailure.DigestMismatch, "code");
        await When(s => s.Stage == SelfUpdateStage.Failed);
        lock (inHistory) Assert.Equal([1], inHistory);
    }

    // One that started by itself never prompts: without silent mode's task, it waits for the click.
    [Fact]
    public async Task UpdateThatStartedByItself_WithoutTheTask_WaitsForTheClick()
    {
        Updater.Offer(Release);
        Updater.Update(automatic: true);
        var start = await _elevation.NextCall(Ct);
        Assert.False(start.MayPrompt);
        start.Answer(HelperStartResult.NeedsPrompt);
        await When(s => s.Stage == SelfUpdateStage.Available);
        Assert.Null(_settings.Current.SelfUpdate.Note);
    }

    public static TheoryData<UpgradeFailure> Failures() => new() { UpgradeFailure.DigestMismatch, UpgradeFailure.OtherAccounts, UpgradeFailure.GitHubUnreachable, UpgradeFailure.ReleaseRefused };

    // The Auto rules try an automatic one again once a problem that can pass is 12 h old (spec §6.2).
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Failure_SaysWhetherItsAttemptWasAutomatic(bool automatic)
    {
        var (_, call) = await Running(automatic);
        call.Finish(UpgradeResult.Failed, UpgradeFailure.GitHubUnreachable, "code");
        Assert.Equal(automatic, (await When(s => s.Stage == SelfUpdateStage.Failed)).Automatic);
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task HelpersFailure_IsShown_AndRecorded_AndItsNoteGoes(UpgradeFailure failure)
    {
        var (_, call) = await Running();
        call.Finish(UpgradeResult.Failed, failure, "code");
        var failed = await When(s => s.Stage == SelfUpdateStage.Failed);
        Assert.Equal(new UpgradeOutcome(UpgradeResult.Failed, failure, "code"), failed.Outcome);
        var entry = Assert.Single(_history.Entries);
        Assert.Equal((SelfUpdater.Key.Id, SelfUpdater.Key.Source, "Tiny Tracker", HistoryResult.Failed, "0.1.0", "0.2.0", failure.ToString(), "code"),
            (entry.Id, entry.Source, entry.Name, entry.Result, entry.FromVersion, entry.ToVersion, entry.Reason, entry.Code));
        Assert.Null(_settings.Current.SelfUpdate.Note);
        _queue.Enqueue([Request()]);
        (await _upgrader.NextCall(Ct)).Finish(UpgradeResult.Updated);
    }

    // A failure that waits for the user keeps its version in settings.json, so it waits after a restart too; one that was automatic and
    // can pass goes again after 12 h instead (spec §6.5).
    [Theory]
    [InlineData(true, UpgradeFailure.DigestMismatch, "0.2.0")]
    [InlineData(true, UpgradeFailure.GitHubUnreachable, null)]
    [InlineData(false, UpgradeFailure.GitHubUnreachable, "0.2.0")]
    public async Task FailureThatWaitsForTheUser_IsKept(bool automatic, UpgradeFailure failure, string? kept)
    {
        var (_, call) = await Running(automatic);
        call.Finish(UpgradeResult.Failed, failure);
        await When(s => s.Stage == SelfUpdateStage.Failed);
        Assert.Equal(kept, _settings.Current.SelfUpdate.FailedVersion);
    }

    // The click is the user's answer.
    [Fact]
    public async Task ClickedUpdate_ForgetsTheKeptFailure()
    {
        _settings.Update(f => f with { SelfUpdate = f.SelfUpdate with { FailedVersion = "0.2.0" } });
        await Running();
        Assert.Null(_settings.Current.SelfUpdate.FailedVersion);
    }

    // An error nothing expected isn't one that passes by itself.
    [Fact]
    public async Task UnexpectedErrorOfAnAutomaticAttempt_IsKept()
    {
        var (_, call) = await Running(automatic: true);
        call.Result.TrySetException(new InvalidOperationException("Something unexpected."));
        await When(s => s.Stage == SelfUpdateStage.Failed);
        Assert.Equal("0.2.0", _settings.Current.SelfUpdate.FailedVersion);
    }

    [Fact]
    public async Task Retry_AfterAFailure_RunsAgain()
    {
        var (_, call) = await Running();
        call.Finish(UpgradeResult.Failed, UpgradeFailure.DownloadFailed);
        await When(s => s.Stage == SelfUpdateStage.Failed);
        Updater.Update(automatic: false);
        var again = (await _elevation.NextCall(Ct)).Approve();
        Assert.Equal("0.2.0", (await again.SelfUpdates.NextCall(Ct)).Version);
    }

    [Fact]
    public async Task SecondClick_StartsNoSecondUpdate()
    {
        Updater.Offer(Release);
        Updater.Update(automatic: false);
        Updater.Update(automatic: false);
        await _elevation.NextCall(Ct);
        await Task.Delay(Settle, Ct);
        Assert.Equal(1, _elevation.Count);
    }

    [Fact]
    public async Task Cancel_WhileItDownloads_StopsIt_WithNothingRecorded()
    {
        var (_, call) = await Running();
        call.Download(MB);
        await When(s => s.Stage == SelfUpdateStage.Downloading);
        Updater.Cancel();
        await call.Cancelled.WaitAsync(Wait, Ct);
        await When(s => s.Stage == SelfUpdateStage.Available);
        Assert.Empty(_history.Entries);
        Assert.Null(_settings.Current.SelfUpdate.Note);
    }

    [Fact]
    public async Task Cancel_WhileItWaitsForOthers_StartsNothing()
    {
        _queue.Enqueue([Request()]);
        var app = await _upgrader.NextCall(Ct);
        Updater.Offer(Release);
        Updater.Update(automatic: false);
        Updater.Cancel();
        await When(s => s.Stage == SelfUpdateStage.Available);
        app.Finish(UpgradeResult.Updated);
        await Task.Delay(Settle, Ct);
        Assert.Equal(0, _elevation.Count);
    }

    // A cancel counts as an attempt too, so the next automatic one waits out its 12 h (spec §6.2).
    [Fact]
    public async Task CancelWhileItWaitsForOthers_StillCountsAsAnAttempt()
    {
        _queue.Enqueue([Request()]);
        var app = await _upgrader.NextCall(Ct);
        Updater.Offer(Release);
        Updater.Update(automatic: true);
        Updater.Cancel();
        await When(s => s.Stage == SelfUpdateStage.Available);
        Assert.Equal(("0.2.0", (DateTimeOffset?)Start), (_settings.Current.SelfUpdate.AttemptedVersion, _settings.Current.SelfUpdate.AttemptedAt));
        Assert.Null(_settings.Current.SelfUpdate.Note);
        app.Finish(UpgradeResult.Updated);
    }

    [Fact]
    public async Task Cancel_OnceSetupStarted_DoesNothing()
    {
        await Installing();
        Updater.Cancel();
        await Task.Delay(Settle, Ct);
        Assert.Equal(SelfUpdateStage.Installing, Updater.State.Stage);
    }

    [Fact]
    public async Task Quit_DuringTheDownload_StopsIt_AndForgetsItsNote()
    {
        var (_, call) = await Running();
        Updater.Dispose();
        await Updater.Stopped.WaitAsync(Wait, Ct);
        await call.Cancelled.WaitAsync(Wait, Ct);
        Assert.Null(_settings.Current.SelfUpdate.Note);
        Assert.Empty(_history.Entries);
    }

    [Fact]
    public async Task Quit_OnceSetupStarted_KeepsTheNote()
    {
        await Installing();
        Updater.Dispose();
        await Updater.Stopped.WaitAsync(Wait, Ct);
        Assert.NotNull(_settings.Current.SelfUpdate.Note);
    }

    // Still here once Setup ended: it failed before it could close the app.
    [Fact]
    public async Task SetupThatEndedWithTheAppStillHere_Failed()
    {
        _setupRunning = true;
        await Installing();
        _time.Advance(SelfUpdater.SetupGrace);
        await Task.Delay(Settle, Ct);
        Assert.Equal(SelfUpdateStage.Installing, Updater.State.Stage);
        _setupRunning = false;
        _time.Advance(SelfUpdater.WatchEvery);
        Assert.Equal(UpgradeFailure.InstallerFailed, (await When(s => s.Stage == SelfUpdateStage.Failed)).Outcome?.Failure);
        Assert.Equal("InstallerFailed", Assert.Single(_history.Entries).Reason);
        Assert.Null(_settings.Current.SelfUpdate.Note);
        Assert.Equal("0.2.0", _settings.Current.SelfUpdate.FailedVersion);
        _queue.Enqueue([Request()]);
        (await _upgrader.NextCall(Ct)).Finish(UpgradeResult.Updated);
    }

    // Setup's loader may not show yet, so it gets its grace first.
    [Fact]
    public async Task SetupNotSeenYet_GetsItsGrace()
    {
        await Installing();
        _time.Advance(SelfUpdater.SetupGrace - TimeSpan.FromSeconds(1));
        await Task.Delay(Settle, Ct);
        Assert.Equal(SelfUpdateStage.Installing, Updater.State.Stage);
        _time.Advance(TimeSpan.FromSeconds(1));
        await When(s => s.Stage == SelfUpdateStage.Failed);
    }

    [Fact]
    public async Task FailedVersion_StaysFailed_UntilANewerOneComes()
    {
        var (_, call) = await Running();
        call.Finish(UpgradeResult.Failed, UpgradeFailure.DownloadFailed);
        await When(s => s.Stage == SelfUpdateStage.Failed);
        Updater.Offer(Release);
        Assert.Equal(SelfUpdateStage.Failed, Updater.State.Stage);
        var newer = new SelfRelease(new SelfVersion(0, 3, 0), Start);
        Updater.Offer(newer);
        Assert.Equal(new SelfUpdateState(SelfUpdateStage.Available, newer), Updater.State);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RestartOnTheNewVersion_IsRecorded_AndToldOnlyWhenItWasAutomatic(bool automatic)
    {
        Noted(automatic, TimeSpan.FromMinutes(2));
        var updater = Create(New);
        updater.Restarted(New);
        var entry = Assert.Single(_history.Entries);
        Assert.Equal((SelfUpdater.Key.Id, SelfUpdater.Key.Source, "Tiny Tracker", HistoryResult.Updated, "0.1.0", "0.2.0", (string?)null),
            (entry.Id, entry.Source, entry.Name, entry.Result, entry.FromVersion, entry.ToVersion, entry.Reason));
        Assert.Null(_settings.Current.SelfUpdate.Note);
        lock (_toldUpdated) Assert.Equal(automatic ? new[] { New } : [], _toldUpdated);
        Assert.Equal(SelfUpdateStage.None, updater.State.Stage);
    }

    // A note that can't be cleared is read again at the next start, but its update is recorded and told once (spec §6.5), when the
    // clock went back since Setup started too.
    [Theory]
    [InlineData(2)]
    [InlineData(-300)]
    public void NoteThatCantBeCleared_IsRecordedAndToldOnce(int minutesAgo)
    {
        Noted(automatic: true, TimeSpan.FromMinutes(minutesAgo));
        using (new FileStream(_folder.PathOf("settings.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Create(New).Restarted(New);
            Create(New).Restarted(New);
        }
        Assert.NotNull(_settings.Current.SelfUpdate.Note);
        Assert.Single(_history.Entries);
        lock (_toldUpdated) Assert.Equal([New], _toldUpdated);
    }

    // The same update again, as after going back to the old version, is one of its own.
    [Fact]
    public void SameUpdateLater_IsRecordedAndToldAgain()
    {
        Noted(automatic: true, TimeSpan.FromMinutes(2));
        Create(New).Restarted(New);
        _time.Advance(TimeSpan.FromDays(1));
        Noted(automatic: true, TimeSpan.FromMinutes(2));
        Create(New).Restarted(New);
        Assert.Equal(2, _history.Entries.Count);
        lock (_toldUpdated) Assert.Equal([New, New], _toldUpdated);
    }

    // A failure the same way: in History once, and the next start's row doesn't make it news again.
    [Fact]
    public void FailedNoteThatCantBeCleared_IsRecordedOnce_AndNotNewsAgain()
    {
        Noted(automatic: true, TimeSpan.FromMinutes(2));
        SelfUpdater second;
        using (new FileStream(_folder.PathOf("settings.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Updater.Restarted(Old);
            Assert.True(Updater.State.Automatic);
            second = Create(Old);
            second.Restarted(Old);
        }
        Assert.Single(_history.Entries);
        Assert.Equal((SelfUpdateStage.Failed, false), (second.State.Stage, second.State.Automatic));
    }

    // After a restart the row knows the release only from its note, until a check brings GitHub's record of it.
    [Fact]
    public void FailureAfterARestart_TakesTheReleaseACheckBrings()
    {
        Noted(automatic: true, TimeSpan.FromMinutes(2));
        Updater.Restarted(Old);
        Assert.True(Updater.State.Automatic);
        Updater.Offer(Release);
        Assert.Equal((SelfUpdateStage.Failed, Release, true), (Updater.State.Stage, Updater.State.Release, Updater.State.Automatic));
    }

    [Fact]
    public void RestartOnTheOldVersion_OnceSetupEnded_Failed()
    {
        Noted(automatic: true, TimeSpan.FromMinutes(2));
        Updater.Restarted(Old);
        Assert.Equal(("InstallerFailed", HistoryResult.Failed), (Assert.Single(_history.Entries).Reason, _history.Entries[0].Result));
        Assert.Null(_settings.Current.SelfUpdate.Note);
        Assert.Equal("0.2.0", _settings.Current.SelfUpdate.FailedVersion);
        Assert.Equal((SelfUpdateStage.Failed, New, UpgradeFailure.InstallerFailed), (Updater.State.Stage, Updater.State.Release?.Version, Updater.State.Outcome?.Failure));
        lock (_toldUpdated) Assert.Empty(_toldUpdated);
    }

    // Stopped before Setup started, such as by a shutdown during the download: nothing failed, so nothing says so.
    [Fact]
    public void RestartAfterASetupThatNeverStarted_ForgetsTheNote_Quietly()
    {
        Noted(automatic: true, TimeSpan.FromMinutes(2), setupStarted: false);
        Updater.Restarted(Old);
        Assert.Empty(_history.Entries);
        Assert.Null(_settings.Current.SelfUpdate.Note);
        Assert.Equal(SelfUpdateStage.None, Updater.State.Stage);
        Updater.Offer(Release);
        Assert.Equal(SelfUpdateStage.Available, Updater.State.Stage);
    }

    // Restart Manager closed the app before it marked its note, and Setup went on to fail: that's still said.
    [Fact]
    public async Task RestartWhileAnUnmarkedSetupRuns_WaitsForIt_ThenSaysItFailed()
    {
        Noted(automatic: true, TimeSpan.FromMinutes(2), setupStarted: false);
        _setupRunning = true;
        Updater.Restarted(Old);
        Assert.Equal(SelfUpdateStage.Installing, Updater.State.Stage);
        _setupRunning = false;
        _time.Advance(SelfUpdater.WatchEvery);
        await When(s => s.Stage == SelfUpdateStage.Failed);
        Assert.Equal("InstallerFailed", Assert.Single(_history.Entries).Reason);
    }

    [Fact]
    public async Task RestartWhileSetupStillRuns_WaitsForIt()
    {
        Noted(automatic: false, TimeSpan.FromMinutes(2));
        _setupRunning = true;
        Updater.Restarted(Old);
        Assert.Equal(SelfUpdateStage.Installing, Updater.State.Stage);
        Assert.Empty(_history.Entries);
        _setupRunning = false;
        _time.Advance(SelfUpdater.WatchEvery);
        await When(s => s.Stage == SelfUpdateStage.Failed);
        Assert.Single(_history.Entries);
    }

    // Setup replaces the helper and closes apps, so after a restart too, nothing else starts until it ends (spec §6.5).
    [Fact]
    public async Task RestartWhileSetupStillRuns_HoldsTheQueue_UntilItEnds()
    {
        Noted(automatic: false, TimeSpan.FromMinutes(2));
        _setupRunning = true;
        Updater.Restarted(Old);
        _queue.Enqueue([Request()]);
        await Task.Delay(Settle, Ct);
        Assert.Equal(0, _upgrader.Count);
        _setupRunning = false;
        _time.Advance(SelfUpdater.WatchEvery);
        await When(s => s.Stage == SelfUpdateStage.Failed);
        (await _upgrader.NextCall(Ct)).Finish(UpgradeResult.Updated);
    }

    // An update the queue still ran at the restart ends first; then the queue is held until Setup ends.
    [Fact]
    public async Task RestartWhileTheQueueWorks_HoldsItOnceItsDone()
    {
        Noted(automatic: false, TimeSpan.FromMinutes(2));
        _setupRunning = true;
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.BecameIdle += (_, _) => idle.TrySetResult();
        _queue.Enqueue([Request()]);
        var running = await _upgrader.NextCall(Ct);
        Updater.Restarted(Old);
        running.Finish(UpgradeResult.Updated);
        await idle.Task.WaitAsync(Wait, Ct);
        _time.Advance(SelfUpdater.WatchEvery);
        _queue.Enqueue([Request()]);
        await Task.Delay(Settle, Ct);
        Assert.Equal(1, _upgrader.Count);
        _setupRunning = false;
        _time.Advance(SelfUpdater.WatchEvery);
        await When(s => s.Stage == SelfUpdateStage.Failed);
        (await _upgrader.NextCall(Ct)).Finish(UpgradeResult.Updated);
    }

    // Quit lets the queue go too.
    [Fact]
    public async Task QuitWhileSetupStillRuns_LetsTheQueueGo()
    {
        Noted(automatic: false, TimeSpan.FromMinutes(2));
        _setupRunning = true;
        Updater.Restarted(Old);
        Updater.Dispose();
        await Updater.Stopped.WaitAsync(Wait, Ct);
        _queue.Enqueue([Request()]);
        (await _upgrader.NextCall(Ct)).Finish(UpgradeResult.Updated);
    }

    [Fact]
    public void RestartWithNoNote_ChangesNothing()
    {
        Updater.Restarted(Old);
        Assert.Equal(SelfUpdateStage.None, Updater.State.Stage);
        Assert.Empty(_history.Entries);
    }

    // The demo pretends the restart in the same app.
    [Fact]
    public async Task RestartedOnTheNewVersion_EndsTheRow()
    {
        await Installing();
        Updater.Restarted(New);
        Assert.Equal(SelfUpdateStage.None, Updater.State.Stage);
        Assert.Equal(HistoryResult.Updated, Assert.Single(_history.Entries).Result);
        _queue.Enqueue([Request()]);
        (await _upgrader.NextCall(Ct)).Finish(UpgradeResult.Updated);
    }
}
