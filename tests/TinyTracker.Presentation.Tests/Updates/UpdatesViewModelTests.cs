using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using TinyTracker.Core.Checking;
using TinyTracker.Core.History;
using TinyTracker.Core.Installing;
using TinyTracker.Core.Logging;
using TinyTracker.Core.Scheduling;
using TinyTracker.Core.Storage;
using TinyTracker.Core.Tracking;
using TinyTracker.Presentation.History;
using TinyTracker.Presentation.Settings;
using TinyTracker.Presentation.Shell;
using TinyTracker.Presentation.Updates;
using Xunit;
using static TinyTracker.Presentation.Tests.Fixtures;

namespace TinyTracker.Presentation.Tests.Updates;

public sealed class UpdatesViewModelTests : IAsyncDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly TempFolder _folder = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly TestUi _ui = new();
    private readonly FakeInstaller _installer = new();
    private readonly FakeConditions _conditions = new();
    private readonly List<string> _opened = [];
    private readonly CheckScheduler _scheduler;
    private readonly SettingsStore _settings;
    private readonly HistoryStore _history;
    private readonly FileLog _log;
    private readonly SettingsWriter _writer;
    private readonly HistoryWriter _historyWriter;
    private readonly UpdatesViewModel _vm;
    private int _checksDue;
    private bool _checksEndAtOnce = true;

    public UpdatesViewModelTests()
    {
        _scheduler = new CheckScheduler(_time, TimeSpan.FromHours(6));
        // Like a runner whose checks end at once, so the scheduler doesn't hold Auto installs back.
        _scheduler.CheckDue += (_, ticket) =>
        {
            _checksDue++;
            if (_checksEndAtOnce) _scheduler.Finished(ticket, succeeded: true);
        };
        _settings = new SettingsStore(_folder.PathOf("settings.json"));
        _settings.Update(f => f with { Apps = [App("Example.Editor"), App("Example.Paint"), App("Example.Viewer", offer: null)] });
        _history = new HistoryStore(_folder.PathOf("history.json"), _time);
        _log = new FileLog(_folder.PathOf("app.log"), _time);
        _writer = new SettingsWriter(_settings, _log, _ui.Post);
        _historyWriter = new HistoryWriter(_history, _log, _ui.Post);
        _vm = new UpdatesViewModel(_scheduler, _installer, _conditions, _settings, _writer, _history, _historyWriter, _time, _ui.Post, _opened.Add,
            culture: () => CultureInfo.InvariantCulture);
    }

    // Saves a test left queued land, and later log lines are dropped, before the folder goes.
    public async ValueTask DisposeAsync()
    {
        await Task.WhenAll(_writer.Idle, _historyWriter.Idle).WaitAsync(Wait);
        _vm.Dispose();
        _scheduler.Dispose();
        _log.Close();
        _folder.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private CheckCompleted Checked(params AppCheck[] apps) => new(new CheckTicket(1, CheckTrigger.Manual), _time.GetUtcNow(), apps, CheckProblem.None);

    private CheckCompleted Failed(CheckProblem problem) => new(new CheckTicket(1, CheckTrigger.Manual), _time.GetUtcNow(), [], problem, "winget call failed (0x800706BA)");

    private void Show(params AppCheck[] apps) => _vm.CheckFinished(Checked(apps));

    private UpdateRow Row(string name) => _vm.Updates.Concat(_vm.UpToDate).Single(r => r.Name == name);

    private async Task Saved()
    {
        await _writer.Idle.WaitAsync(Wait, Ct);
        _ui.Pump();
    }

    private void Pass(TimeSpan time)
    {
        _time.Advance(time);
        _ui.Pump();
    }

    private void TrackAuto(params string[] ids) => _settings.Update(f => f with { Apps = [.. f.Apps.Select(a => ids.Contains(a.Id) ? a with { AutoChoice = true } : a)] });

    // Update apps automatically (spec §4.5).
    private void SwitchOn() => _settings.Update(f => f with { Settings = f.Settings with { AutoUpdateApps = true } });

    private IEnumerable<string> Enqueued => _installer.Enqueued.Select(r => r.Package.Id);

    [Fact]
    public void NoTrackedApps_ShowsTheEmptyState()
    {
        _settings.Update(f => f with { Apps = [] });
        _vm.TrackedAppsChanged(added: false);
        Assert.True(_vm.IsEmpty);
        Assert.Equal("", _vm.Summary);
        Assert.Equal("Tiny Tracker: No apps chosen", _vm.Tray.Tooltip);
    }

    [Fact]
    public void BeforeTheFirstCheck_NothingIsListed()
    {
        Assert.False(_vm.IsEmpty);
        Assert.Equal("Not checked yet", _vm.Summary);
        Assert.Equal(new TrayState(TrayIconKind.Idle, "Tiny Tracker: Not checked yet"), _vm.Tray);
        _vm.CheckStarted();
        Assert.Equal(("Checking for updates…", "Checking for updates…"), (_vm.Summary, _vm.NextCheck));
        Assert.Empty(_vm.Updates);
        Assert.Empty(_vm.UpToDate);
    }

    [Fact]
    public void CheckResults_FillBothGroups_InOrder()
    {
        _settings.Update(f => f with { Apps = [.. f.Apps, App("Example.Clock"), App("Example.Legacy"), App("Example.Launcher", offer: null)] });
        Show(
            Check(AppStatus.Available),
            Check(AppStatus.UpToDate, "Example.Viewer", offer: null),
            Check(AppStatus.Skipped, "Example.Clock", skipped: "2.5.0"),
            Check(AppStatus.NotFound, "Example.Legacy", offer: null),
            Check(AppStatus.VersionUnknown, "Example.Launcher", installed: "Unknown", offer: null));
        Assert.Equal(["Example Legacy", "Example Editor"], _vm.Updates.Select(r => r.Name));
        Assert.Equal(["Example Clock", "Example Launcher", "Example Viewer"], _vm.UpToDate.Select(r => r.Name));
        Assert.Equal("3 apps are up to date", _vm.UpToDateText);
        Assert.False(_vm.IsUpToDateExpanded);
    }

    [Fact]
    public void Summary_CountsUpdates_AndSaysWhenChecked()
    {
        _vm.Shown();
        Show(Check(AppStatus.Available), Check(AppStatus.Available, "Example.Paint"));
        Assert.Equal("2 updates ready · checked just now", _vm.Summary);
        Pass(TimeSpan.FromMinutes(2));
        Assert.Equal("2 updates ready · checked 2 min ago", _vm.Summary);
    }

    [Fact]
    public void NothingToUpdate_SaysUpToDate_AndOpensTheGroup()
    {
        Show(Check(AppStatus.UpToDate, "Example.Viewer", offer: null));
        Assert.Equal("Up to date · checked just now", _vm.Summary);
        Assert.True(_vm.IsUpToDateExpanded);
        _vm.ToggleUpToDateCommand.Execute(null);
        Show(Check(AppStatus.UpToDate, "Example.Viewer", offer: null));
        Assert.False(_vm.IsUpToDateExpanded);
    }

    [Fact]
    public void Footer_ShowsTheNextCheck_OrWhyItWaits()
    {
        Assert.Equal("Next check in 1 min", _vm.NextCheck);
        _scheduler.SetConditions(online: false, batterySaver: true);
        _vm.ConditionsChanged();
        Assert.Equal("Next check waits for the network", _vm.NextCheck);
        _scheduler.SetConditions(online: true, batterySaver: true);
        _vm.ConditionsChanged();
        Assert.Equal("Next check waits until Energy saver is off", _vm.NextCheck);
    }

    [Fact]
    public void Times_RefreshOnlyWhileOpen()
    {
        Show(Check(AppStatus.Available));
        _time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(0, _ui.Pump());
        _vm.Shown();
        Assert.Equal("1 update ready · checked 2 min ago", _vm.Summary);
        Pass(TimeSpan.FromMinutes(1));
        Assert.Equal("1 update ready · checked 3 min ago", _vm.Summary);
        _vm.Hidden();
        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(0, _ui.Pump());
    }

    [Fact]
    public void OpeningTheFlyout_AsksForAStaleCheck_ShowingThePageDoesNot()
    {
        _vm.Shown();
        Assert.Equal(0, _checksDue);
        _vm.FlyoutOpened();
        Assert.Equal(1, _checksDue);
    }

    [Fact]
    public void RefreshIcon_TurnsOnlyWhileTheFlyoutShowsACheck()
    {
        _vm.CheckStarted();
        Assert.False(_vm.IsSpinning);
        _vm.Shown();
        Assert.True(_vm.IsSpinning);
        _vm.Hidden();
        Assert.False(_vm.IsSpinning);
        _vm.Shown();
        Show(Check(AppStatus.Available));
        Assert.False(_vm.IsSpinning);
    }

    [Fact]
    public void UpdateAll_QueuesAvailableAndFailedRowsOnly()
    {
        _settings.Update(f => f with { Apps = [.. f.Apps, App("Example.Notes", phantom: true), App("Example.Clock", skipped: "2.5.0")] });
        Show(
            Check(AppStatus.Available),
            Check(AppStatus.Available, "Example.Paint"),
            Check(AppStatus.UpToDate, "Example.Viewer", offer: null),
            Check(AppStatus.Phantom, "Example.Notes"),
            Check(AppStatus.Skipped, "Example.Clock", skipped: "2.5.0"));
        _vm.InstallChanged(Done(UpgradeResult.Failed, "Example.Paint", UpgradeFailure.DiskFull));
        Assert.Equal(("Update all (2)", true), (_vm.UpdateAllText, _vm.CanUpdateAll));
        _vm.UpdateAllCommand.Execute(null);
        Assert.Equal(["Example.Editor", "Example.Paint"], _installer.Enqueued.Select(r => r.Package.Id).Order());
    }

    [Fact]
    public void Update_QueuesTheOfferedVersion()
    {
        Show(Check(AppStatus.Available));
        Row("Example Editor").PrimaryCommand.Execute(null);
        // Installed for all users, so through the helper.
        Assert.Equal(Request() with { Route = InstallRoute.Helper, LocalId = @"ARP\Machine\X64\Example Editor" }, Assert.Single(_installer.Enqueued));
    }

    [Fact]
    public void RowAtWork_MovesToTheTop()
    {
        Show(Check(AppStatus.Available), Check(AppStatus.Available, "Example.Paint"));
        Assert.Equal(["Example Editor", "Example Paint"], _vm.Updates.Select(r => r.Name));
        _vm.InstallChanged(Item(InstallStage.Downloading, "Example.Paint", Downloading(180 * MB, 400 * MB), 20 * MB));
        Assert.Equal(["Example Paint", "Example Editor"], _vm.Updates.Select(r => r.Name));
        Assert.Equal($"{Nb("180 of 400 MB")} · {Nb("20 MB/s")}", _vm.Updates[0].View.Status);
    }

    [Fact]
    public void Updated_ShowsForAMoment_ThenJoinsUpToDate()
    {
        Show(Check(AppStatus.Available));
        _vm.InstallChanged(Done(UpgradeResult.Updated, after: Check(AppStatus.UpToDate, installed: "2.5.0", offer: null)));
        Assert.Equal(RowState.Updated, Row("Example Editor").View.State);
        Assert.Contains(Row("Example Editor"), _vm.Updates);
        Pass(UpdatesViewModel.UpdatedShownFor);
        Assert.Equal(RowState.UpToDate, Row("Example Editor").View.State);
        Assert.Contains(Row("Example Editor"), _vm.UpToDate);
    }

    [Fact]
    public void Failure_StaysUntilTheOfferChanges()
    {
        Show(Check(AppStatus.Available));
        _vm.InstallChanged(Done(UpgradeResult.Failed, failure: UpgradeFailure.DiskFull));
        Show(Check(AppStatus.Available));
        Assert.Equal(RowState.Failed, Row("Example Editor").View.State);
        Show(Check(AppStatus.Available, offer: "2.6.0"));
        Assert.Equal((RowState.Available, "6.0"), (Row("Example Editor").View.State, Row("Example Editor").View.To.Changed));
    }

    [Fact]
    public void Cancelled_ReturnsTheRowToAvailable()
    {
        Show(Check(AppStatus.Available));
        _vm.InstallChanged(Item(InstallStage.Waiting));
        Row("Example Editor").CancelCommand.Execute(null);
        Assert.Equal(new PackageKey("Example.Editor", "winget"), Assert.Single(_installer.Cancelled));
        _vm.InstallChanged(Done(UpgradeResult.Cancelled));
        Assert.Equal(RowState.Available, Row("Example Editor").View.State);
    }

    [Fact]
    public void InstallOfAnUntrackedApp_IsIgnored()
    {
        Show(Check(AppStatus.Available));
        _vm.InstallChanged(Item(InstallStage.Downloading, "Example.Other"));
        Assert.Equal(["Example Editor"], _vm.Updates.Select(r => r.Name));
    }

    [Fact]
    public async Task StopTracking_ShowsUndo_ThenRemovesTheRow()
    {
        Show(Check(AppStatus.Available));
        var row = Row("Example Editor");
        row.StopTrackingCommand.Execute(null);
        Assert.True(row.IsRemoved);
        await Saved();
        Assert.DoesNotContain(_settings.Current.Apps, a => a.Id == "Example.Editor");
        Assert.Contains(row, _vm.Updates);
        Pass(UpdatesViewModel.UndoShownFor);
        Assert.DoesNotContain(row, _vm.Updates);
    }

    [Fact]
    public async Task Undo_PutsTheAppBack()
    {
        _settings.Update(f => f with { Apps = [App("Example.Editor", auto: true)] });
        Show(Check(AppStatus.Available, auto: true));
        var row = Row("Example Editor");
        row.StopTrackingCommand.Execute(null);
        await Saved();
        row.UndoCommand.Execute(null);
        await Saved();
        Pass(UpdatesViewModel.UndoShownFor);
        Assert.False(row.IsRemoved);
        Assert.Contains(row, _vm.Updates);
        Assert.True(Assert.Single(_settings.Current.Apps).AutoChoice);
    }

    [Fact]
    public async Task Undo_WhileTheRowFadesOut_PutsItBack()
    {
        Show(Check(AppStatus.Available));
        var row = Row("Example Editor");
        row.StopTrackingCommand.Execute(null);
        await Saved();
        Pass(UpdatesViewModel.UndoShownFor);
        Assert.DoesNotContain(row, _vm.Updates);
        row.UndoCommand.Execute(null);
        await Saved();
        Assert.Contains(row, _vm.Updates);
        Assert.False(row.IsRemoved);
        Assert.Single(_settings.Current.Apps, a => a.Id == "Example.Editor");
    }

    [Fact]
    public async Task StopTrackingRefusedAfterTheRowLeft_PutsTheRowBack()
    {
        Show(Check(AppStatus.Available));
        var row = Row("Example Editor");
        using var gate = new ManualResetEventSlim();
        try
        {
            _writer.Update(file =>
            {
                gate.Wait(Wait);
                return file;
            });
            row.StopTrackingCommand.Execute(null);
            Pass(UpdatesViewModel.UndoShownFor);
            Assert.DoesNotContain(row, _vm.Updates);
            File.Delete(_folder.PathOf("settings.json"));
            Directory.CreateDirectory(_folder.PathOf("settings.json"));
        }
        finally
        {
            gate.Set();
        }
        await Saved();
        Assert.Contains(row, _vm.Updates);
        Assert.False(row.IsRemoved);
        var notice = Assert.Single(_vm.Notices);
        Assert.Equal((NoticeKind.SaveFailed, "Code: 0x80070005"), (notice.Kind, notice.Details));
    }

    [Fact]
    public void RemovedRow_StillFollowsItsInstall_AndIsntWork()
    {
        Show(Check(AppStatus.Available));
        _vm.InstallChanged(Item(InstallStage.Waiting));
        var row = Row("Example Editor");
        row.StopTrackingCommand.Execute(null);
        Assert.False(_vm.IsWorking);
        _vm.InstallChanged(Done(UpgradeResult.Cancelled));
        row.UndoCommand.Execute(null);
        Assert.Equal(RowState.Available, row.View.State);
        Assert.False(_vm.IsWorking);
    }

    [Fact]
    public void RemovedRowThatFinishesUpdating_StillLeavesAfterItsFiveSeconds()
    {
        Show(Check(AppStatus.Available));
        _vm.InstallChanged(Item(InstallStage.Installing));
        var row = Row("Example Editor");
        row.StopTrackingCommand.Execute(null);
        _vm.InstallChanged(Done(UpgradeResult.Updated, after: Check(AppStatus.UpToDate, installed: "2.5.0", offer: null)));
        Pass(UpdatesViewModel.UndoShownFor);
        Assert.DoesNotContain(row, _vm.Updates.Concat(_vm.UpToDate));
    }

    [Fact]
    public void UndoOfARowThatUpdatedMeanwhile_ShowsUpdated_ThenJoinsUpToDate()
    {
        Show(Check(AppStatus.Available));
        _vm.InstallChanged(Item(InstallStage.Installing));
        var row = Row("Example Editor");
        row.StopTrackingCommand.Execute(null);
        _vm.InstallChanged(Done(UpgradeResult.Updated, after: Check(AppStatus.UpToDate, installed: "2.5.0", offer: null)));
        row.UndoCommand.Execute(null);
        Assert.Equal(RowState.Updated, row.View.State);
        Pass(UpdatesViewModel.UpdatedShownFor);
        Assert.Contains(row, _vm.UpToDate);
    }

    [Fact]
    public void StopTracking_CancelsAWaitingInstall()
    {
        Show(Check(AppStatus.Available));
        _vm.InstallChanged(Item(InstallStage.Waiting));
        Row("Example Editor").StopTrackingCommand.Execute(null);
        Assert.Single(_installer.Cancelled);
    }

    [Fact]
    public void SkippingAFailedUpdate_MovesItToUpToDate_AndOutOfUpdateAll()
    {
        Show(Check(AppStatus.Available));
        _vm.InstallChanged(Done(UpgradeResult.Failed, failure: UpgradeFailure.DiskFull));
        Assert.True(_vm.CanUpdateAll);
        Row("Example Editor").SkipCommand.Execute(null);
        Assert.Equal(RowState.Skipped, Row("Example Editor").View.State);
        Assert.Contains(Row("Example Editor"), _vm.UpToDate);
        Assert.False(_vm.CanUpdateAll);
    }

    [Fact]
    public async Task Skip_MovesTheRowToUpToDate_AndUndoBringsItBack()
    {
        Show(Check(AppStatus.Available));
        Row("Example Editor").SkipCommand.Execute(null);
        Assert.Equal(RowState.Skipped, Row("Example Editor").View.State);
        Assert.Contains(Row("Example Editor"), _vm.UpToDate);
        await Saved();
        Assert.Equal("2.5.0", _settings.Current.Apps.Single(a => a.Id == "Example.Editor").SkippedVersion);
        await _historyWriter.Idle.WaitAsync(Wait, Ct);
        Assert.Equal((HistoryResult.Skipped, "2.4.1", "2.5.0"), (_history.Entries[0].Result, _history.Entries[0].FromVersion, _history.Entries[0].ToVersion));

        Row("Example Editor").UndoSkipCommand.Execute(null);
        await Saved();
        Assert.Equal(RowState.Available, Row("Example Editor").View.State);
        Assert.Null(_settings.Current.Apps.Single(a => a.Id == "Example.Editor").SkippedVersion);
    }

    [Fact]
    public async Task UndoneSkip_LeavesNoHistory()
    {
        Show(Check(AppStatus.Available));
        Row("Example Editor").SkipCommand.Execute(null);
        await Saved();
        await _historyWriter.Idle.WaitAsync(Wait, Ct);
        Assert.Equal(HistoryResult.Skipped, Assert.Single(_history.Entries).Result);

        Row("Example Editor").UndoSkipCommand.Execute(null);
        await Saved();
        await _historyWriter.Idle.WaitAsync(Wait, Ct);
        Assert.Empty(_history.Entries);
    }

    // The skip stays, and so does its entry.
    [Fact]
    public async Task UndoSkipThatCantBeSaved_KeepsTheSkipInHistory()
    {
        Show(Check(AppStatus.Available));
        Row("Example Editor").SkipCommand.Execute(null);
        await Saved();
        await _historyWriter.Idle.WaitAsync(Wait, Ct);
        File.Delete(_folder.PathOf("settings.json"));
        Directory.CreateDirectory(_folder.PathOf("settings.json"));

        Row("Example Editor").UndoSkipCommand.Execute(null);
        await Saved();
        await _historyWriter.Idle.WaitAsync(Wait, Ct);
        Assert.Equal(RowState.Skipped, Row("Example Editor").View.State);
        Assert.Equal(HistoryResult.Skipped, Assert.Single(_history.Entries).Result);
    }

    [Fact]
    public async Task SkipThatCantBeSaved_WritesNoHistory()
    {
        Show(Check(AppStatus.Available));
        File.Delete(_folder.PathOf("settings.json"));
        Directory.CreateDirectory(_folder.PathOf("settings.json"));
        Row("Example Editor").SkipCommand.Execute(null);
        await Saved();
        await _historyWriter.Idle.WaitAsync(Wait, Ct);
        Assert.Equal(RowState.Available, Row("Example Editor").View.State);
        Assert.Empty(_history.Entries);
    }

    [Fact]
    public async Task SkipOfAFailureThatCantBeSaved_BringsTheFailureBack()
    {
        Show(Check(AppStatus.Available));
        _vm.InstallChanged(Done(UpgradeResult.Failed, failure: UpgradeFailure.DiskFull));
        File.Delete(_folder.PathOf("settings.json"));
        Directory.CreateDirectory(_folder.PathOf("settings.json"));
        Row("Example Editor").SkipCommand.Execute(null);
        await Saved();
        Assert.Equal((RowState.Failed, "Not enough disk space"), (Row("Example Editor").View.State, Row("Example Editor").View.Status));
        Assert.True(_vm.CanUpdateAll);
    }

    [Fact]
    public void SkipRightBeforeQuit_StillWritesItsHistory()
    {
        Show(Check(AppStatus.Available));
        Row("Example Editor").SkipCommand.Execute(null);
        var done = false;
        new SaveDrain(_writer, _historyWriter, _ui.Post).Drain(Task.CompletedTask, () => done = true);
        _ui.Pump();
        Assert.True(done);
        Assert.Equal(HistoryResult.Skipped, Assert.Single(_history.Entries).Result);
    }

    [Fact]
    public async Task SkipUndoneBeforeItWasSaved_WritesNoHistory()
    {
        Show(Check(AppStatus.Available));
        Row("Example Editor").SkipCommand.Execute(null);
        Row("Example Editor").UndoSkipCommand.Execute(null);
        await Saved();
        await _historyWriter.Idle.WaitAsync(Wait, Ct);
        Assert.Empty(_history.Entries);
    }

    [Fact]
    public async Task SkipUndoAndSkipAgain_BeforeTheSaves_WriteOneEntry()
    {
        Show(Check(AppStatus.Available));
        Row("Example Editor").SkipCommand.Execute(null);
        Row("Example Editor").UndoSkipCommand.Execute(null);
        Row("Example Editor").SkipCommand.Execute(null);
        await Saved();
        await _historyWriter.Idle.WaitAsync(Wait, Ct);
        Assert.Single(_history.Entries);
    }

    // Every app without its own choice is on Auto, so it shows the pill and installs by itself (spec §6.2).
    [Fact]
    public void SwitchOn_PutsAppsOnAuto_AndTheyInstallByThemselves()
    {
        Show(Check(AppStatus.Available, scope: InstallScope.User));
        Assert.False(Row("Example Editor").Auto);
        SwitchOn();
        _vm.ConditionsChanged();
        Assert.True(Row("Example Editor").Auto);
        Assert.Equal(["Example.Editor"], Enqueued);
    }

    [Fact]
    public async Task ToggleAuto_ShowsThePill_AndSaves()
    {
        Show(Check(AppStatus.Available));
        Row("Example Editor").ToggleAutoCommand.Execute(null);
        Assert.True(Row("Example Editor").Auto);
        await Saved();
        Assert.True(_settings.Current.Apps.Single(a => a.Id == "Example.Editor").AutoChoice);
    }

    // An app's own choice keeps it off while the switch is on; setting it back to the switch's value clears it (spec §6.2).
    [Fact]
    public async Task ToggleAuto_WithTheSwitchOn_MakesAnException_AndSettingItBackClearsIt()
    {
        SwitchOn();
        Show(Check(AppStatus.Available));
        Row("Example Editor").ToggleAutoCommand.Execute(null);
        Assert.False(Row("Example Editor").Auto);
        await Saved();
        Assert.False(_settings.Current.Apps.Single(a => a.Id == "Example.Editor").AutoChoice);
        Row("Example Editor").ToggleAutoCommand.Execute(null);
        Assert.True(Row("Example Editor").Auto);
        await Saved();
        Assert.Null(_settings.Current.Apps.Single(a => a.Id == "Example.Editor").AutoChoice);
    }

    // Flipping the switch keeps an app's own choice.
    [Fact]
    public async Task OwnChoice_StaysWhenTheSwitchFlips()
    {
        Show(Check(AppStatus.Available));
        Row("Example Editor").ToggleAutoCommand.Execute(null);
        await Saved();
        SwitchOn();
        _settings.Update(f => f with { Settings = f.Settings with { AutoUpdateApps = false } });
        _vm.ConditionsChanged();
        Assert.True(Row("Example Editor").Auto);
        Assert.True(_settings.Current.Apps.Single(a => a.Id == "Example.Editor").AutoChoice);
    }

    [Fact]
    public async Task ChangeThatCantBeSaved_IsUndone_AndExplained()
    {
        Show(Check(AppStatus.Available));
        File.Delete(_folder.PathOf("settings.json"));
        Directory.CreateDirectory(_folder.PathOf("settings.json"));
        Row("Example Editor").ToggleAutoCommand.Execute(null);
        await Saved();
        Assert.False(Row("Example Editor").Auto);
        Assert.Equal(NoticeKind.SaveFailed, Assert.Single(_vm.Notices).Kind);
    }

    [Fact]
    public void CheckProblem_ShowsABanner_AndKeepsTheRows()
    {
        Show(Check(AppStatus.Available));
        _vm.CheckFinished(Failed(CheckProblem.WinGetUnreachable));
        Assert.Equal(("Can't reach winget right now, retrying", "winget call failed (0x800706BA)"), (_vm.Problem!.Title, _vm.Problem.Details));
        Assert.Equal(["Example Editor"], _vm.Updates.Select(r => r.Name));
        Show(Check(AppStatus.Available));
        Assert.Null(_vm.Problem);
    }

    [Fact]
    public void GoodCheck_AfterAProblem_ClosesTheBanner()
    {
        _vm.CheckFinished(Failed(CheckProblem.WinGetTooOld));
        Assert.True(_vm.HasProblem);
        var changed = new List<string?>();
        _vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        Show(Check(AppStatus.Available));
        Assert.False(_vm.HasProblem);
        Assert.Contains(nameof(UpdatesViewModel.HasProblem), changed);
    }

    [Fact]
    public void WinGetTooOld_OffersTheStore()
    {
        _vm.CheckFinished(Failed(CheckProblem.WinGetTooOld));
        Assert.Equal(("winget needs an update", true), (_vm.Problem!.Title, _vm.Problem.OffersStore));
        _vm.OpenStoreCommand.Execute(null);
        Assert.Equal(["ms-windows-store://pdp/?productid=9NBLGGH4NNS1"], _opened);
        Assert.Equal(TrayIconKind.Badge, _vm.Tray.Icon);
    }

    private void Offer(params string[] ids) =>
        _vm.CheckFinished(Checked(Check(AppStatus.Available)) with { NewApps = [.. ids.Select(id => new ListedApp(id, Name(id), @"ARP\Machine\X64\" + id))] });

    private IEnumerable<string> NewAppNotices => _vm.Notices.Where(n => n.Kind == NoticeKind.NewApp).Select(n => n.Title);

    private NoticeAction NewAppAction(string text) => Assert.Single(_vm.Notices, n => n.Kind == NoticeKind.NewApp).Actions.Single(a => a.Text == text);

    // New apps (spec §4.3).
    [Fact]
    public void NewApps_GetANoticeEach_InNameOrder()
    {
        Offer("Fabrikam.Chat", "Contoso.Paint");
        Assert.Equal(["New app: Contoso Paint", "New app: Fabrikam Chat"], NewAppNotices);
        Assert.Equal(["Track", "No thanks"], _vm.Notices.First(n => n.Kind == NoticeKind.NewApp).Actions.Select(a => a.Text));
    }

    [Fact]
    public void NewAppNotice_Stays_UntilTheAppGoes()
    {
        Offer("Contoso.Paint");
        _vm.CheckFinished(Checked(Check(AppStatus.Available)));
        Assert.Equal(["New app: Contoso Paint"], NewAppNotices);
        Offer();
        Assert.Empty(NewAppNotices);
    }

    [Fact]
    public async Task Track_AddsTheApp_WithAutoOff_AndChecksIt()
    {
        Offer("Contoso.Paint");
        NewAppAction("Track").Run();
        Assert.Empty(NewAppNotices);
        await Saved();
        var app = Assert.Single(_settings.Current.Apps, a => a.Id == "Contoso.Paint");
        Assert.Equal(("winget", "Contoso Paint", (bool?)null), (app.Source, app.Name, app.AutoChoice));
        Assert.Equal(1, _checksDue);
    }

    [Fact]
    public async Task NoThanks_NeverOffersItAgain()
    {
        _settings.Update(f => f with { KnownApps = ["Example.Editor"] });
        Offer("Contoso.Paint");
        NewAppAction("No thanks").Run();
        Assert.Empty(NewAppNotices);
        await Saved();
        Assert.Contains("Contoso.Paint", _settings.Current.KnownApps!);
    }

    // The page then moves the focus to the next notice's Track (spec §4.8): the one that takes the answered one's place.
    [Theory]
    [InlineData("Track")]
    [InlineData("No thanks")]
    public void Answer_NamesTheNextNotice_OnceItShows(string answer)
    {
        _settings.Update(f => f with { KnownApps = ["Example.Editor"] });
        Offer("Contoso.Paint", "Fabrikam.Chat", "Litware.Reader", "Northwind.Clock");
        string[]? shown = null;
        string? next = null;
        _vm.NewAppAnswered += (_, notice) => (shown, next) = ([.. NewAppNotices], notice?.Title);
        _vm.Notices.Where(n => n.Kind == NoticeKind.NewApp).ElementAt(1).Actions.Single(a => a.Text == answer).Run();
        Assert.NotNull(shown);
        Assert.Equal(["New app: Contoso Paint", "New app: Litware Reader", "New app: Northwind Clock"], shown);
        Assert.Equal("New app: Litware Reader", next);
    }

    // Then the focus goes to Check now.
    [Fact]
    public void AnsweringTheLastNotice_NamesNone()
    {
        Offer("Contoso.Paint", "Fabrikam.Chat");
        var named = new List<Notice?>();
        _vm.NewAppAnswered += (_, notice) => named.Add(notice);
        _vm.Notices.Where(n => n.Kind == NoticeKind.NewApp).ElementAt(1).Actions.Single(a => a.Text == "No thanks").Run();
        NewAppAction("No thanks").Run();
        Assert.Equal([null, null], named);
    }

    // The check that was running when the user answered still lists the app.
    [Theory]
    [InlineData("Track")]
    [InlineData("No thanks")]
    public void AnsweredApp_DoesntComeBack_WithTheCheckThatWasRunning(string answer)
    {
        _settings.Update(f => f with { KnownApps = ["Example.Editor"] });
        Offer("Contoso.Paint");
        _vm.CheckStarted();
        NewAppAction(answer).Run();
        Offer("Contoso.Paint");
        Assert.Empty(NewAppNotices);
    }

    [Fact]
    public async Task TrackThatCantBeSaved_SaysSo_AndTheAppIsOfferedAgain()
    {
        Offer("Contoso.Paint");
        File.Delete(_folder.PathOf("settings.json"));
        Directory.CreateDirectory(_folder.PathOf("settings.json"));
        NewAppAction("Track").Run();
        await Saved();
        Assert.Contains(_vm.Notices, n => n.Kind == NoticeKind.SaveFailed);
        Assert.Equal(0, _checksDue);
        Offer("Contoso.Paint");
        Assert.Equal(["New app: Contoso Paint"], NewAppNotices);
    }

    [Fact]
    public void NewApps_KeepNameOrder_AcrossChecks()
    {
        Offer("Fabrikam.Chat");
        Offer("Fabrikam.Chat", "Contoso.Paint");
        Assert.Equal(["New app: Contoso Paint", "New app: Fabrikam Chat"], NewAppNotices);
    }

    // Narrator hears which app each button is for.
    [Fact]
    public void NewAppButtons_NameTheApp()
    {
        Offer("Contoso.Paint");
        Assert.Equal(["Track: Contoso Paint", "No thanks: Contoso Paint"], Assert.Single(_vm.Notices, n => n.Kind == NoticeKind.NewApp).Actions.Select(a => a.Name));
    }

    // A new app isn't an update: no badge.
    [Fact]
    public void NewApp_LeavesTheTrayIconIdle()
    {
        _vm.CheckFinished(Checked(Check(AppStatus.UpToDate, offer: null)) with { NewApps = [new ListedApp("Contoso.Paint", "Contoso Paint", @"ARP\Machine\X64\Contoso.Paint")] });
        Assert.Equal(TrayIconKind.Idle, _vm.Tray.Icon);
    }

    // Tracked from Choose apps or Restore: its notice goes at once, not after the next check.
    [Fact]
    public void NewApp_TrackedAnotherWay_LosesItsNotice()
    {
        Offer("Contoso.Paint", "Fabrikam.Chat");
        _settings.Update(f => f with { Apps = [.. f.Apps, new TrackedApp { Id = "contoso.paint", Source = "winget" }] });
        _vm.TrackedAppsChanged(added: true);
        Assert.Equal(["New app: Fabrikam Chat"], NewAppNotices);
    }

    private void AnswerNewApp(string name, string answer) =>
        _vm.Notices.Single(n => n.Title == "New app: " + name).Actions.Single(a => a.Text == answer).Run();

    // At most three show, so the rows stay in view; answering one brings the next (spec §4.3).
    [Fact]
    public async Task NewApps_ShowThreeAtATime_TheRestWaitTheirTurn()
    {
        _settings.Update(f => f with { KnownApps = ["Example.Editor"] });
        Offer("Fabrikam.Chat", "Contoso.Paint", "Northwind.Notes", "Adatum.Photos", "Litware.Reader");
        Assert.Equal(["New app: Adatum Photos", "New app: Contoso Paint", "New app: Fabrikam Chat"], NewAppNotices);
        AnswerNewApp("Adatum Photos", "No thanks");
        Assert.Equal(["New app: Contoso Paint", "New app: Fabrikam Chat", "New app: Litware Reader"], NewAppNotices);
        AnswerNewApp("Contoso Paint", "Track");
        await Saved();
        Assert.Equal(["New app: Fabrikam Chat", "New app: Litware Reader", "New app: Northwind Notes"], NewAppNotices);
    }

    // The group stays together, above any tip.
    [Fact]
    public void NewApps_StayTogether_AboveTips()
    {
        _vm.TrackedAppsChanged(added: false);
        Offer("Fabrikam.Chat");
        Offer("Fabrikam.Chat", "Northwind.Notes");
        Assert.Equal([NoticeKind.NewApp, NoticeKind.NewApp, NoticeKind.HiddenIconsTip], _vm.Notices.Select(n => n.Kind));
        Assert.Equal(["New app: Fabrikam Chat", "New app: Northwind Notes"], NewAppNotices);
    }

    // A fourth app found later waits behind the three; tracking one from Choose apps brings it in.
    [Fact]
    public void FourthApp_WaitsItsTurn()
    {
        _settings.Update(f => f with { KnownApps = ["Example.Editor"] });
        Offer("Contoso.Paint", "Fabrikam.Chat", "Northwind.Notes");
        Offer("Contoso.Paint", "Fabrikam.Chat", "Northwind.Notes", "Woodgrove.Mail");
        Assert.Equal(["New app: Contoso Paint", "New app: Fabrikam Chat", "New app: Northwind Notes"], NewAppNotices);
        _settings.Update(f => f with { Apps = [.. f.Apps, new TrackedApp { Id = "Fabrikam.Chat", Source = "winget" }] });
        _vm.TrackedAppsChanged(added: true);
        Assert.Equal(["New app: Contoso Paint", "New app: Northwind Notes", "New app: Woodgrove Mail"], NewAppNotices);
    }

    // A later check that names an app anew shows the new name, in its place.
    [Fact]
    public void RenamedApp_TakesItsNewName()
    {
        Offer("Contoso.Paint", "Fabrikam.Chat");
        _vm.CheckFinished(Checked(Check(AppStatus.Available)) with
        {
            NewApps = [new ListedApp("Fabrikam.Chat", "Fabrikam Chat", @"ARP\Machine\X64\Fabrikam.Chat"), new ListedApp("Contoso.Paint", "Adatum Paint", @"ARP\Machine\X64\Contoso.Paint")],
        });
        Assert.Equal(["New app: Adatum Paint", "New app: Fabrikam Chat"], NewAppNotices);
    }

    // Before the first list is noted there's nothing to add the answer to: the notice goes, and settings.json stays as it was.
    [Fact]
    public async Task NoThanks_BeforeTheFirstList_WritesNothing()
    {
        Offer("Contoso.Paint");
        var before = File.Exists(_folder.PathOf("settings.json")) ? File.ReadAllText(_folder.PathOf("settings.json")) : null;
        NewAppAction("No thanks").Run();
        await Saved();
        Assert.Empty(NewAppNotices);
        Assert.Null(_settings.Current.KnownApps);
        Assert.Equal(before, File.Exists(_folder.PathOf("settings.json")) ? File.ReadAllText(_folder.PathOf("settings.json")) : null);
    }

    [Fact]
    public void WhatsNew_OpensTheReleaseNotes()
    {
        Show(Check(AppStatus.Available));
        Row("Example Editor").OpenNotesCommand.Execute(null);
        Assert.Equal(["https://example.com/notes"], _opened);
    }

    // With the notes' text, What's new opens its page instead (spec §4.9).
    [Fact]
    public void WhatsNew_WithText_OpensThePage()
    {
        var asked = 0;
        _vm.NotesRequested += (_, _) => asked++;
        Show(Check(AppStatus.Available, text: "New\n- Tabs"));
        Row("Example Editor").OpenNotesCommand.Execute(null);
        Assert.Equal(1, asked);
        Assert.Empty(_opened);
        Assert.Same(Row("Example Editor"), _vm.WhatsNew.Row);
        Assert.Equal([new NoteLine(NoteKind.Heading, "New"), new NoteLine(NoteKind.Bullet, "Tabs")], _vm.WhatsNew.Lines);
        Assert.True(_vm.WhatsNew.HasLink);
        _vm.WhatsNew.OpenLinkCommand.Execute(null);
        Assert.Equal(["https://example.com/notes"], _opened);
    }

    [Fact]
    public void WhatsNewPage_WithoutALink_HasNone()
    {
        Show(Check(AppStatus.Available, notes: null, text: "- Tabs"));
        Row("Example Editor").OpenNotesCommand.Execute(null);
        Assert.False(_vm.WhatsNew.HasLink);
    }

    [Fact]
    public void WhatsNewPage_Button_DoesWhatTheRowsDoes_ThenGoesBack()
    {
        var done = 0;
        _vm.WhatsNew.Done += (_, _) => done++;
        Show(Check(AppStatus.Available, text: "- Tabs"));
        Row("Example Editor").OpenNotesCommand.Execute(null);
        Assert.Equal("Update", _vm.WhatsNew.Row!.View.ActionText);
        _vm.WhatsNew.PrimaryCommand.Execute(null);
        Assert.Equal(["Example.Editor"], Enqueued);
        Assert.Equal(1, done);
    }

    [Fact]
    public void WhatsNewPage_KeepsUpWithANewCheck()
    {
        Show(Check(AppStatus.Available, text: "- Tabs"));
        Row("Example Editor").OpenNotesCommand.Execute(null);
        Show(Check(AppStatus.Available, text: "- Panes"));
        Assert.Equal([new NoteLine(NoteKind.Bullet, "Panes")], _vm.WhatsNew.Lines);
    }

    [Fact]
    public void WhatsNewPage_KeepsItsNotes_WhenTheTextGoes()
    {
        Show(Check(AppStatus.Available, text: "- Tabs"));
        Row("Example Editor").OpenNotesCommand.Execute(null);
        Show(Check(AppStatus.Available));
        Assert.Equal([new NoteLine(NoteKind.Bullet, "Tabs")], _vm.WhatsNew.Lines);
    }

    // A busy row keeps its button to itself.
    [Fact]
    public void WhatsNewPage_Button_WaitsWhileTheRowIsBusy()
    {
        var done = 0;
        _vm.WhatsNew.Done += (_, _) => done++;
        Show(Check(AppStatus.Available, text: "- Tabs"));
        Row("Example Editor").OpenNotesCommand.Execute(null);
        _vm.InstallChanged(Item(InstallStage.NotClosed));
        Assert.False(_vm.WhatsNew.OffersAction);
        _vm.WhatsNew.PrimaryCommand.Execute(null);
        Assert.Empty(_installer.ForceClosed);
        Assert.Equal(0, done);
    }

    // The link goes with the notes it came with, and a check's new notes bring theirs (spec §4.9).
    [Fact]
    public void WhatsNewPage_KeepsItsLink_WithItsNotes()
    {
        Show(Check(AppStatus.Available, text: "- Tabs"));
        Row("Example Editor").OpenNotesCommand.Execute(null);
        Show(Check(AppStatus.Available, notes: null));
        Assert.True(_vm.WhatsNew.HasLink);
        _vm.WhatsNew.OpenLinkCommand.Execute(null);
        Assert.Equal(["https://example.com/notes"], _opened);
        Show(Check(AppStatus.Available, notes: null, text: "- Panes"));
        Assert.False(_vm.WhatsNew.HasLink);
    }

    // The update installed: the notes and their link stay, and the button goes.
    [Fact]
    public void WhatsNewPage_AfterTheUpdate_KeepsItsNotesAndLink()
    {
        Show(Check(AppStatus.Available, text: "- Tabs"));
        Row("Example Editor").OpenNotesCommand.Execute(null);
        Show(Check(AppStatus.UpToDate, offer: null, notes: null));
        Assert.Equal((true, false), (_vm.WhatsNew.HasLink, _vm.WhatsNew.OffersAction));
        Assert.Equal([new NoteLine(NoteKind.Bullet, "Tabs")], _vm.WhatsNew.Lines);
        Assert.False(_vm.WhatsNew.HasNoNotes);
    }

    // The notes shown were the older version's (spec §4.9).
    [Fact]
    public void WhatsNewPage_NewerVersionWithoutNotes_SaysSo()
    {
        Show(Check(AppStatus.Available, text: "- Tabs"));
        Row("Example Editor").OpenNotesCommand.Execute(null);
        Show(Check(AppStatus.Available, offer: "2.6.0", notes: null));
        Assert.Empty(_vm.WhatsNew.Lines);
        Assert.Equal((true, "No release notes for 2.6.0.", false), (_vm.WhatsNew.HasNoNotes, _vm.WhatsNew.NoNotes, _vm.WhatsNew.HasLink));
    }

    [Fact]
    public void WhatsNewPage_NewerVersionWithOnlyALink_OffersIt()
    {
        Show(Check(AppStatus.Available, notes: null, text: "- Tabs"));
        Row("Example Editor").OpenNotesCommand.Execute(null);
        Show(Check(AppStatus.Available, offer: "2.6.0", notes: "https://example.com/2.6"));
        Assert.Equal((true, true), (_vm.WhatsNew.HasNoNotes, _vm.WhatsNew.HasLink));
        _vm.WhatsNew.OpenLinkCommand.Execute(null);
        Assert.Equal(["https://example.com/2.6"], _opened);
    }

    [Fact]
    public void WhatsNewPage_NewerVersionWithNotes_ShowsThem()
    {
        Show(Check(AppStatus.Available, text: "- Tabs"));
        Row("Example Editor").OpenNotesCommand.Execute(null);
        Show(Check(AppStatus.Available, offer: "2.6.0", notes: null));
        Show(Check(AppStatus.Available, offer: "2.7.0", text: "- Panes"));
        Assert.Equal([new NoteLine(NoteKind.Bullet, "Panes")], _vm.WhatsNew.Lines);
        Assert.False(_vm.WhatsNew.HasNoNotes);
    }

    // No button once the app is no longer tracked (spec §4.9).
    [Fact]
    public void WhatsNewPage_Button_DoesNothingForAStoppedRow()
    {
        var done = 0;
        _vm.WhatsNew.Done += (_, _) => done++;
        Show(Check(AppStatus.Available, text: "- Tabs"));
        var row = Row("Example Editor");
        row.OpenNotesCommand.Execute(null);
        Assert.True(_vm.WhatsNew.OffersAction);
        row.StopTrackingCommand.Execute(null);
        Assert.False(_vm.WhatsNew.OffersAction);
        _vm.WhatsNew.PrimaryCommand.Execute(null);
        Assert.Empty(_installer.Enqueued);
        Assert.Equal(0, done);
    }

    [Fact]
    public void WhatsNewPage_OffersNoButton_OnceTheAppIsUntrackedElsewhere()
    {
        Show(Check(AppStatus.Available, text: "- Tabs"));
        Row("Example Editor").OpenNotesCommand.Execute(null);
        _settings.Update(f => f with { Apps = [] });
        _vm.TrackedAppsChanged(added: false);
        Assert.False(_vm.WhatsNew.OffersAction);
    }

    [Fact]
    public void AppsAddedDuringACheck_GetAnotherCheck()
    {
        _vm.CheckStarted();
        _vm.TrackedAppsChanged(added: true);
        Assert.Equal(0, _checksDue);
        Show(Check(AppStatus.Available));
        Assert.Equal(1, _checksDue);
    }

    [Fact]
    public void AppsAdded_AreCheckedRightAway()
    {
        _vm.TrackedAppsChanged(added: true);
        Assert.Equal(1, _checksDue);
    }

    [Fact]
    public async Task TicksStillSaving_LandBeforeRowsLeaveAndTheCheckStarts()
    {
        Show(Check(AppStatus.Available), Check(AppStatus.UpToDate, "Example.Viewer", offer: null));
        var clockTracked = false;
        _scheduler.CheckDue += (_, _) => clockTracked = _settings.Current.Apps.Any(a => a.Id == "Example.Clock");
        using var gate = new ManualResetEventSlim();
        try
        {
            _writer.Update(file =>
            {
                gate.Wait(Wait);
                return file with { Apps = [.. file.Apps.Where(a => a.Id != "Example.Viewer"), App("Example.Clock")] };
            });
            _vm.TrackedAppsChanged(added: true);
            _ui.Pump();
            Assert.Single(_vm.UpToDate);
            Assert.Equal(0, _checksDue);
        }
        finally
        {
            gate.Set();
        }
        await Saved();
        Assert.Empty(_vm.UpToDate);
        Assert.Equal(1, _checksDue);
        Assert.True(clockTracked);
    }

    [Fact]
    public void UntrackedApps_LeaveThePage()
    {
        Show(Check(AppStatus.Available), Check(AppStatus.UpToDate, "Example.Viewer", offer: null));
        _settings.Update(f => f with { Apps = f.Apps.Where(a => a.Id != "Example.Viewer").ToList() });
        _vm.TrackedAppsChanged(added: false);
        Assert.Empty(_vm.UpToDate);
    }

    [Fact]
    public void UpToDatePreview_IsKept_WhileItsRowsStayTheSame()
    {
        Show(Check(AppStatus.Available), Check(AppStatus.UpToDate, "Example.Viewer", offer: null));
        var preview = _vm.UpToDatePreview;
        _vm.InstallChanged(Item(InstallStage.Downloading, progress: Downloading(10 * MB, 100 * MB)));
        _vm.InstallChanged(Item(InstallStage.Downloading, progress: Downloading(20 * MB, 100 * MB)));
        Assert.Same(preview, _vm.UpToDatePreview);
        Show(Check(AppStatus.UpToDate, "Example.Paint", offer: null));
        Assert.Equal(["Example Paint", "Example Viewer"], _vm.UpToDatePreview.Select(r => r.Name));
    }

    [Fact]
    public void Tray_ShowsWork_ThenUpdatesReady_ThenUpToDate()
    {
        Show(Check(AppStatus.Available));
        _vm.InstallChanged(Item(InstallStage.Downloading, progress: Downloading(180 * MB, 400 * MB)));
        Assert.Equal(new TrayState(TrayIconKind.Working, "Tiny Tracker: Installing Example Editor (45%)"), _vm.Tray);
        _vm.InstallChanged(Done(UpgradeResult.Failed, failure: UpgradeFailure.Other));
        Assert.Equal(new TrayState(TrayIconKind.Badge, "Tiny Tracker: 1 update ready"), _vm.Tray);
        Show(Check(AppStatus.UpToDate, installed: "2.5.0", offer: null));
        Assert.Equal(new TrayState(TrayIconKind.Idle, "Tiny Tracker: Up to date"), _vm.Tray);
        _vm.CheckStarted();
        Assert.Equal(new TrayState(TrayIconKind.Working, "Tiny Tracker: Checking for updates"), _vm.Tray);
    }

    [Fact]
    public void StartupNotices_ShowDamagedAndUnreadableFiles()
    {
        Directory.CreateDirectory(_folder.PathOf("locked.json"));
        var history = new HistoryStore(_folder.PathOf("locked.json"), _time);
        history.Load();
        using var vm = new UpdatesViewModel(_scheduler, _installer, _conditions, _settings, _writer, history, _historyWriter, _time, _ui.Post, _opened.Add);
        vm.ShowStartupNotices(settingsRecovered: true, historyRecovered: false);
        Assert.Equal([NoticeKind.SettingsRecovered, NoticeKind.HistoryUnreadable], vm.Notices.Select(n => n.Kind));
        vm.DismissCommand.Execute(vm.Notices[0]);
        Assert.Equal([NoticeKind.HistoryUnreadable], vm.Notices.Select(n => n.Kind));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(UpgradeResult.Failed, true)]
    [InlineData(UpgradeResult.AppInUse, true)]
    [InlineData(UpgradeResult.NeedsAdmin, true)]
    [InlineData(UpgradeResult.CouldNotClose, true)]
    [InlineData(UpgradeResult.NotInstalled, false)]
    [InlineData(UpgradeResult.Updated, false)]
    public void History_MayRetryOnlyWhatTheRowOffers(UpgradeResult? done, bool retry)
    {
        Show(Check(AppStatus.Available));
        if (done is { } result) _vm.InstallChanged(Done(result, failure: result == UpgradeResult.Failed ? UpgradeFailure.DiskFull : UpgradeFailure.None));
        var editor = new PackageKey("Example.Editor", "winget");
        Assert.Equal(retry, _vm.CanRetry(editor, "2.5.0"));
        Assert.Equal(retry, _vm.Retry(editor, "2.5.0"));
        Assert.Equal(retry ? 1 : 0, _installer.Enqueued.Count);
    }

    [Fact]
    public void History_MayNotRetrySkippedPhantomActiveRemovedOrOtherVersions()
    {
        _settings.Update(f => f with { Apps = [.. f.Apps, App("Example.Clock"), App("Example.Sync", phantom: true)] });
        Show(
            Check(AppStatus.Available),
            Check(AppStatus.Available, "Example.Paint"),
            Check(AppStatus.Available, "Example.Viewer"),
            Check(AppStatus.Skipped, "Example.Clock", skipped: "2.5.0"),
            Check(AppStatus.Phantom, "Example.Sync"));
        _vm.InstallChanged(Item(InstallStage.Downloading, "Example.Paint"));
        Row("Example Viewer").StopTrackingCommand.Execute(null);
        Assert.False(_vm.CanRetry(new PackageKey("Example.Viewer", "winget"), "2.5.0"));
        Assert.False(_vm.CanRetry(new PackageKey("Example.Editor", "winget"), "2.4.9"));
        Assert.False(_vm.CanRetry(new PackageKey("Example.Paint", "winget"), "2.5.0"));
        Assert.False(_vm.CanRetry(new PackageKey("Example.Clock", "winget"), "2.5.0"));
        Assert.False(_vm.CanRetry(new PackageKey("Example.Sync", "winget"), "2.5.0"));
        Assert.False(_vm.CanRetry(new PackageKey("Example.Missing", "winget"), "2.5.0"));
        Assert.True(_vm.CanRetry(new PackageKey("example.editor", "WINGET"), "2.5.0"));
    }

    [Fact]
    public void History_GetsIconsAndHearsOfRowChanges()
    {
        var changes = 0;
        _vm.RowsChanged += (_, _) => changes++;
        Show(Check(AppStatus.Available));
        Assert.True(changes > 0);
        Assert.Equal(@"ARP\Machine\X64\Example Editor", _vm.LocalIdOf(new PackageKey("Example.Editor", "winget")));
        Assert.Equal("", _vm.LocalIdOf(new PackageKey("Example.Missing", "winget")));
    }

    [Fact]
    public void LastChecks_AreKeptForDiagnostics()
    {
        Assert.Equal(((DateTimeOffset?)null, CheckProblem.None, (DateTimeOffset?)null), (_vm.LastCheckAt, _vm.LastProblem, _vm.LastGoodCheckAt));
        Show(Check(AppStatus.Available));
        _time.Advance(TimeSpan.FromHours(1));
        _vm.CheckFinished(Failed(CheckProblem.WinGetUnreachable));
        Assert.Equal(((DateTimeOffset?)Now + TimeSpan.FromHours(1), CheckProblem.WinGetUnreachable, (DateTimeOffset?)Now), (_vm.LastCheckAt, _vm.LastProblem, _vm.LastGoodCheckAt));
    }

    // For Choose apps' tracked apps it can't list.
    [Fact]
    public void StatusOf_SaysWhatTheLastCheckFound()
    {
        Show(Check(AppStatus.NotInCatalog));
        Assert.Equal(AppStatus.NotInCatalog, _vm.StatusOf("example.editor", "winget"));
        Assert.Null(_vm.StatusOf("Example.Paint", "winget"));
    }

    // The file's own notice already says changes aren't saved.
    [Fact]
    public void CheckThatCouldntSave_WhileSettingsCantBeRead_ShowsOneNotice()
    {
        var path = _folder.PathOf("locked-settings.json");
        File.WriteAllText(path, "{}");
        var settings = new SettingsStore(path);
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) settings.Load();
        using var vm = new UpdatesViewModel(_scheduler, _installer, _conditions, settings, _writer, _history, _historyWriter, _time, _ui.Post, _opened.Add);
        vm.ShowStartupNotices(settingsRecovered: false, historyRecovered: false);
        vm.CheckFinished(Failed(CheckProblem.SettingsNotSaved));
        Assert.Equal([NoticeKind.SettingsUnreadable], vm.Notices.Select(n => n.Kind));
        Assert.Null(vm.Problem);
        Assert.Equal(CheckProblem.SettingsNotSaved, vm.LastProblem);
    }

    [Fact]
    public void HistoryReadAgain_ClearsItsNotice()
    {
        var path = _folder.PathOf("locked.json");
        File.WriteAllText(path, "{}");
        var history = new HistoryStore(path, _time);
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) history.Load();
        using var vm = new UpdatesViewModel(_scheduler, _installer, _conditions, _settings, _writer, history, _historyWriter, _time, _ui.Post, _opened.Add);
        vm.ShowStartupNotices(settingsRecovered: false, historyRecovered: false);
        Assert.Equal([NoticeKind.HistoryUnreadable], vm.Notices.Select(n => n.Kind));
        history.Retry();
        vm.FilesChanged();
        Assert.Empty(vm.Notices);
    }

    [Fact]
    public async Task AutoApp_InstallsByItself_AndRemembersTheAttempt()
    {
        TrackAuto("Example.Editor");
        Show(Check(AppStatus.Available, auto: true, scope: InstallScope.User));
        Assert.Equal(["Example.Editor"], Enqueued);
        await Saved();
        Assert.Equal(Now, _settings.Current.Apps.Single(a => a.Id == "Example.Editor").Offer!.LastAutoAttempt);
    }

    [Fact]
    public void AutoApps_InstallOneAtATime()
    {
        TrackAuto("Example.Editor", "Example.Paint");
        Show(Check(AppStatus.Available, auto: true, scope: InstallScope.User), Check(AppStatus.Available, "Example.Paint", auto: true, scope: InstallScope.User));
        Assert.Equal(["Example.Editor"], Enqueued);
        _vm.InstallChanged(Item(InstallStage.Downloading));
        _vm.InstallChanged(Done(UpgradeResult.Updated, after: Check(AppStatus.UpToDate, installed: "2.5.0", offer: null)));
        Assert.Equal(["Example.Editor", "Example.Paint"], Enqueued);
    }

    [Fact]
    public void AutoApp_WaitsWhileAnotherInstallRuns()
    {
        Show(Check(AppStatus.Available));
        Row("Example Editor").PrimaryCommand.Execute(null);
        _vm.InstallChanged(Item(InstallStage.Downloading));
        TrackAuto("Example.Paint");
        Show(Check(AppStatus.Available, "Example.Paint", auto: true, scope: InstallScope.User));
        Assert.Equal(["Example.Editor"], Enqueued);
        _vm.InstallChanged(Done(UpgradeResult.Updated, after: Check(AppStatus.UpToDate, installed: "2.5.0", offer: null)));
        Assert.Equal(["Example.Editor", "Example.Paint"], Enqueued);
    }

    [Fact]
    public void AutoAppThatNeedsAdmin_SaysSo_AndWaitsForTheUsersInstall()
    {
        TrackAuto("Example.Editor");
        Show(Check(AppStatus.Available, auto: true));
        Assert.Empty(_installer.Enqueued);
        var row = Row("Example Editor");
        Assert.Equal((RowState.NeedsPermission, "Needs admin permission", RowAction.Install), (row.View.State, row.View.Status, row.View.Action));
        row.PrimaryCommand.Execute(null);
        var request = Assert.Single(_installer.Enqueued);
        Assert.Equal((InstallRoute.Helper, false), (request.Route, request.ByItself));
    }

    [Fact]
    public void InSilentMode_AnAutoAppThatNeedsAdmin_InstallsByItself_ThroughTheHelper()
    {
        TrackAuto("Example.Editor");
        _settings.Update(f => f with { Settings = f.Settings with { SilentMode = true } });
        Show(Check(AppStatus.Available, auto: true));
        var request = Assert.Single(_installer.Enqueued);
        Assert.Equal((InstallRoute.Helper, true), (request.Route, request.ByItself));
    }

    [Theory]
    [InlineData(InstallScope.Machine, false, InstallRoute.Helper)]
    [InlineData(InstallScope.User, false, InstallRoute.App)]
    [InlineData(InstallScope.Unknown, false, InstallRoute.App)]
    [InlineData(InstallScope.Unknown, true, InstallRoute.Helper)]
    public void Update_GoesWhereTheSpecTableSays_WithTheAppsLocalId(InstallScope scope, bool silent, InstallRoute route)
    {
        _settings.Update(f => f with { Settings = f.Settings with { SilentMode = silent } });
        Show(Check(AppStatus.Available, scope: scope));
        Row("Example Editor").PrimaryCommand.Execute(null);
        var request = Assert.Single(_installer.Enqueued);
        Assert.Equal((route, false, false, $@"ARP\{scope}\X64\Example Editor"), (request.Route, request.ByItself, request.CloseFirst, request.LocalId));
    }

    [Fact]
    public void AutoInstall_IsMarkedAsStartedByItself()
    {
        TrackAuto("Example.Editor");
        Show(Check(AppStatus.Available, auto: true, scope: InstallScope.User));
        var request = Assert.Single(_installer.Enqueued);
        Assert.Equal((InstallRoute.App, true), (request.Route, request.ByItself));
    }

    [Fact]
    public void WinGetsNeedsAdminAnswer_IsInstalledThroughTheHelper()
    {
        Show(Check(AppStatus.Available, scope: InstallScope.User));
        _vm.InstallChanged(Done(UpgradeResult.NeedsAdmin, code: "0x8A150019"));
        Row("Example Editor").PrimaryCommand.Execute(null);
        var request = Assert.Single(_installer.Enqueued);
        Assert.Equal((InstallRoute.Helper, true), (request.Route, request.NeedsAdmin));
    }

    // Close & update after an Install found the app running: it still needs admin, so it goes through the helper too.
    [Fact]
    public void AppInUse_AfterAnInstall_ClosesAndUpdatesThroughTheHelper()
    {
        Show(Check(AppStatus.Available, scope: InstallScope.User));
        _vm.InstallChanged(Done(UpgradeResult.NeedsAdmin, code: "0x8A150019"));
        Row("Example Editor").PrimaryCommand.Execute(null);
        var install = Assert.Single(_installer.Enqueued);
        _vm.InstallChanged(new InstallItem(install, InstallStage.Done) { Done = new InstallDone(new UpgradeOutcome(UpgradeResult.AppInUse, Code: "0x8A150101")) });
        var row = Row("Example Editor");
        Assert.Equal(RowAction.CloseAndUpdate, row.View.Action);
        row.PrimaryCommand.Execute(null);
        var close = _installer.Enqueued[1];
        Assert.Equal((InstallRoute.Helper, true, true), (close.Route, close.NeedsAdmin, close.CloseFirst));
    }

    [Fact]
    public void CloseAndUpdate_AsksTheQueueToCloseTheAppFirst()
    {
        Show(Check(AppStatus.Available, scope: InstallScope.User));
        _vm.InstallChanged(Done(UpgradeResult.AppInUse, code: "0x8A150101"));
        Row("Example Editor").PrimaryCommand.Execute(null);
        var request = Assert.Single(_installer.Enqueued);
        Assert.Equal((true, @"ARP\User\X64\Example Editor"), (request.CloseFirst, request.LocalId));
    }

    [Fact]
    public void ForceClose_IsTheUsersAnswer_ToTheQueue()
    {
        Show(Check(AppStatus.Available));
        _vm.InstallChanged(Item(InstallStage.NotClosed));
        var row = Row("Example Editor");
        row.PrimaryCommand.Execute(null);
        Assert.Equal([row.Key], _installer.ForceClosed);
        row.CancelCommand.Execute(null);
        Assert.Equal([row.Key], _installer.Cancelled);
        Assert.Empty(_installer.Enqueued);
    }

    [Fact]
    public void UpdateAll_TakesTheRowsThatNeedPermission_ThroughTheHelper()
    {
        TrackAuto("Example.Editor");
        Show(Check(AppStatus.Available, auto: true), Check(AppStatus.Available, "Example.Paint", scope: InstallScope.User));
        Assert.Equal("Update all (2)", _vm.UpdateAllText);
        _vm.UpdateAllCommand.Execute(null);
        Assert.Equal([("Example.Editor", InstallRoute.Helper), ("Example.Paint", InstallRoute.App)], _installer.Enqueued.Select(r => (r.Package.Id, r.Route)).Order());
    }

    [Fact]
    public async Task AdminFallback_ShowsItsTipOnce_Ever()
    {
        _vm.AdminFallback();
        var tip = Assert.Single(_vm.Notices);
        Assert.Equal(("", "Admin updates will ask for permission one by one, because winget doesn't answer the admin helper.", NoticeSeverity.Informational, true),
            (tip.Title, tip.Message, tip.Severity, tip.Closable));
        _vm.AdminFallback();
        await Saved();
        Assert.Equal(["adminFallback"], _settings.Current.TipsShown);
        _vm.DismissCommand.Execute(tip);
        _vm.AdminFallback();
        Assert.Empty(_vm.Notices);
    }

    // Windows puts a new app's tray icon under ^ (spec §4.3).
    [Fact]
    public async Task LeavingChooseAppsTheFirstTime_ShowsTheHiddenIconsTipOnce_Ever()
    {
        _vm.TrackedAppsChanged(added: false);
        var tip = Assert.Single(_vm.Notices);
        Assert.Equal(("", "Windows keeps new icons under ^ on the taskbar. Drag the hamster out to keep it in view.", NoticeSeverity.Informational, true),
            (tip.Title, tip.Message, tip.Severity, tip.Closable));
        await Saved();
        Assert.Equal(["hiddenIcons"], _settings.Current.TipsShown);
        _vm.DismissCommand.Execute(tip);
        _vm.TrackedAppsChanged(added: false);
        Assert.Empty(_vm.Notices);
    }

    [Fact]
    public void ToastsInstall_TakesEveryRowThatNeedsPermission_ThroughTheHelper()
    {
        TrackAuto("Example.Editor");
        Show(Check(AppStatus.Available, auto: true), Check(AppStatus.Available, "Example.Paint", scope: InstallScope.User),
            Check(AppStatus.Available, "Example.Clock", scope: InstallScope.User));
        _vm.InstallChanged(Done(UpgradeResult.NeedsAdmin, "Example.Paint", code: "0x8A150019"));
        _vm.InstallNeedingPermission();
        Assert.Equal([("Example.Editor", InstallRoute.Helper), ("Example.Paint", InstallRoute.Helper)], _installer.Enqueued.Select(r => (r.Package.Id, r.Route)).Order());
    }

    [Fact]
    public void ToastsCloseAndUpdate_ClosesEveryAppInUseFirst()
    {
        Show(Check(AppStatus.Available, scope: InstallScope.User), Check(AppStatus.Available, "Example.Paint", scope: InstallScope.User));
        _vm.InstallChanged(Done(UpgradeResult.AppInUse, code: "0x8A150101"));
        _vm.CloseAndUpdateInUse();
        var request = Assert.Single(_installer.Enqueued);
        Assert.Equal(("Example.Editor", true), (request.Package.Id, request.CloseFirst));
    }

    [Fact]
    public void AutoApp_WithinTheWait_SaysWhenItInstalls()
    {
        TrackAuto("Example.Editor");
        _settings.Update(f => f with { Settings = f.Settings with { AutoInstallWaitDays = 3 } });
        Show(Check(AppStatus.Available, auto: true, scope: InstallScope.User));
        Assert.Empty(_installer.Enqueued);
        Assert.Equal("Installs automatically in 1\u00a0day", Row("Example Editor").View.Status);
    }

    [Fact]
    public void AutoApp_OnAMeteredConnection_WaitsUntilItEnds()
    {
        TrackAuto("Example.Editor");
        _conditions.State = _conditions.State with { Metered = true };
        Show(Check(AppStatus.Available, auto: true, scope: InstallScope.User));
        Assert.Equal("Waits for an unmetered connection", Row("Example Editor").View.Status);
        Assert.Empty(_installer.Enqueued);
        _conditions.State = _conditions.State with { Metered = false };
        _vm.ConditionsChanged();
        Assert.Equal(["Example.Editor"], Enqueued);
    }

    [Fact]
    public void AutoApp_Offline_WaitsForTheNetwork()
    {
        TrackAuto("Example.Editor");
        _conditions.State = _conditions.State with { Offline = true };
        Show(Check(AppStatus.Available, auto: true, scope: InstallScope.User));
        Assert.Equal("Waits for the network", Row("Example Editor").View.Status);
        Assert.Empty(_installer.Enqueued);
        _conditions.State = _conditions.State with { Offline = false };
        _vm.ConditionsChanged();
        Assert.Equal(["Example.Editor"], Enqueued);
    }

    // The install window (spec §6.2); now is 08:00.
    [Fact]
    public void AutoApp_OutsideTheWindow_SaysWhen_AndInstallsOnceItOpens()
    {
        TrackAuto("Example.Editor");
        Window(9, 17);
        Show(Check(AppStatus.Available, auto: true, scope: InstallScope.User));
        Assert.Equal("Installs automatically at 09:00", Row("Example Editor").View.Status);
        Pass(TimeSpan.FromMinutes(59));
        Assert.Empty(_installer.Enqueued);
        Pass(TimeSpan.FromMinutes(1));
        Assert.Equal(["Example.Editor"], Enqueued);
    }

    [Fact]
    public void WindowTurnedOff_InstallsRightAway()
    {
        TrackAuto("Example.Editor");
        Window(9, 17);
        Show(Check(AppStatus.Available, auto: true, scope: InstallScope.User));
        _settings.Update(f => f with { Settings = f.Settings with { InstallWindowEnabled = false } });
        _vm.ConditionsChanged();
        Assert.Equal(["Example.Editor"], Enqueued);
    }

    // A clock set forward past the start doesn't wait for the old timer.
    [Fact]
    public void ClockChangedIntoTheWindow_InstallsAtTheNextLook()
    {
        TrackAuto("Example.Editor");
        Window(9, 17);
        Show(Check(AppStatus.Available, auto: true, scope: InstallScope.User));
        _time.SetUtcNow(Now.AddHours(2));
        _vm.ConditionsChanged();
        Assert.Equal(["Example.Editor"], Enqueued);
    }

    // Here the window opens half an hour sooner.
    [Fact]
    public void TimeZoneChange_MovesTheWindowsStart()
    {
        TrackAuto("Example.Editor");
        Window(9, 17);
        Show(Check(AppStatus.Available, auto: true, scope: InstallScope.User));
        _time.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("UTC+00:30", TimeSpan.FromMinutes(30), "UTC+00:30", "UTC+00:30"));
        _vm.ConditionsChanged();
        Pass(TimeSpan.FromMinutes(30));
        Assert.Equal(["Example.Editor"], Enqueued);
    }

    // An install the window let start runs to its end (spec §6.2).
    [Fact]
    public void WindowClosingDuringAnAutoInstall_LetsItFinish_AndHoldsTheNext()
    {
        TrackAuto("Example.Editor", "Example.Paint");
        Window(8, 9);
        Show(Check(AppStatus.Available, auto: true, scope: InstallScope.User), Check(AppStatus.Available, "Example.Paint", auto: true, scope: InstallScope.User));
        Assert.Equal(["Example.Editor"], Enqueued);
        _vm.InstallChanged(Item(InstallStage.Installing));
        Pass(TimeSpan.FromHours(1));
        _vm.InstallChanged(Done(UpgradeResult.Updated));
        Assert.Equal(["Example.Editor"], Enqueued);
        Assert.Empty(_installer.Cancelled);
        Assert.Equal("Installs automatically at 08:00", Row("Example Paint").View.Status);
    }

    [Fact]
    public void SecurityFixes_ComeFirst_WhileTheSwitchIsOn()
    {
        Show(Check(AppStatus.Available), Check(AppStatus.Available, "Example.Paint", text: SecurityText));
        Assert.Equal(["Example Paint", "Example Editor"], _vm.Updates.Select(r => r.Name));
        Assert.True(Row("Example Paint").View.Security);
        _settings.Update(f => f with { Settings = f.Settings with { SecurityFirst = false } });
        _vm.ConditionsChanged();
        Assert.Equal(["Example Editor", "Example Paint"], _vm.Updates.Select(r => r.Name));
        Assert.False(Row("Example Paint").View.Security);
    }

    [Fact]
    public void WhatsNewLinksOff_HideTheLink_ButNotTheMenuItem()
    {
        _settings.Update(f => f with { Settings = f.Settings with { ShowWhatsNew = false } });
        Show(Check(AppStatus.Available));
        Assert.Equal((false, true), (Row("Example Editor").View.ShowNotes, Row("Example Editor").View.HasNotes));
    }

    private void Window(int from, int to) =>
        _settings.Update(f => f with { Settings = f.Settings with { InstallWindowEnabled = true, InstallWindowFrom = from, InstallWindowTo = to } });

    [Fact]
    public async Task AutoApp_DuringAGame_IsCheckedEachMinute_OnlyWhileItWaits()
    {
        TrackAuto("Example.Editor");
        _conditions.State = _conditions.State with { FullScreen = true };
        Show(Check(AppStatus.Available, auto: true, scope: InstallScope.User));
        Assert.Equal("2 days ago", Row("Example Editor").View.Status);
        Pass(UpdatesViewModel.FullScreenRecheck);
        Assert.Empty(_installer.Enqueued);
        _conditions.State = _conditions.State with { FullScreen = false };
        Pass(UpdatesViewModel.FullScreenRecheck);
        Assert.Equal(["Example.Editor"], Enqueued);
        await Saved();
        var reads = _conditions.Reads;
        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(0, _ui.Pump());
        Assert.Equal(reads, _conditions.Reads);
    }

    [Fact]
    public void AutoTurnedOn_InstallsRightAway()
    {
        Show(Check(AppStatus.Available, scope: InstallScope.User));
        Assert.Empty(_installer.Enqueued);
        Row("Example Editor").ToggleAutoCommand.Execute(null);
        Assert.Equal(["Example.Editor"], Enqueued);
    }

    [Fact]
    public void CancelledAutoInstall_WaitsTwelveHoursBeforeTheNextTry()
    {
        TrackAuto("Example.Editor");
        Show(Check(AppStatus.Available, auto: true, scope: InstallScope.User));
        _vm.InstallChanged(Item(InstallStage.Waiting));
        _vm.InstallChanged(Done(UpgradeResult.Cancelled));
        Assert.Single(_installer.Enqueued);
        _time.Advance(AutoInstallRules.AttemptCooldown);
        _vm.ConditionsChanged();
        Assert.Equal(["Example.Editor", "Example.Editor"], Enqueued);
    }

    [Fact]
    public void FailedAutoInstall_ForALastingReason_IsNotTriedAgainByItself()
    {
        TrackAuto("Example.Editor");
        Show(Check(AppStatus.Available, auto: true, scope: InstallScope.User));
        _vm.InstallChanged(Done(UpgradeResult.Failed, failure: UpgradeFailure.DiskFull));
        _time.Advance(AutoInstallRules.AttemptCooldown + TimeSpan.FromHours(1));
        _vm.ConditionsChanged();
        Assert.Single(_installer.Enqueued);
        Assert.Equal(RowState.Failed, Row("Example Editor").View.State);
    }

    [Fact]
    public void FailedAutoInstall_ForAPassingReason_IsTriedAgainAfterTheCooldown()
    {
        TrackAuto("Example.Editor");
        Show(Check(AppStatus.Available, auto: true, scope: InstallScope.User));
        _vm.InstallChanged(Done(UpgradeResult.Failed, failure: UpgradeFailure.NoNetwork));
        _time.Advance(AutoInstallRules.AttemptCooldown - TimeSpan.FromMinutes(1));
        _vm.ConditionsChanged();
        Assert.Single(_installer.Enqueued);
        Assert.Equal(RowState.Failed, Row("Example Editor").View.State);
        _time.Advance(TimeSpan.FromMinutes(1));
        _vm.ConditionsChanged();
        Assert.Equal(["Example.Editor", "Example.Editor"], Enqueued);
    }

    [Fact]
    public void FailedManualInstallOfAnAutoApp_IsNotTriedAgainByItself()
    {
        TrackAuto("Example.Editor");
        _settings.Update(f => f with { Settings = f.Settings with { AutoInstallWaitDays = 3 } });
        Show(Check(AppStatus.Available, auto: true, scope: InstallScope.User));
        Row("Example Editor").PrimaryCommand.Execute(null);
        _vm.InstallChanged(Done(UpgradeResult.Failed, failure: UpgradeFailure.NoNetwork));
        _time.Advance(TimeSpan.FromDays(4));
        _vm.ConditionsChanged();
        Assert.Single(_installer.Enqueued);
        Assert.Equal(RowState.Failed, Row("Example Editor").View.State);
    }

    [Fact]
    public void VersionJustInstalled_IsNotInstalledAgain_FromACheckThatReadTheAppsBefore()
    {
        TrackAuto("Example.Editor");
        Show(Check(AppStatus.Available, auto: true, scope: InstallScope.User));
        _vm.InstallChanged(Item(InstallStage.Downloading));
        _vm.InstallChanged(Done(UpgradeResult.Updated, after: Check(AppStatus.UpToDate, installed: "2.5.0", offer: null)));
        Show(Check(AppStatus.Available, auto: true, scope: InstallScope.User, newVersion: true));
        Pass(UpdatesViewModel.UpdatedShownFor);
        Assert.Single(_installer.Enqueued);
        Show(Check(AppStatus.UpToDate, installed: "2.5.0", offer: null, auto: true, scope: InstallScope.User));
        Show(Check(AppStatus.Available, installed: "2.4.1", auto: true, scope: InstallScope.User, newVersion: true));
        Assert.Equal(["Example.Editor", "Example.Editor"], Enqueued);
    }

    [Fact]
    public async Task RowUntrackedByACheck_LeavesNoTimerBehind()
    {
        Show(Check(AppStatus.NotFound));
        Row("Example Editor").StopTrackingCommand.Execute(null);
        await Saved();
        _vm.CheckFinished(Checked() with { Untracked = [new PackageKey("Example.Editor", "winget")] });
        _time.Advance(UpdatesViewModel.UndoShownFor);
        Assert.Equal(0, _ui.Pump());
    }

    [Fact]
    public void AutoAppRemovedWhileInstalling_StillLetsTheNextOneGo()
    {
        TrackAuto("Example.Editor", "Example.Paint");
        Show(Check(AppStatus.Available, auto: true, scope: InstallScope.User), Check(AppStatus.Available, "Example.Paint", auto: true, scope: InstallScope.User));
        _vm.InstallChanged(Item(InstallStage.Downloading));
        Row("Example Editor").StopTrackingCommand.Execute(null);
        Pass(UpdatesViewModel.UndoShownFor);
        _vm.InstallChanged(Done(UpgradeResult.Cancelled));
        Assert.Equal(["Example.Editor", "Example.Paint"], Enqueued);
    }

    [Fact]
    public void NoAutoInstall_StartsWhileACheckRuns()
    {
        Show(Check(AppStatus.Available, scope: InstallScope.User));
        _vm.CheckStarted();
        Row("Example Editor").ToggleAutoCommand.Execute(null);
        Assert.Empty(_installer.Enqueued);
        _vm.CheckFinished(Checked(Check(AppStatus.Available, auto: true, scope: InstallScope.User)));
        Assert.Equal(["Example.Editor"], Enqueued);
    }

    [Fact]
    public void NoAutoInstall_StartsWhileTheSchedulerRunsACheck()
    {
        TrackAuto("Example.Editor");
        _conditions.State = _conditions.State with { Metered = true };
        Show(Check(AppStatus.Available, auto: true, scope: InstallScope.User));
        _conditions.State = _conditions.State with { Metered = false };
        // Opening the flyout starts a check; the page hears of it a UI turn later.
        _checksEndAtOnce = false;
        _vm.FlyoutOpened();
        _vm.ConditionsChanged();
        Assert.Empty(_installer.Enqueued);
        _scheduler.Finished(new CheckTicket(1, CheckTrigger.FlyoutOpened), succeeded: true);
        _vm.CheckFinished(Checked(Check(AppStatus.Available, auto: true, scope: InstallScope.User)));
        Assert.Equal(["Example.Editor"], Enqueued);
    }

    [Fact]
    public void NoAutoInstall_AfterACheckThatFailed()
    {
        TrackAuto("Example.Editor");
        _conditions.State = _conditions.State with { Metered = true };
        Show(Check(AppStatus.Available, auto: true, scope: InstallScope.User));
        _vm.CheckFinished(Failed(CheckProblem.WinGetUnreachable));
        _conditions.State = _conditions.State with { Metered = false };
        _vm.ConditionsChanged();
        Assert.Empty(_installer.Enqueued);
        Show(Check(AppStatus.Available, auto: true, scope: InstallScope.User));
        Assert.Equal(["Example.Editor"], Enqueued);
    }

    [Fact]
    public void CancelledManualInstallOfAnAutoApp_WaitsTheCooldownToo()
    {
        TrackAuto("Example.Editor");
        _conditions.State = _conditions.State with { Metered = true };
        Show(Check(AppStatus.Available, auto: true, scope: InstallScope.User));
        Row("Example Editor").PrimaryCommand.Execute(null);
        _vm.InstallChanged(Item(InstallStage.Waiting));
        _conditions.State = _conditions.State with { Metered = false };
        _vm.InstallChanged(Done(UpgradeResult.Cancelled));
        Assert.Single(_installer.Enqueued);
    }

    [Fact]
    public async Task AutoTurnedOnThatCantBeSaved_EndsOff()
    {
        Show(Check(AppStatus.Available, scope: InstallScope.User));
        File.Delete(_folder.PathOf("settings.json"));
        Directory.CreateDirectory(_folder.PathOf("settings.json"));
        Row("Example Editor").ToggleAutoCommand.Execute(null);
        await Saved();
        Assert.False(Row("Example Editor").Auto);
    }

    [Fact]
    public void AppThatStoppedBeingTracked_LeavesTheList()
    {
        Show(Check(AppStatus.NotFound, offer: null), Check(AppStatus.Available, "Example.Paint"));
        _settings.Update(f => f with { Apps = [.. f.Apps.Where(a => a.Id != "Example.Editor")] });
        _vm.CheckFinished(Checked(Check(AppStatus.Available, "Example.Paint")) with { Untracked = [new PackageKey("Example.Editor", "winget")] });
        Assert.Equal(["Example Paint"], _vm.Updates.Select(r => r.Name));
    }

    [Fact]
    public void TimerAfterDispose_DoesNothing()
    {
        Show(Check(AppStatus.Available));
        _vm.InstallChanged(Done(UpgradeResult.Updated, after: Check(AppStatus.UpToDate, installed: "2.5.0", offer: null)));
        _vm.Dispose();
        Pass(UpdatesViewModel.UpdatedShownFor);
        Assert.Equal(RowState.Updated, Row("Example Editor").View.State);
    }
}
