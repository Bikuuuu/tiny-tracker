using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using TinyTracker.Core.Checking;
using TinyTracker.Core.Installing;
using TinyTracker.Core.Logging;
using TinyTracker.Core.Scheduling;
using TinyTracker.Core.SelfUpdate;
using TinyTracker.Core.Storage;
using TinyTracker.Core.Tracking;
using TinyTracker.Presentation.History;
using TinyTracker.Presentation.Settings;
using TinyTracker.Presentation.Shell;
using TinyTracker.Presentation.Updates;
using Xunit;
using static TinyTracker.Presentation.Tests.Fixtures;

namespace TinyTracker.Presentation.Tests.Updates;

// Tiny Tracker's own row on the Updates page (spec §4.3, §6.5).
public sealed class UpdatesSelfUpdateTests : IAsyncDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly SelfRelease Release = new(new SelfVersion(0, 2, 0), Now.AddDays(-10));

    private readonly TempFolder _folder = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly TestUi _ui = new();
    private readonly FakeInstaller _installer = new();
    private readonly FakeConditions _conditions = new();
    private readonly FakeSelfUpdate _self = new();
    private readonly List<string> _opened = [];
    private readonly CheckScheduler _scheduler;
    private readonly SettingsStore _settings;
    private readonly HistoryStore _history;
    private readonly FileLog _log;
    private readonly SettingsWriter _writer;
    private readonly HistoryWriter _historyWriter;
    private readonly UpdatesViewModel _vm;

    public UpdatesSelfUpdateTests()
    {
        _scheduler = new CheckScheduler(_time, TimeSpan.FromHours(6));
        _scheduler.CheckDue += (_, ticket) => _scheduler.Finished(ticket, succeeded: true);
        _settings = new SettingsStore(_folder.PathOf("settings.json"));
        _settings.Update(f => f with { Apps = [App("Example.Editor")] });
        _history = new HistoryStore(_folder.PathOf("history.json"), _time);
        _log = new FileLog(_folder.PathOf("app.log"), _time);
        _writer = new SettingsWriter(_settings, _log, _ui.Post);
        _historyWriter = new HistoryWriter(_history, _log, _ui.Post);
        _vm = new UpdatesViewModel(_scheduler, _installer, _conditions, _settings, _writer, _history, _historyWriter, _time, _ui.Post, _opened.Add, _self,
            () => CultureInfo.InvariantCulture);
    }

    public async ValueTask DisposeAsync()
    {
        await Task.WhenAll(_writer.Idle, _historyWriter.Idle).WaitAsync(Wait);
        _vm.Dispose();
        _scheduler.Dispose();
        _log.Close();
        _folder.Dispose();
    }

    private void Show(params AppCheck[] apps) => _vm.CheckFinished(new CheckCompleted(new CheckTicket(1, CheckTrigger.Manual), _time.GetUtcNow(), apps, CheckProblem.None));

    private void Self(SelfUpdateState state)
    {
        _self.State = state;
        _vm.SelfUpdateChanged();
    }

    private void SilentMode() => _settings.Update(f => f with { Settings = f.Settings with { SilentMode = true } });

    private void Attempted() => _settings.Update(f => f with { SelfUpdate = f.SelfUpdate with { AttemptedVersion = "0.2.0", AttemptedAt = _time.GetUtcNow() } });

    private static SelfUpdateState Failed(bool automatic, UpgradeFailure failure) =>
        new(SelfUpdateStage.Failed, Release) { Outcome = new UpgradeOutcome(UpgradeResult.Failed, failure), Automatic = automatic };

    [Fact]
    public void NothingNewer_HasNoRow() => Assert.False(_vm.HasSelfUpdate);

    [Fact]
    public void NewerVersion_ShowsItsRow_UnderUpdates()
    {
        Self(new SelfUpdateState(SelfUpdateStage.Available, Release));
        Assert.True(_vm.HasSelfUpdate && _vm.HasUpdates);
        Assert.Equal("Tiny Tracker 0.2.0", _vm.SelfRow.View.Title);
        Self(new SelfUpdateState(SelfUpdateStage.None, null));
        Assert.False(_vm.HasSelfUpdate);
    }

    [Theory]
    [InlineData(SelfUpdateStage.Available)]
    [InlineData(SelfUpdateStage.Failed)]
    public void UpdateAndRetry_AskForIt(SelfUpdateStage stage)
    {
        Self(new SelfUpdateState(stage, Release) { Outcome = stage == SelfUpdateStage.Failed ? new UpgradeOutcome(UpgradeResult.Failed, UpgradeFailure.DownloadFailed) : null });
        _vm.SelfRow.PrimaryCommand.Execute(null);
        Assert.Equal([false], _self.Updates);
    }

    [Fact]
    public void Cancel_AsksToStopIt()
    {
        Self(new SelfUpdateState(SelfUpdateStage.Downloading, Release));
        _vm.SelfRow.CancelCommand.Execute(null);
        Assert.Equal(1, _self.Cancels);
    }

    [Fact]
    public void WhatsNew_OpensTheReleasePage()
    {
        Self(new SelfUpdateState(SelfUpdateStage.Available, Release));
        _vm.SelfRow.OpenNotesCommand.Execute(null);
        Assert.Equal(["https://github.com/Bikuuuu/tiny-tracker/releases/tag/v0.2.0"], _opened);
    }

    // It isn't one of the apps Update all installs (spec §4.3), but it's an update that's ready.
    [Fact]
    public void UpdateAll_LeavesTinyTrackerOut_ButTheSummaryCountsIt()
    {
        Show(Check(AppStatus.Available));
        Self(new SelfUpdateState(SelfUpdateStage.Available, Release));
        Assert.Equal("Update all (1)", _vm.UpdateAllText);
        Assert.StartsWith("2 updates ready", _vm.Summary);
        _vm.UpdateAllCommand.Execute(null);
        Assert.Equal(["Example.Editor"], _installer.Enqueued.Select(r => r.Package.Id));
        Assert.Empty(_self.Updates);
    }

    [Fact]
    public void TinyTrackerAlone_BadgesTheTrayIcon_ButOffersNoUpdateAll()
    {
        Show(Check(AppStatus.UpToDate, offer: null));
        Self(new SelfUpdateState(SelfUpdateStage.Available, Release));
        Assert.Equal(new TrayState(TrayIconKind.Badge, "Tiny Tracker: 1 update ready"), _vm.Tray);
        Assert.False(_vm.CanUpdateAll);
    }

    [Fact]
    public void Downloading_IsWork_AndTheTraySaysSo()
    {
        Self(new SelfUpdateState(SelfUpdateStage.Downloading, Release) { Progress = Downloading(45 * MB, 100 * MB) });
        Assert.True(_vm.IsWorking);
        Assert.Equal(new TrayState(TrayIconKind.Working, "Tiny Tracker: Installing Tiny Tracker (45%)"), _vm.Tray);
    }

    // With silent mode on it installs by itself once the wait is over, and meanwhile says so.
    [Fact]
    public void UpdateThatInstallsByItself_SaysForHowLongItWaits()
    {
        _settings.Update(f => f with { Settings = f.Settings with { SilentMode = true, AutoInstallWaitDays = 3 } });
        Self(new SelfUpdateState(SelfUpdateStage.Available, Release with { PublishedAt = Now.AddDays(-1) }));
        Assert.Equal("0.1.0 → 0.2.0 · Installs automatically in 2 days", _vm.SelfRow.View.Status);
    }

    [Fact]
    public void WithoutSilentMode_ItJustOffersUpdate()
    {
        _settings.Update(f => f with { Settings = f.Settings with { AutoInstallWaitDays = 3 } });
        Self(new SelfUpdateState(SelfUpdateStage.Available, Release with { PublishedAt = Now.AddDays(-1) }));
        Assert.Equal("0.1.0 → 0.2.0", _vm.SelfRow.View.Status);
    }

    // History's entries of Tiny Tracker itself get its icon, from its uninstall entry (spec §4.6).
    [Fact]
    public void HistoryOfTinyTracker_ShowsItsIcon() => Assert.Equal(@"ARP\Machine\X64\TinyTracker_is1", _vm.LocalIdOf(SelfUpdater.Key));

    // Its failed entry's Retry does what the row's Retry does, while the row offers that version.
    [Fact]
    public void HistoryRetryOfTinyTracker_IsTheRowsRetry()
    {
        Self(new SelfUpdateState(SelfUpdateStage.Failed, Release) { Outcome = new UpgradeOutcome(UpgradeResult.Failed, UpgradeFailure.DigestMismatch) });
        Assert.True(_vm.CanRetry(SelfUpdater.Key, "0.2.0"));
        Assert.False(_vm.CanRetry(SelfUpdater.Key, "0.1.9"));
        Assert.True(_vm.Retry(SelfUpdater.Key, "0.2.0"));
        Assert.Equal([false], _self.Updates);
    }

    [Fact]
    public void HistoryRetryOfTinyTracker_WhileItRuns_IsntOffered()
    {
        Self(new SelfUpdateState(SelfUpdateStage.Downloading, Release));
        Assert.False(_vm.CanRetry(SelfUpdater.Key, "0.2.0"));
        Assert.False(_vm.Retry(SelfUpdater.Key, "0.2.0"));
        Assert.Empty(_self.Updates);
    }

    // With the switch and silent mode on it installs by itself, at a moment when nothing else runs (spec §6.5).
    [Fact]
    public void AllClear_ItInstallsByItself_Once()
    {
        SilentMode();
        Self(new SelfUpdateState(SelfUpdateStage.Available, Release));
        Assert.Equal([true], _self.Updates);
        _vm.ConditionsChanged();
        Assert.Equal([true], _self.Updates);
    }

    // A game holds it back; once the game ends it goes within a minute, as an Auto app does (spec §6.2).
    [Fact]
    public void AfterAGame_ItInstallsByItself_WithinAMinute()
    {
        SilentMode();
        _conditions.State = _conditions.State with { FullScreen = true };
        Self(new SelfUpdateState(SelfUpdateStage.Available, Release));
        Assert.Empty(_self.Updates);
        _conditions.State = _conditions.State with { FullScreen = false };
        _time.Advance(UpdatesViewModel.FullScreenRecheck);
        _ui.Pump();
        Assert.Equal([true], _self.Updates);
    }

    // The install window holds it too, and it goes once the window opens (spec §6.5); now is 08:00.
    [Fact]
    public void OutsideTheInstallWindow_ItWaits_ThenInstallsByItself()
    {
        SilentMode();
        _settings.Update(f => f with { Settings = f.Settings with { InstallWindowEnabled = true, InstallWindowFrom = 9, InstallWindowTo = 17 } });
        Self(new SelfUpdateState(SelfUpdateStage.Available, Release));
        Assert.Equal("0.1.0 → 0.2.0 · Installs automatically at 09:00", _vm.SelfRow.View.Status);
        _time.Advance(TimeSpan.FromHours(1));
        _ui.Pump();
        Assert.Equal([true], _self.Updates);
    }

    [Fact]
    public void WhatsNewLinksOff_HideTheRowsLink()
    {
        _settings.Update(f => f with { Settings = f.Settings with { ShowWhatsNew = false } });
        Self(new SelfUpdateState(SelfUpdateStage.Available, Release));
        Assert.False(_vm.SelfRow.View.ShowNotes);
    }

    [Fact]
    public void WithoutSilentMode_NeverByItself()
    {
        Self(new SelfUpdateState(SelfUpdateStage.Available, Release));
        Assert.Empty(_self.Updates);
    }

    [Fact]
    public void SwitchOff_NeverByItself()
    {
        _settings.Update(f => f with { Settings = f.Settings with { SilentMode = true, AutoSelfUpdate = false } });
        Self(new SelfUpdateState(SelfUpdateStage.Available, Release));
        Assert.Empty(_self.Updates);
    }

    [Fact]
    public void NotWhileACheckRuns()
    {
        SilentMode();
        _vm.CheckStarted();
        Self(new SelfUpdateState(SelfUpdateStage.Available, Release));
        Assert.Empty(_self.Updates);
        Show();
        Assert.Equal([true], _self.Updates);
    }

    [Fact]
    public void NotWhileAnAppInstalls()
    {
        SilentMode();
        Show(Check(AppStatus.Available));
        _vm.InstallChanged(Item(InstallStage.Downloading));
        Self(new SelfUpdateState(SelfUpdateStage.Available, Release));
        Assert.Empty(_self.Updates);
        _vm.InstallChanged(Done(UpgradeResult.Updated));
        Assert.Equal([true], _self.Updates);
    }

    // The app it restarts goes on with the Auto apps, so it goes first, and they wait meanwhile.
    [Fact]
    public void ItGoesBeforeTheAutoApps_WhichWaitMeanwhile()
    {
        SilentMode();
        _settings.Update(f => f with { Apps = [App("Example.Editor", auto: true)] });
        Self(new SelfUpdateState(SelfUpdateStage.Available, Release));
        Show(Check(AppStatus.Available, auto: true, scope: InstallScope.User));
        Assert.Equal([true], _self.Updates);
        Assert.Empty(_installer.Enqueued);
    }

    // An automatic update that failed for a reason that can pass is tried again once its 12 hours are over (spec §6.2).
    [Fact]
    public void AutomaticFailureThatCanPass_IsTriedAgain_After12Hours()
    {
        SilentMode();
        Attempted();
        Self(Failed(automatic: true, UpgradeFailure.GitHubUnreachable));
        _time.Advance(TimeSpan.FromHours(11));
        _vm.ConditionsChanged();
        Assert.Empty(_self.Updates);
        _time.Advance(TimeSpan.FromHours(1));
        _vm.ConditionsChanged();
        Assert.Equal([true], _self.Updates);
    }

    // Other failures, and failed updates the user started, wait for the user.
    [Theory]
    [InlineData(false, UpgradeFailure.GitHubUnreachable)]
    [InlineData(true, UpgradeFailure.DigestMismatch)]
    [InlineData(true, UpgradeFailure.InstallerFailed)]
    [InlineData(true, UpgradeFailure.OtherAccounts)]
    public void OtherFailures_WaitForTheUser(bool automatic, UpgradeFailure failure)
    {
        SilentMode();
        Attempted();
        Self(Failed(automatic, failure));
        _time.Advance(TimeSpan.FromHours(13));
        _vm.ConditionsChanged();
        Assert.Empty(_self.Updates);
    }

    // After a restart the row offers Update again, and a failure that waited for the user still does, until a newer version (spec §6.5).
    [Fact]
    public void KeptFailure_WaitsForTheUser_AfterARestart()
    {
        SilentMode();
        _settings.Update(f => f with { SelfUpdate = f.SelfUpdate with { FailedVersion = "0.2.0" } });
        Self(new SelfUpdateState(SelfUpdateStage.Available, Release));
        Assert.Empty(_self.Updates);
        Assert.Equal("0.1.0 → 0.2.0", _vm.SelfRow.View.Status);
        Self(new SelfUpdateState(SelfUpdateStage.Available, Release with { Version = new SelfVersion(0, 3, 0) }));
        Assert.Equal([true], _self.Updates);
    }

    // Records what the page asks of Tiny Tracker's own update, which moves on as the real one does.
    private sealed class FakeSelfUpdate : ISelfUpdate
    {
        public SelfVersion Running { get; set; } = new(0, 1, 0);
        public SelfUpdateState State { get; set; } = new(SelfUpdateStage.None, null);
        public List<bool> Updates { get; } = [];
        public int Cancels { get; private set; }

        public void Update(bool automatic)
        {
            Updates.Add(automatic);
            State = State with { Stage = SelfUpdateStage.WaitsForOthers };
        }

        public void Cancel() => Cancels++;
    }
}
