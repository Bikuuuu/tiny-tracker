using TinyTracker.Core.Installing;
using TinyTracker.Core.Tracking;
using TinyTracker.Presentation.Updates;
using Xunit;
using static TinyTracker.Presentation.Tests.Fixtures;

namespace TinyTracker.Presentation.Tests.Updates;

// Every row state of spec §4.3: its status line, action and progress.
public class RowViewTests
{
    private static RowView View(AppCheck check, InstallItem? install = null) => RowView.Of(check, install, Now);

    [Fact]
    public void Available_ShowsItsAge_AnUpdateButton_AndWhatsNew()
    {
        var view = View(Check(AppStatus.Available));
        Assert.Equal((RowState.Available, "2 days ago", RowAction.Update, "Update"), (view.State, view.Status, view.Action, view.ActionText));
        Assert.True(view.ShowNotes);
        Assert.Equal(("2.4.1", "2.", "5.0"), (view.From, view.To.Unchanged, view.To.Changed));
        Assert.True(view.ShowVersions && view.CanUpdateNow && view.CanSkip && view.HasNotes);
        Assert.False(view.ShowProgress || view.CanCancel || view.Warning);
    }

    [Fact]
    public void Available_UsesTheReleaseDateWhenKnown()
    {
        var check = Check(AppStatus.Available);
        check = check with { App = check.App with { Offer = check.App.Offer! with { ReleaseDate = DateOnly.FromDateTime(Now.UtcDateTime).AddDays(-5) } } };
        Assert.Equal("5 days ago", View(check).Status);
    }

    [Theory]
    [InlineData(AutoBlock.TooNew, 1, "Installs automatically in 1\u00a0day")]
    [InlineData(AutoBlock.TooNew, 3, "Installs automatically in 3\u00a0days")]
    [InlineData(AutoBlock.Metered, 0, "Waits for an unmetered connection")]
    [InlineData(AutoBlock.BatterySaver, 0, "Waits for Energy saver to turn off")]
    [InlineData(AutoBlock.FullScreen, 0, "2 days ago")]
    [InlineData(AutoBlock.RecentlyAttempted, 0, "2 days ago")]
    public void AutoApp_SaysWhyItWaits(AutoBlock block, int daysLeft, string status)
    {
        var view = RowView.Of(Check(AppStatus.Available, auto: true), null, Now, block, daysLeft);
        Assert.Equal((RowState.Available, status, RowAction.Update), (view.State, view.Status, view.Action));
        Assert.True(view.ShowNotes);
        Assert.False(view.Warning);
    }

    [Fact]
    public void AutoAppThatNeedsAdmin_NeedsPermission_AndOffersInstall()
    {
        var view = RowView.Of(Check(AppStatus.Available, auto: true), null, Now, AutoBlock.NeedsAdmin);
        Assert.Equal((RowState.NeedsPermission, "Needs admin permission", RowAction.Install, "Install"), (view.State, view.Status, view.Action, view.ActionText));
        Assert.True(view.ShowNotes && view.CanUpdateNow && view.CountsForUpdateAll && view.IsPending);
        Assert.False(view.Warning || view.IsActive);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DeclinedPromptForAnInstall_KeepsItsInstall(bool auto)
    {
        var request = Request() with { Route = InstallRoute.Helper, NeedsAdmin = true };
        var declined = new InstallItem(request, InstallStage.Done) { Done = new InstallDone(new UpgradeOutcome(UpgradeResult.PermissionDeclined)) };
        var view = View(Check(AppStatus.Available, auto: auto), declined);
        Assert.Equal((RowState.NeedsPermission, "Permission was declined", RowAction.Install), (view.State, view.Status, view.Action));
        Assert.False(view.ShowNotes);
    }

    [Fact]
    public void Available_WithoutNotes_HidesWhatsNew()
    {
        var view = View(Check(AppStatus.Available, notes: null));
        Assert.False(view.ShowNotes || view.HasNotes);
    }

    [Fact]
    public void Available_WithOnlyTheNotesText_ShowsWhatsNew()
    {
        var view = View(Check(AppStatus.Available, notes: null, text: "- Tabs"));
        Assert.True(view.ShowNotes && view.HasNotes);
    }

    // Show What's new links off: the status line leaves it out, the "…" menu keeps it (spec §4.3).
    [Fact]
    public void WhatsNewLinksOff_LeaveTheMenuItem()
    {
        var view = RowView.Of(Check(AppStatus.Available), null, Now, whatsNew: false);
        Assert.False(view.ShowNotes);
        Assert.True(view.HasNotes);
    }

    [Fact]
    public void SecurityFix_GetsThePill()
    {
        Assert.True(View(Check(AppStatus.Available, text: SecurityText)).Security);
        Assert.False(View(Check(AppStatus.Available, text: "- Faster start.")).Security);
    }

    [Fact]
    public void SecurityFix_WithTheSwitchOff_HasNoPill() =>
        Assert.False(RowView.Of(Check(AppStatus.Available, text: SecurityText), null, Now, securityFirst: false).Security);

    [Fact]
    public void SkippedSecurityFix_HasNoPill() => Assert.False(View(Check(AppStatus.Skipped, skipped: "2.5.0", text: SecurityText)).Security);

    // Security fixes come right after the rows in progress (spec §4.3).
    [Fact]
    public void SecurityFix_RanksAfterTheRowsInProgress_AndBeforeTheRest()
    {
        var installing = View(Check(AppStatus.Available, text: SecurityText), Item(InstallStage.Installing)).Rank;
        var waiting = View(Check(AppStatus.Available), Item(InstallStage.Waiting)).Rank;
        var security = View(Check(AppStatus.Available, text: SecurityText)).Rank;
        var failed = View(Check(AppStatus.Available), Done(UpgradeResult.Failed)).Rank;
        var available = View(Check(AppStatus.Available)).Rank;
        Assert.True(installing < security && waiting < security && security < failed && failed < available, $"{installing} {waiting} {security} {failed} {available}");
    }

    [Fact]
    public void SecurityFix_NeedingPermission_KeepsThePill_AndItsPlace()
    {
        var view = View(Check(AppStatus.Available, text: SecurityText), Done(UpgradeResult.NeedsAdmin, code: "0x8A150019"));
        var failed = View(Check(AppStatus.Available), Done(UpgradeResult.Failed)).Rank;
        Assert.Equal((RowState.NeedsPermission, true), (view.State, view.Security));
        Assert.True(view.Rank < failed, $"{view.Rank} {failed}");
    }

    [Fact]
    public void AutoApp_OutsideTheInstallWindow_SaysWhenItInstalls()
    {
        var view = RowView.Of(Check(AppStatus.Available, auto: true), null, Now, AutoBlock.OutsideWindow, windowStart: "22:00");
        Assert.Equal((RowState.Available, "Installs automatically at 22:00", RowAction.Update), (view.State, view.Status, view.Action));
    }

    [Fact]
    public void Waiting_CanBeCancelled()
    {
        var view = View(Check(AppStatus.Available), Item(InstallStage.Waiting));
        Assert.Equal((RowState.Waiting, "Waiting…", RowAction.None), (view.State, view.Status, view.Action));
        Assert.True(view.CanCancel && view.IsActive);
        Assert.False(view.CanUpdateNow || view.CanSkip);
    }

    [Fact]
    public void WaitingForAnotherInstall_SaysSo() =>
        Assert.Equal("Waiting for another install to finish…", View(Check(AppStatus.Available), Item(InstallStage.Waiting, busy: true)).Status);

    [Fact]
    public void Downloading_ShowsTheAmountSpeedAndPercent()
    {
        var view = View(Check(AppStatus.Available), Item(InstallStage.Downloading, progress: Downloading(180 * MB, 400 * MB), speed: 20 * MB));
        Assert.Equal((RowState.Downloading, $"{Nb("180 of 400 MB")} · {Nb("20 MB/s")}", "45%"), (view.State, view.Status, view.PercentText));
        Assert.Equal(45, view.Percent, 3);
        Assert.True(view.ShowProgress && view.CanCancel);
        Assert.False(view.Indeterminate);
    }

    [Fact]
    public void Downloading_OfUnknownSize_HasNoPercent()
    {
        var view = View(Check(AppStatus.Available), Item(InstallStage.Downloading, progress: Downloading(180 * MB, 0)));
        Assert.Equal((Nb("180 MB"), null), (view.Status, view.PercentText));
        Assert.True(view.Indeterminate);
    }

    [Theory]
    [InlineData(0.0, true, 0.0)]
    [InlineData(0.5, false, 50.0)]
    public void Installing_ShowsProgressWhenKnown(double fraction, bool indeterminate, double percent)
    {
        var view = View(Check(AppStatus.Available), Item(InstallStage.Installing, progress: new UpgradeProgress(UpgradeStage.Installing, 0, 0, 1, fraction)));
        Assert.Equal((RowState.Installing, "Installing…", indeterminate, RowAction.None), (view.State, view.Status, view.Indeterminate, view.Action));
        Assert.Equal(percent, view.Percent, 3);
        Assert.False(view.CanCancel);
    }

    [Fact]
    public void AppInUse_NamesTheApp_AndOffersCloseAndUpdate()
    {
        var view = View(Check(AppStatus.Available), Done(UpgradeResult.AppInUse, code: "0x8A150101"));
        Assert.Equal((RowState.AppInUse, "Example Editor is running", RowAction.CloseAndUpdate, "Close & update", "Code: 0x8A150101"),
            (view.State, view.Status, view.Action, view.ActionText, view.Details));
        Assert.True(view.Warning && view.HasDetails && view.IsPending);
        Assert.False(view.CountsForUpdateAll);
    }

    [Fact]
    public void AppThatCantBeClosed_AsksTheUserToCloseIt_ThenRetry()
    {
        var view = View(Check(AppStatus.Available), Done(UpgradeResult.CouldNotClose, code: "0x8A150101"));
        Assert.Equal((RowState.CouldNotClose, "Close Example Editor, then try again", RowAction.Retry, "Code: 0x8A150101"), (view.State, view.Status, view.Action, view.Details));
        Assert.True(view.Warning && view.IsPending);
        Assert.False(view.CountsForUpdateAll || view.IsActive);
    }

    [Fact]
    public void Closing_SaysSo_WithNothingToClick()
    {
        var view = View(Check(AppStatus.Available), Item(InstallStage.Closing));
        Assert.Equal((RowState.Closing, "Closing Example Editor…", RowAction.None), (view.State, view.Status, view.Action));
        Assert.True(view.IsActive && view.IsPending && view.ShowVersions);
        Assert.False(view.CanCancel || view.CanSkip);
    }

    [Fact]
    public void AppThatDidntClose_OffersForceCloseAndCancel()
    {
        var view = View(Check(AppStatus.Available), Item(InstallStage.NotClosed));
        Assert.Equal((RowState.NotClosed, "Example Editor didn't close", RowAction.ForceClose, "Force close"), (view.State, view.Status, view.Action, view.ActionText));
        Assert.True(view.CanCancel && view.Warning && view.IsActive);
        Assert.Equal(0, view.Rank);
    }

    [Fact]
    public void WaitingForThePrompt_SaysSo() =>
        Assert.Equal("Waiting for permission…", View(Check(AppStatus.Available), Item(InstallStage.Waiting) with { AwaitingPermission = true }).Status);

    [Fact]
    public void WinGetsNeedsAdminAnswer_OffersInstall()
    {
        var view = View(Check(AppStatus.Available), Done(UpgradeResult.NeedsAdmin, code: "0x8A150019"));
        Assert.Equal((RowState.NeedsPermission, "Needs admin permission", RowAction.Install, "Code: 0x8A150019"), (view.State, view.Status, view.Action, view.Details));
        Assert.True(view.CountsForUpdateAll);
    }

    [Theory]
    [InlineData(UpgradeResult.Failed, UpgradeFailure.DiskFull, "Not enough disk space")]
    [InlineData(UpgradeResult.Failed, UpgradeFailure.Stalled, "Download stalled")]
    [InlineData(UpgradeResult.Failed, UpgradeFailure.TookTooLong, "Took too long")]
    [InlineData(UpgradeResult.Busy, UpgradeFailure.None, "Another install is running")]
    [InlineData(UpgradeResult.NoUpdate, UpgradeFailure.None, "This version isn't offered anymore")]
    public void Failed_GivesTheReason_Retry_AndDetails(UpgradeResult result, UpgradeFailure failure, string reason)
    {
        var view = View(Check(AppStatus.Available), Done(result, failure: failure, code: "0x8A150105"));
        Assert.Equal((RowState.Failed, reason, RowAction.Retry, "Code: 0x8A150105"), (view.State, view.Status, view.Action, view.Details));
        Assert.True(view.CountsForUpdateAll && view.CanUpdateNow && view.CanSkip);
    }

    [Fact]
    public void RestartNeeded_HasNoAction()
    {
        var view = View(Check(AppStatus.Available), Done(UpgradeResult.RestartNeeded));
        Assert.Equal((RowState.RestartNeeded, "Restart to finish", RowAction.None), (view.State, view.Status, view.Action));
        Assert.False(view.IsPending || view.CanSkip);
    }

    [Fact]
    public void Updated_ShowsTheNewVersion_WhileTheCheckIsAlreadyUpToDate()
    {
        var view = View(Check(AppStatus.UpToDate, installed: "2.5.0", offer: null), Done(UpgradeResult.Updated));
        Assert.Equal((RowState.Updated, "Updated to 2.5.0", RowAction.None), (view.State, view.Status, view.Action));
        Assert.Equal(("2.4.1", "5.0"), (view.From, view.To.Changed));
        Assert.False(view.InUpToDateGroup);
    }

    [Fact]
    public void Phantom_OffersUpdateAnyway()
    {
        foreach (var view in new[] { View(Check(AppStatus.Available), Done(UpgradeResult.Updated, phantom: true)), View(Check(AppStatus.Phantom)) })
        {
            Assert.Equal((RowState.Phantom, "Installed, but Windows still reports the old version", RowAction.UpdateAnyway), (view.State, view.Status, view.Action));
            Assert.False(view.CountsForUpdateAll || view.IsPending);
        }
    }

    // App Installer is winget itself: Windows finishes its update once Tiny Tracker restarts, so there's nothing to click (spec §6.2).
    [Fact]
    public void AppInstallerPhantom_SaysToRestartTinyTracker()
    {
        const string appInstaller = "Microsoft.AppInstaller";
        foreach (var view in new[] { View(Check(AppStatus.Available, appInstaller), Done(UpgradeResult.Updated, phantom: true)), View(Check(AppStatus.Phantom, appInstaller)) })
        {
            Assert.Equal((RowState.Phantom, "Can't finish while Tiny Tracker runs: quit it and open it again", RowAction.None), (view.State, view.Status, view.Action));
            Assert.False(view.CountsForUpdateAll || view.IsPending);
        }
    }

    [Fact]
    public void VersionUnknown_IsInTheUpToDateGroup()
    {
        var view = View(Check(AppStatus.VersionUnknown, installed: "Unknown", offer: null));
        Assert.Equal((RowState.VersionUnknown, "Version unknown · this app updates itself", RowAction.None), (view.State, view.Status, view.Action));
        Assert.True(view.InUpToDateGroup);
    }

    [Theory]
    [InlineData(AppStatus.NotFound, RowState.NotFound, "Not installed anymore")]
    [InlineData(AppStatus.NotInCatalog, RowState.NotInCatalog, "Not found in winget")]
    public void Missing_OffersStopTracking(AppStatus status, RowState state, string text)
    {
        var view = View(Check(status, offer: null));
        Assert.Equal((state, text, RowAction.StopTracking, "Stop tracking"), (view.State, view.Status, view.Action, view.ActionText));
        Assert.False(view.ShowVersions || view.InUpToDateGroup);
    }

    [Fact]
    public void UpToDate_ShowsTheInstalledVersion()
    {
        var view = View(Check(AppStatus.UpToDate, installed: "2.5.0", offer: null));
        Assert.Equal((RowState.UpToDate, "2.5.0", RowAction.None), (view.State, view.Status, view.Action));
        Assert.True(view.InUpToDateGroup);
        Assert.False(view.ShowVersions || view.CanSkip || view.CanUpdateNow);
    }

    [Fact]
    public void Skipped_CanBeUndone()
    {
        var view = View(Check(AppStatus.Skipped, skipped: "2.5.0"));
        Assert.Equal((RowState.Skipped, "Skipped version 2.5.0"), (view.State, view.Status));
        Assert.True(view.CanUndoSkip && view.CanUpdateNow && view.InUpToDateGroup);
        Assert.False(view.CanSkip || view.CountsForUpdateAll);
    }

    [Fact]
    public void PermissionDeclined_ReturnsToAvailable_WithANote()
    {
        var view = View(Check(AppStatus.Available), Done(UpgradeResult.PermissionDeclined));
        Assert.Equal((RowState.Available, "Permission was declined", RowAction.Update), (view.State, view.Status, view.Action));
        Assert.False(view.ShowNotes);
    }

    [Fact]
    public void Cancelled_ReturnsToAvailable() =>
        Assert.Equal((RowState.Available, "2 days ago"), (View(Check(AppStatus.Available), Done(UpgradeResult.Cancelled)).State, View(Check(AppStatus.Available), Done(UpgradeResult.Cancelled)).Status));

    [Fact]
    public void NotInstalledResult_IsNotFound() =>
        Assert.Equal(RowState.NotFound, View(Check(AppStatus.Available), Done(UpgradeResult.NotInstalled)).State);

    [Fact]
    public void Rank_PutsWorkFirst_ThenAttention_ThenAvailable()
    {
        var available = View(Check(AppStatus.Available));
        var failed = View(Check(AppStatus.Available), Done(UpgradeResult.Failed, failure: UpgradeFailure.Other));
        var waiting = View(Check(AppStatus.Available), Item(InstallStage.Waiting));
        var downloading = View(Check(AppStatus.Available), Item(InstallStage.Downloading, progress: Downloading(1, 2)));
        Assert.Equal([downloading, waiting, failed, available], new[] { available, failed, waiting, downloading }.OrderBy(v => v.Rank));
    }
}
