using TinyTracker.Core.Installing;
using TinyTracker.Core.SelfUpdate;
using TinyTracker.Core.Tracking;
using TinyTracker.Presentation.Updates;
using Xunit;
using static TinyTracker.Presentation.Tests.Fixtures;

namespace TinyTracker.Presentation.Tests.Updates;

// Tiny Tracker's own row, in each of its states (spec §4.3).
public class SelfUpdateViewTests
{
    private static readonly SelfVersion Running = new(0, 1, 0);
    private static readonly SelfRelease Release = new(new SelfVersion(0, 2, 0), Now.AddDays(-2));

    private static SelfUpdateView View(SelfUpdateStage stage, UpgradeOutcome? outcome = null, AutoBlock auto = AutoBlock.AutoOff, int daysLeft = 0) =>
        SelfUpdateView.Of(new SelfUpdateState(stage, Release) { Outcome = outcome }, Running, auto, daysLeft)!;

    [Fact]
    public void NothingNewer_HasNoRow() => Assert.Null(SelfUpdateView.Of(new SelfUpdateState(SelfUpdateStage.None, null), Running));

    [Fact]
    public void Available_NamesTheVersion_ShowsTheChange_AndWhatsNew()
    {
        var view = View(SelfUpdateStage.Available);
        Assert.Equal(("Tiny Tracker 0.2.0", "0.1.0 → 0.2.0", RowAction.Update, "Update"), (view.Title, view.Status, view.Action, view.ActionText));
        Assert.True(view.ShowNotes);
        Assert.False(view.ShowProgress || view.CanCancel || view.Warning || view.IsActive);
    }

    [Fact]
    public void DeclinedPrompt_SaysSo_AndOffersUpdateAgain()
    {
        var view = SelfUpdateView.Of(new SelfUpdateState(SelfUpdateStage.Available, Release) { Declined = true }, Running)!;
        Assert.Equal(("Permission was declined", RowAction.Update), (view.Status, view.Action));
        Assert.False(view.ShowNotes);
    }

    [Fact]
    public void WhatsNewLinksOff_LeaveNoLink() =>
        Assert.False(SelfUpdateView.Of(new SelfUpdateState(SelfUpdateStage.Available, Release), Running, whatsNew: false)!.ShowNotes);

    [Fact]
    public void UpdateThatInstallsByItself_OutsideTheWindow_SaysWhen() =>
        Assert.Equal("0.1.0 → 0.2.0 · Installs automatically at 22:00",
            SelfUpdateView.Of(new SelfUpdateState(SelfUpdateStage.Available, Release), Running, AutoBlock.OutsideWindow, windowStart: "22:00")!.Status);

    // With the switch and silent mode on, it installs by itself; meanwhile the row says why it waits, as an Auto app's does.
    [Theory]
    [InlineData(AutoBlock.TooNew, 2, "0.1.0 → 0.2.0 · Installs automatically in 2 days")]
    [InlineData(AutoBlock.Offline, 0, "0.1.0 → 0.2.0 · Waits for the network")]
    [InlineData(AutoBlock.Metered, 0, "0.1.0 → 0.2.0 · Waits for an unmetered connection")]
    [InlineData(AutoBlock.BatterySaver, 0, "0.1.0 → 0.2.0 · Waits for Energy saver to turn off")]
    [InlineData(AutoBlock.FullScreen, 0, "0.1.0 → 0.2.0")]
    [InlineData(AutoBlock.NeedsAdmin, 0, "0.1.0 → 0.2.0")]
    [InlineData(AutoBlock.RecentlyAttempted, 0, "0.1.0 → 0.2.0")]
    public void UpdateThatInstallsByItself_SaysWhyItWaits(AutoBlock auto, int daysLeft, string status)
    {
        var view = View(SelfUpdateStage.Available, auto: auto, daysLeft: daysLeft);
        Assert.Equal((status, RowAction.Update), (view.Status, view.Action));
        Assert.True(view.ShowNotes);
    }

    [Fact]
    public void WaitingForTheOtherUpdates_CanBeCancelled()
    {
        var view = View(SelfUpdateStage.WaitsForOthers);
        Assert.Equal(("Waits for the other updates", RowAction.None), (view.Status, view.Action));
        Assert.True(view.CanCancel && view.IsActive);
    }

    [Fact]
    public void WaitingForPermission_CanBeCancelled()
    {
        var view = View(SelfUpdateStage.AwaitingPermission);
        Assert.Equal(("Waiting for permission…", RowAction.None), (view.Status, view.Action));
        Assert.True(view.CanCancel && view.IsActive);
    }

    [Fact]
    public void Downloading_ShowsItsProgressAndSpeed()
    {
        var state = new SelfUpdateState(SelfUpdateStage.Downloading, Release) { Progress = Downloading(180 * MB, 400 * MB), BytesPerSecond = 20 * MB };
        var view = SelfUpdateView.Of(state, Running)!;
        Assert.Equal(($"{Nb("180 of 400 MB")} · {Nb("20 MB/s")}", "45%", 45.0), (view.Status, view.PercentText, view.Percent));
        Assert.True(view.ShowProgress && view.CanCancel && view.IsActive);
        Assert.False(view.Indeterminate || view.HasAction);
    }

    [Fact]
    public void DownloadThatHasntBegun_Waits()
    {
        var view = View(SelfUpdateStage.Downloading);
        Assert.Equal(("Waiting…", (string?)null), (view.Status, view.PercentText));
        Assert.True(view.ShowProgress && view.Indeterminate && view.CanCancel);
    }

    [Fact]
    public void Installing_SaysItWillRestart_AndCantBeCancelled()
    {
        var view = View(SelfUpdateStage.Installing);
        Assert.Equal(("Installing… Tiny Tracker will restart", RowAction.None), (view.Status, view.Action));
        Assert.True(view.ShowProgress && view.Indeterminate && view.IsActive);
        Assert.False(view.CanCancel);
    }

    [Fact]
    public void CopyForAnotherAccount_AsksToCloseIt_ThenRetry()
    {
        var view = View(SelfUpdateStage.Failed, new UpgradeOutcome(UpgradeResult.Failed, UpgradeFailure.OtherAccounts));
        Assert.Equal(("Close Tiny Tracker for the other accounts on this PC, then try again", RowAction.Retry, "Retry"), (view.Status, view.Action, view.ActionText));
        Assert.True(view.Warning);
        Assert.Null(view.Details);
    }

    [Theory]
    [InlineData(UpgradeResult.Failed, UpgradeFailure.DigestMismatch, "Couldn't update Tiny Tracker · The download didn't match GitHub's record")]
    [InlineData(UpgradeResult.Failed, UpgradeFailure.GitHubUnreachable, "Couldn't update Tiny Tracker · Couldn't reach GitHub")]
    [InlineData(UpgradeResult.Failed, UpgradeFailure.ReleaseRefused, "Couldn't update Tiny Tracker · The release on GitHub couldn't be verified")]
    [InlineData(UpgradeResult.Failed, UpgradeFailure.InstallerFailed, "Couldn't update Tiny Tracker · The installer reported an error")]
    [InlineData(UpgradeResult.Busy, UpgradeFailure.None, "Couldn't update Tiny Tracker · Another install is running")]
    [InlineData(UpgradeResult.Failed, UpgradeFailure.Other, "Couldn't update Tiny Tracker")]
    public void Failure_SaysWhy_WithRetryAndItsCode(UpgradeResult result, UpgradeFailure failure, string status)
    {
        var view = View(SelfUpdateStage.Failed, new UpgradeOutcome(result, failure, "0x80070005"));
        Assert.Equal((status, RowAction.Retry, "Code: 0x80070005"), (view.Status, view.Action, view.Details));
        Assert.True(view.Warning);
        Assert.False(view.ShowNotes || view.CanCancel);
    }
}
