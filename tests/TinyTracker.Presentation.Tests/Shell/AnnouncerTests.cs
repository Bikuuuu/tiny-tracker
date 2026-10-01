using Microsoft.Extensions.Time.Testing;
using TinyTracker.Core.Checking;
using TinyTracker.Core.Installing;
using TinyTracker.Core.Logging;
using TinyTracker.Core.Scheduling;
using TinyTracker.Core.Settings;
using TinyTracker.Core.Storage;
using TinyTracker.Core.Tracking;
using TinyTracker.Presentation.Settings;
using TinyTracker.Presentation.Shell;
using Xunit;
using static TinyTracker.Presentation.Tests.Fixtures;

namespace TinyTracker.Presentation.Tests.Shell;

public sealed class AnnouncerTests : IAsyncDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly TempFolder _folder = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly TestUi _ui = new();
    private readonly FakeToasts _toasts = new();
    private readonly FakeConditions _conditions = new();
    private readonly SettingsStore _settings;
    private readonly FileLog _log;
    private readonly SettingsWriter _writer;
    private readonly Announcer _announcer;
    private bool _flyoutOpen;

    public AnnouncerTests()
    {
        _settings = new SettingsStore(_folder.PathOf("settings.json"));
        _log = new FileLog(_folder.PathOf("app.log"), _time);
        _writer = new SettingsWriter(_settings, _log, _ui.Post);
        _announcer = Start();
    }

    // Saves a test left queued land before the folder goes.
    public async ValueTask DisposeAsync()
    {
        await _writer.Idle.WaitAsync(Wait);
        _announcer.Dispose();
        _log.Close();
        _folder.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Announcer Start() => new(_toasts, _conditions, _settings, _writer, () => _flyoutOpen, _time, _ui.Post);

    private void Checked(params AppCheck[] apps) => Checked(_announcer, apps);

    private void Checked(Announcer announcer, params AppCheck[] apps) =>
        announcer.CheckFinished(new CheckCompleted(new CheckTicket(1, CheckTrigger.Scheduled), _time.GetUtcNow(), apps, CheckProblem.None));

    private static AppCheck Seen(AppCheck check) => check with { App = check.App with { Offer = check.App.Offer! with { Announced = true } } };

    private async Task Saved()
    {
        await _writer.Idle.WaitAsync(Wait, Ct);
        _ui.Pump();
    }

    private static AppCheck New(string id, bool auto = false, InstallScope scope = InstallScope.Machine) => Check(AppStatus.Available, id, auto: auto, scope: scope, newVersion: true);

    private void Pass(TimeSpan time)
    {
        _time.Advance(time);
        _ui.Pump();
    }

    private void Installed(string id, UpgradeResult result)
    {
        _announcer.InstallChanged(Item(InstallStage.Waiting, id));
        _announcer.InstallChanged(Done(result, id));
    }

    [Fact]
    public void NewVersionsTheUserInstalls_AreAnnouncedOnce()
    {
        Checked(New("Example.Editor"), New("Example.Paint"));
        var toast = Assert.Single(_toasts.Shown);
        Assert.Equal(("ready", "2 updates ready", "Example Editor and Example Paint"), (toast.Kind, toast.Title, toast.Body));
        Assert.Equal([("Update all", ToastAction.UpdateAll), ("View", ToastAction.View)], toast.Buttons.Select(b => (b.Text, b.Action)));
        Checked(Check(AppStatus.Available), Check(AppStatus.Available, "Example.Paint"));
        Assert.Single(_toasts.Shown);
    }

    [Fact]
    public void AutoAppsThatNeedAdmin_AskForPermission_InTheirOwnToast_Once()
    {
        Checked(New("Example.Editor", auto: true, scope: InstallScope.User), New("Example.Paint", auto: true), New("Example.Clock"));
        Assert.Equal([("ready", "1 update ready", "Example Clock"), ("permission", "1 update needs your permission", "Example Paint")],
            _toasts.Shown.Select(t => (t.Kind, t.Title, t.Body)));
        Assert.Equal([("Install", ToastAction.Install), ("Later", ToastAction.Later)], _toasts.Shown[1].Buttons.Select(b => (b.Text, b.Action)));
        Checked(Check(AppStatus.Available, "Example.Paint", auto: true));
        Assert.Equal(2, _toasts.Shown.Count);
    }

    // Update apps automatically (spec §6.2): apps without their own choice are on Auto; one whose own choice is off isn't.
    [Fact]
    public void SwitchOn_PutsAppsWithoutTheirOwnChoiceOnAuto()
    {
        _settings.Update(f => f with { Settings = f.Settings with { AutoUpdateApps = true } });
        var manual = New("Example.Clock");
        Checked(New("Example.Editor", scope: InstallScope.User), New("Example.Paint"), manual with { App = manual.App with { AutoChoice = false } });
        Assert.Equal([("ready", "1 update ready", "Example Clock"), ("permission", "1 update needs your permission", "Example Paint")],
            _toasts.Shown.Select(t => (t.Kind, t.Title, t.Body)));
    }

    [Fact]
    public void InSilentMode_AutoAppsThatNeedAdmin_InstallByThemselves_SoAreNotNews()
    {
        _settings.Update(f => f with { Settings = f.Settings with { SilentMode = true } });
        Checked(New("Example.Paint", auto: true));
        Assert.Empty(_toasts.Shown);
    }

    [Fact]
    public void InstallsThatNeedAdmin_AskForPermission_InsteadOfCountingAsFailed()
    {
        Installed("Example.Editor", UpgradeResult.NeedsAdmin);
        Installed("Example.Paint", UpgradeResult.NeedsAdmin);
        Installed("Example.Clock", UpgradeResult.Updated);
        Pass(Announcer.BatchQuiet);
        Assert.Equal([("permission", "2 updates need your permission", "Example Editor and Example Paint"), ("batch", "1 update installed", "Example Clock")],
            _toasts.Shown.Select(t => (t.Kind, t.Title, t.Body)));
    }

    [Fact]
    public void VersionAnInstallsPermissionToastNamed_IsNotNewsAgain()
    {
        Installed("Example.Paint", UpgradeResult.NeedsAdmin);
        Pass(Announcer.BatchQuiet);
        Checked(New("Example.Paint", auto: true));
        Assert.Equal(["permission"], _toasts.Shown.Select(t => t.Kind));
    }

    [Fact]
    public void AppsInUse_AskToClose_InsteadOfCountingAsFailed()
    {
        Installed("Example.Editor", UpgradeResult.AppInUse);
        Pass(Announcer.BatchQuiet);
        var toast = Assert.Single(_toasts.Shown);
        Assert.Equal(("close", "Example Editor needs to close to update", ""), (toast.Kind, toast.Title, toast.Body));
        Assert.Equal([("Close & update", ToastAction.CloseAndUpdate), ("Later", ToastAction.Later)], toast.Buttons.Select(b => (b.Text, b.Action)));
        Installed("Example.Editor", UpgradeResult.AppInUse);
        Installed("Example.Paint", UpgradeResult.AppInUse);
        Pass(Announcer.BatchQuiet);
        Assert.Equal(("2 apps need to close to update", "Example Editor and Example Paint"), (_toasts.Shown[1].Title, _toasts.Shown[1].Body));
    }

    [Fact]
    public void AppThatCouldNotClose_CountsAsFailed()
    {
        Installed("Example.Editor", UpgradeResult.CouldNotClose);
        Pass(Announcer.BatchQuiet);
        var toast = Assert.Single(_toasts.Shown);
        Assert.Equal(("batch", "1 update failed"), (toast.Kind, toast.Title));
    }

    [Fact]
    public void AppsThatEndInUseOrNeedAdmin_WhileTheFlyoutIsOpen_AreOnThePage()
    {
        _flyoutOpen = true;
        Installed("Example.Editor", UpgradeResult.AppInUse);
        Installed("Example.Paint", UpgradeResult.NeedsAdmin);
        _flyoutOpen = false;
        Pass(Announcer.BatchQuiet);
        Assert.Empty(_toasts.Shown);
    }

    [Fact]
    public void PermissionAndCloseToasts_WaitDuringAGame_Too()
    {
        _conditions.State = _conditions.State with { FullScreen = true };
        Checked(New("Example.Paint", auto: true));
        Installed("Example.Editor", UpgradeResult.AppInUse);
        Pass(Announcer.BatchQuiet);
        Assert.Empty(_toasts.Shown);
        _conditions.State = _conditions.State with { FullScreen = false };
        Pass(Announcer.BusyRecheck);
        Assert.Equal(["permission", "close"], _toasts.Shown.Select(t => t.Kind));
    }

    [Fact]
    public void ManyNames_AreShortened()
    {
        Checked(New("Example.Alpha"), New("Example.Beta"), New("Example.Delta"), New("Example.Gamma"), New("Example.Omega"));
        Assert.Equal(("5 updates ready", "Example Alpha, Example Beta, Example Delta and 2 more"), (_toasts.Shown[0].Title, _toasts.Shown[0].Body));
    }

    [Fact]
    public void NothingNew_IsNotAnnounced()
    {
        Checked(Seen(Check(AppStatus.Available)), Check(AppStatus.UpToDate, "Example.Paint", offer: null), Check(AppStatus.Skipped, "Example.Clock", skipped: "2.5.0"));
        Assert.Empty(_toasts.Shown);
    }

    [Fact]
    public void NoToast_WhileTheFlyoutIsOpen_AndWhatItShowedCountsAsSeen()
    {
        _flyoutOpen = true;
        Checked(New("Example.Editor"));
        _flyoutOpen = false;
        Checked(Check(AppStatus.Available));
        Pass(TimeSpan.FromMinutes(5));
        Assert.Empty(_toasts.Shown);
    }

    [Fact]
    public void NotificationsOff_ShowNothing()
    {
        Level(NotificationLevel.Off);
        Checked(New("Example.Editor"));
        Installed("Example.Editor", UpgradeResult.Updated);
        Pass(Announcer.BatchQuiet);
        Assert.Empty(_toasts.Shown);
    }

    // When I need to act: everything but what installed well (spec §4.7).
    [Fact]
    public void NeedsMe_LeavesOutWhatInstalled()
    {
        Level(NotificationLevel.NeedsMe);
        Checked(New("Example.Editor"));
        Installed("Example.Paint", UpgradeResult.Updated);
        Pass(Announcer.BatchQuiet);
        Assert.Equal(["ready"], _toasts.Shown.Select(t => t.Kind));
    }

    [Fact]
    public void NeedsMe_ShowsABatchWithAFailureOrARestart()
    {
        Level(NotificationLevel.NeedsMe);
        Installed("Example.Editor", UpgradeResult.Updated);
        Installed("Example.Paint", UpgradeResult.Failed);
        Pass(Announcer.BatchQuiet);
        Installed("Example.Clock", UpgradeResult.RestartNeeded);
        Pass(Announcer.BatchQuiet);
        Assert.Equal([("batch", "1 update installed, 1 failed"), ("batch", "1 update installed")], _toasts.Shown.Select(t => (t.Kind, t.Title)));
    }

    [Fact]
    public void OnlyFailures_ShowsOnlyABatchWithAFailure()
    {
        Level(NotificationLevel.Failures);
        Checked(New("Example.Editor"));
        Installed("Example.Paint", UpgradeResult.RestartNeeded);
        Pass(Announcer.BatchQuiet);
        Installed("Example.Clock", UpgradeResult.Failed);
        Pass(Announcer.BatchQuiet);
        Assert.Equal([("batch", "1 update failed")], _toasts.Shown.Select(t => (t.Kind, t.Title)));
    }

    // A batch that shows keeps its words, restart line and all.
    [Fact]
    public void OnlyFailures_KeepsTheBatchsWords()
    {
        Level(NotificationLevel.Failures);
        Installed("Example.Editor", UpgradeResult.RestartNeeded);
        Installed("Example.Paint", UpgradeResult.Updated);
        Installed("Example.Clock", UpgradeResult.Failed);
        Pass(Announcer.BatchQuiet);
        var toast = Assert.Single(_toasts.Shown);
        Assert.Equal(("2 updates installed, 1 failed", "Restart to finish updating Example Editor"), (toast.Title, toast.Body));
    }

    // What a level leaves out counts as seen, so a later level brings back nothing old.
    [Fact]
    public void NewsALevelLeftOut_IsSeen()
    {
        Level(NotificationLevel.Failures);
        Checked(New("Example.Editor"));
        Level(NotificationLevel.All);
        Checked(Check(AppStatus.Available));
        Assert.Empty(_toasts.Shown);
    }

    private void Level(NotificationLevel level) => _settings.Update(f => f with { Settings = f.Settings with { Notifications = level } });

    [Fact]
    public void ToastsWait_DuringAGame_AndShowOnceItEnds()
    {
        _conditions.State = _conditions.State with { FullScreen = true };
        Checked(New("Example.Editor"));
        Pass(Announcer.BusyRecheck);
        Assert.Empty(_toasts.Shown);
        _conditions.State = _conditions.State with { FullScreen = false };
        Pass(Announcer.BusyRecheck);
        Assert.Equal("1 update ready", Assert.Single(_toasts.Shown).Title);
        _time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(0, _ui.Pump());
    }

    [Fact]
    public void ToastsWait_WhileThePcIsLocked()
    {
        _conditions.Away = true;
        Checked(New("Example.Editor"));
        Assert.Empty(_toasts.Shown);
        _conditions.Away = false;
        Pass(Announcer.BusyRecheck);
        Assert.Single(_toasts.Shown);
    }

    [Fact]
    public void HeldNewsOfAnAppInstalledMeanwhile_IsDropped()
    {
        _conditions.State = _conditions.State with { FullScreen = true };
        Checked(New("Example.Editor"), New("Example.Paint"));
        _announcer.InstallChanged(Item(InstallStage.Waiting));
        _announcer.InstallChanged(Done(UpgradeResult.Updated));
        _conditions.State = _conditions.State with { FullScreen = false };
        Pass(Announcer.BusyRecheck);
        Assert.Equal([("1 update ready", "Example Paint"), ("1 update installed", "Example Editor")], _toasts.Shown.Select(t => (t.Title, t.Body)));
    }

    [Fact]
    public void FlyoutOpened_DropsWhatWaits()
    {
        _conditions.State = _conditions.State with { FullScreen = true };
        Checked(New("Example.Editor"));
        _announcer.FlyoutOpened();
        _conditions.State = _conditions.State with { FullScreen = false };
        Pass(Announcer.BusyRecheck);
        Assert.Empty(_toasts.Shown);
    }

    [Fact]
    public void BatchThatEnds_IsAnnounced_WithItsApps()
    {
        Installed("Example.Editor", UpgradeResult.Updated);
        Installed("Example.Paint", UpgradeResult.Failed);
        Pass(Announcer.BatchQuiet - TimeSpan.FromSeconds(1));
        Assert.Empty(_toasts.Shown);
        Pass(TimeSpan.FromSeconds(1));
        var toast = Assert.Single(_toasts.Shown);
        Assert.Equal(("batch", "1 update installed, 1 failed", "Example Editor and Example Paint"), (toast.Kind, toast.Title, toast.Body));
        Assert.Equal([("View", ToastAction.View)], toast.Buttons.Select(b => (b.Text, b.Action)));
    }

    [Theory]
    [InlineData(1, 0, "1 update installed")]
    [InlineData(3, 0, "3 updates installed")]
    [InlineData(0, 1, "1 update failed")]
    [InlineData(0, 2, "2 updates failed")]
    [InlineData(2, 1, "2 updates installed, 1 failed")]
    public void BatchTitle_CountsWhatHappened(int installed, int failed, string title)
    {
        for (var i = 0; i < installed; i++) Installed($"Example.Installed{i}", UpgradeResult.Updated);
        for (var i = 0; i < failed; i++) Installed($"Example.Failed{i}", UpgradeResult.Failed);
        Pass(Announcer.BatchQuiet);
        Assert.Equal(title, Assert.Single(_toasts.Shown).Title);
    }

    [Fact]
    public void InstallsOneAfterAnother_AreOneBatch()
    {
        Installed("Example.Editor", UpgradeResult.Updated);
        Pass(TimeSpan.FromSeconds(1));
        _announcer.InstallChanged(Item(InstallStage.Waiting, "Example.Paint"));
        Pass(Announcer.BatchQuiet);
        _announcer.InstallChanged(Done(UpgradeResult.Updated, "Example.Paint"));
        Pass(Announcer.BatchQuiet);
        Assert.Equal("2 updates installed", Assert.Single(_toasts.Shown).Title);
    }

    [Fact]
    public void RestartNeeded_IsSaidInTheBatchToast()
    {
        Installed("Example.Editor", UpgradeResult.RestartNeeded);
        Pass(Announcer.BatchQuiet);
        Assert.Equal(("1 update installed", "Restart to finish updating Example Editor"), (_toasts.Shown[0].Title, _toasts.Shown[0].Body));
        Installed("Example.Editor", UpgradeResult.RestartNeeded);
        Installed("Example.Paint", UpgradeResult.RestartNeeded);
        Pass(Announcer.BatchQuiet);
        Assert.Equal("Restart to finish updating 2 apps", _toasts.Shown[1].Body);
    }

    [Fact]
    public void CancelledAndDeclinedInstalls_AreNotAnnounced()
    {
        Installed("Example.Editor", UpgradeResult.Cancelled);
        Installed("Example.Paint", UpgradeResult.PermissionDeclined);
        Pass(Announcer.BatchQuiet);
        Assert.Empty(_toasts.Shown);
    }

    [Fact]
    public void BatchThatEndsWhileTheFlyoutIsOpen_IsNotAnnounced()
    {
        Installed("Example.Editor", UpgradeResult.Updated);
        _flyoutOpen = true;
        Pass(Announcer.BatchQuiet);
        _flyoutOpen = false;
        Pass(Announcer.BusyRecheck);
        Assert.Empty(_toasts.Shown);
    }

    [Fact]
    public void InstallsThatEndWhileTheFlyoutIsOpen_AreNotAnnouncedOnceItCloses()
    {
        _flyoutOpen = true;
        Installed("Example.Editor", UpgradeResult.Updated);
        _flyoutOpen = false;
        Pass(Announcer.BatchQuiet);
        Assert.Empty(_toasts.Shown);
    }

    [Fact]
    public void InstallsThatEndedBeforeTheFlyoutOpened_CountAsSeen()
    {
        Installed("Example.Editor", UpgradeResult.Updated);
        _flyoutOpen = true;
        _announcer.FlyoutOpened();
        _flyoutOpen = false;
        Pass(Announcer.BatchQuiet);
        Assert.Empty(_toasts.Shown);
    }

    [Fact]
    public void BatchThatEndsDuringAGame_ShowsOnceItEnds()
    {
        _conditions.State = _conditions.State with { FullScreen = true };
        Installed("Example.Editor", UpgradeResult.Updated);
        Pass(Announcer.BatchQuiet);
        Assert.Empty(_toasts.Shown);
        _conditions.State = _conditions.State with { FullScreen = false };
        Pass(Announcer.BusyRecheck);
        Assert.Equal("1 update installed", Assert.Single(_toasts.Shown).Title);
    }

    [Fact]
    public void NewerVersionFoundRightAfterAnInstall_IsAnnounced()
    {
        _announcer.InstallChanged(Item(InstallStage.Waiting));
        _announcer.InstallChanged(Done(UpgradeResult.Updated, after: Check(AppStatus.Available, installed: "2.5.0", offer: "2.6.0", newVersion: true)));
        Pass(Announcer.BatchQuiet);
        Assert.Equal([("ready", "1 update ready"), ("batch", "1 update installed")], _toasts.Shown.Select(t => (t.Kind, t.Title)));
    }

    [Fact]
    public void VersionJustInstalled_IsNotNews_EvenFromACheckThatStartedBefore()
    {
        Installed("Example.Editor", UpgradeResult.Updated);
        Checked(New("Example.Editor"));
        Pass(Announcer.BatchQuiet);
        Assert.Equal(["batch"], _toasts.Shown.Select(t => t.Kind));
    }

    [Fact]
    public void NewsHeldAtQuit_IsAnnouncedAfterTheNextStart()
    {
        _conditions.State = _conditions.State with { FullScreen = true };
        Checked(New("Example.Editor"));
        _announcer.Dispose();
        _conditions.State = _conditions.State with { FullScreen = false };
        using var restarted = Start();
        Checked(restarted, Check(AppStatus.Available));
        Assert.Equal("1 update ready", Assert.Single(_toasts.Shown).Title);
    }

    // Signing out in the demo disposes it before the folder goes: news that waited is neither announced nor saved later.
    [Fact]
    public async Task Disposed_NewsThatWaited_IsNeitherAnnouncedNorSaved()
    {
        _settings.Update(f => f with { Apps = [App()] });
        _conditions.State = _conditions.State with { FullScreen = true };
        Checked(New("Example.Editor"));
        _announcer.Dispose();
        _conditions.State = _conditions.State with { FullScreen = false };
        Pass(Announcer.BusyRecheck);
        await Saved();
        Assert.Empty(_toasts.Shown);
        Assert.False(_settings.Current.Apps.Single().Offer!.Announced);
    }

    [Fact]
    public async Task AnnouncedVersions_StayAnnouncedAfterTheNextStart()
    {
        _settings.Update(f => f with { Apps = [App()] });
        Checked(New("Example.Editor"));
        await Saved();
        Assert.True(_settings.Current.Apps.Single().Offer!.Announced);
        using var restarted = Start();
        Checked(restarted, new AppCheck(_settings.Current.Apps.Single(), AppStatus.Available, Package(), false));
        Assert.Single(_toasts.Shown);
    }

    [Fact]
    public async Task OpeningTheFlyout_MarksWhatItShowsAsAnnounced()
    {
        _settings.Update(f => f with { Apps = [App(), App("Example.Paint", auto: true)] });
        _announcer.FlyoutOpened();
        await Saved();
        Assert.All(_settings.Current.Apps, a => Assert.True(a.Offer!.Announced));
    }

    [Fact]
    public void VersionInstalledEarlier_IsNewsAgain_OnceItComesBack()
    {
        Installed("Example.Editor", UpgradeResult.Updated);
        Pass(Announcer.BatchQuiet);
        Checked(Check(AppStatus.UpToDate, installed: "2.5.0", offer: null));
        Checked(New("Example.Editor"));
        Assert.Equal(["batch", "ready"], _toasts.Shown.Select(t => t.Kind));
    }

    [Fact]
    public void OpeningTheFlyout_WithdrawsShownToasts()
    {
        Checked(New("Example.Editor"));
        _announcer.FlyoutOpened();
        Assert.Equal(["ready", "permission", "close", "batch", "self", "selfUpdated", "selfFailed"], _toasts.Hidden);
    }

    private sealed class FakeToasts : IToasts
    {
        public List<Toast> Shown { get; } = [];
        public List<string> Hidden { get; } = [];

        public void Show(Toast toast) => Shown.Add(toast);

        public void Hide(string kind) => Hidden.Add(kind);
    }
}
