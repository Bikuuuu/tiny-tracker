using Microsoft.Extensions.Time.Testing;
using TinyTracker.Core.Installing;
using TinyTracker.Core.Logging;
using TinyTracker.Core.SelfUpdate;
using TinyTracker.Core.Settings;
using TinyTracker.Core.Storage;
using TinyTracker.Core.Tracking;
using TinyTracker.Presentation.Settings;
using TinyTracker.Presentation.Shell;
using Xunit;
using static TinyTracker.Presentation.Tests.Fixtures;

namespace TinyTracker.Presentation.Tests.Shell;

// Tiny Tracker's own toasts (spec §4.7): an update that waits for the click, once per version, and one it installed by itself.
public sealed class AnnouncerSelfUpdateTests : IAsyncDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly SelfRelease Release = new(new SelfVersion(0, 2, 0), Now.AddDays(-1));
    private static readonly SelfUpdateState Available = new(SelfUpdateStage.Available, Release);

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

    public AnnouncerSelfUpdateTests()
    {
        _settings = new SettingsStore(_folder.PathOf("settings.json"));
        _log = new FileLog(_folder.PathOf("app.log"), _time);
        _writer = new SettingsWriter(_settings, _log, _ui.Post);
        _announcer = Start();
    }

    public async ValueTask DisposeAsync()
    {
        await _writer.Idle.WaitAsync(Wait);
        _announcer.Dispose();
        _log.Close();
        _folder.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Announcer Start() => new(_toasts, _conditions, _settings, _writer, () => _flyoutOpen, _time, _ui.Post);

    private async Task Saved()
    {
        await _writer.Idle.WaitAsync(Wait, Ct);
        _ui.Pump();
    }

    [Fact]
    public async Task UpdateThatWaitsForTheClick_IsAnnouncedOnce()
    {
        _announcer.SelfUpdateChanged(Available);
        var toast = Assert.Single(_toasts.Shown);
        Assert.Equal(("self", "Tiny Tracker 0.2.0 is available", ""), (toast.Kind, toast.Title, toast.Body));
        Assert.Equal([("Update", ToastAction.SelfUpdate), ("Later", ToastAction.Later)], toast.Buttons.Select(b => (b.Text, b.Action)));
        _announcer.SelfUpdateChanged(Available);
        Assert.Single(_toasts.Shown);
        await Saved();
        Assert.Equal("0.2.0", _settings.Current.SelfUpdate.Announced);
    }

    [Fact]
    public void AnnouncedVersion_IsNoNewsAfterTheNextStart()
    {
        _settings.Update(f => f with { SelfUpdate = new SelfUpdateBook { Announced = "0.2.0" } });
        using var announcer = Start();
        announcer.SelfUpdateChanged(Available);
        Assert.Empty(_toasts.Shown);
    }

    [Fact]
    public void NewerVersion_IsNewsAgain()
    {
        _settings.Update(f => f with { SelfUpdate = new SelfUpdateBook { Announced = "0.2.0" } });
        _announcer.SelfUpdateChanged(Available with { Release = Release with { Version = new SelfVersion(0, 3, 0) } });
        Assert.Equal("Tiny Tracker 0.3.0 is available", Assert.Single(_toasts.Shown).Title);
    }

    // With the switch and silent mode on it installs by itself, so it isn't news (spec §6.5).
    [Fact]
    public void UpdateThatInstallsByItself_IsNoNews()
    {
        _settings.Update(f => f with { Settings = f.Settings with { SilentMode = true } });
        _announcer.SelfUpdateChanged(Available);
        Assert.Empty(_toasts.Shown);
    }

    [Fact]
    public void SwitchOff_MakesItNews_EvenInSilentMode()
    {
        _settings.Update(f => f with { Settings = f.Settings with { SilentMode = true, AutoSelfUpdate = false } });
        _announcer.SelfUpdateChanged(Available);
        Assert.Single(_toasts.Shown);
    }

    // Silent mode or the switch going off, say with silent mode's task gone, leaves the update to the click: news then.
    [Fact]
    public void UpdateThatNoLongerInstallsByItself_IsNewsThen()
    {
        _settings.Update(f => f with { Settings = f.Settings with { SilentMode = true } });
        _announcer.SelfUpdateChanged(Available);
        Assert.Empty(_toasts.Shown);
        _settings.Update(f => f with { Settings = f.Settings with { SilentMode = false } });
        _announcer.AutoRulesChanged();
        Assert.Equal("self", Assert.Single(_toasts.Shown).Kind);
    }

    [Theory]
    [InlineData(SelfUpdateStage.WaitsForOthers)]
    [InlineData(SelfUpdateStage.AwaitingPermission)]
    [InlineData(SelfUpdateStage.Downloading)]
    [InlineData(SelfUpdateStage.Installing)]
    [InlineData(SelfUpdateStage.Failed)]
    public void UpdateThatsUnderWay_OrFailed_IsNoNews(SelfUpdateStage stage)
    {
        _announcer.SelfUpdateChanged(new SelfUpdateState(stage, Release));
        Assert.Empty(_toasts.Shown);
    }

    [Fact]
    public void DeclinedPrompt_IsNoNews()
    {
        _announcer.SelfUpdateChanged(Available with { Declined = true });
        Assert.Empty(_toasts.Shown);
    }

    [Fact]
    public async Task NoToast_WhileTheFlyoutIsOpen_AndWhatItShowedCountsAsSeen()
    {
        _flyoutOpen = true;
        _announcer.SelfUpdateChanged(Available);
        Assert.Empty(_toasts.Shown);
        await Saved();
        Assert.Equal("0.2.0", _settings.Current.SelfUpdate.Announced);
    }

    [Fact]
    public async Task OpeningTheFlyout_CountsItsRowAsSeen_AndWithdrawsTheToasts()
    {
        _settings.Update(f => f with { Settings = f.Settings with { SilentMode = true } });
        _announcer.SelfUpdateChanged(Available);
        _announcer.FlyoutOpened();
        await Saved();
        Assert.Equal("0.2.0", _settings.Current.SelfUpdate.Announced);
        Assert.Contains("self", _toasts.Hidden);
        Assert.Contains("selfUpdated", _toasts.Hidden);
    }

    [Fact]
    public void Toast_WaitsDuringAGame()
    {
        _conditions.State = _conditions.State with { FullScreen = true };
        _announcer.SelfUpdateChanged(Available);
        Assert.Empty(_toasts.Shown);
        _conditions.State = _conditions.State with { FullScreen = false };
        _time.Advance(Announcer.BusyRecheck);
        _ui.Pump();
        Assert.Equal("self", Assert.Single(_toasts.Shown).Kind);
    }

    [Fact]
    public void NotificationsOff_ShowNothing()
    {
        Level(NotificationLevel.Off);
        _announcer.SelfUpdateChanged(Available);
        _announcer.SelfUpdated(new SelfVersion(0, 2, 0));
        _announcer.SelfUpdateChanged(Failed(UpgradeFailure.DigestMismatch));
        Assert.Empty(_toasts.Shown);
    }

    // An automatic update that failed for a reason that won't pass by itself says so, once (spec §4.7).
    [Fact]
    public void AutomaticUpdateThatFailedForGood_IsToldOnce()
    {
        var failed = Failed(UpgradeFailure.DigestMismatch);
        _announcer.SelfUpdateChanged(failed);
        _announcer.SelfUpdateChanged(failed);
        _announcer.AutoRulesChanged();
        var toast = Assert.Single(_toasts.Shown);
        Assert.Equal(("selfFailed", "Couldn't update Tiny Tracker", "The download didn't match GitHub's record"), (toast.Kind, toast.Title, toast.Body));
        Assert.Equal([("View", ToastAction.View)], toast.Buttons.Select(b => (b.Text, b.Action)));
    }

    // After a restart, the check that follows brings GitHub's own release date: still the same failure.
    [Fact]
    public void SameFailure_WithAnotherReleaseDate_IsToldOnce()
    {
        var failed = Failed(UpgradeFailure.DigestMismatch);
        _announcer.SelfUpdateChanged(failed);
        _announcer.SelfUpdateChanged(failed with { Release = Release with { PublishedAt = Release.PublishedAt.AddDays(-3) } });
        Assert.Single(_toasts.Shown);
    }

    [Fact]
    public void AnotherVersionsFailure_IsToldToo()
    {
        _announcer.SelfUpdateChanged(Failed(UpgradeFailure.DigestMismatch));
        _announcer.SelfUpdateChanged(Failed(UpgradeFailure.DigestMismatch) with { Release = new SelfRelease(new SelfVersion(0, 3, 0), Now) });
        Assert.Equal(["selfFailed", "selfFailed"], _toasts.Shown.Select(t => t.Kind));
    }

    [Fact]
    public void FailureHeldByAGame_ShowsOnceItEnds()
    {
        _conditions.Away = true;
        _announcer.SelfUpdateChanged(Failed(UpgradeFailure.DigestMismatch));
        Assert.Empty(_toasts.Shown);
        _conditions.Away = false;
        _time.Advance(Announcer.BusyRecheck);
        _ui.Pump();
        _time.Advance(Announcer.BusyRecheck);
        _ui.Pump();
        Assert.Equal(["selfFailed"], _toasts.Shown.Select(t => t.Kind));
    }

    // A retry while the toast waits makes it old news.
    [Fact]
    public void FailureHeldByAGame_IsDroppedByARetry()
    {
        _conditions.Away = true;
        _announcer.SelfUpdateChanged(Failed(UpgradeFailure.DigestMismatch));
        _announcer.SelfUpdateChanged(new SelfUpdateState(SelfUpdateStage.WaitsForOthers, Release));
        _conditions.Away = false;
        _time.Advance(Announcer.BusyRecheck);
        _ui.Pump();
        Assert.Empty(_toasts.Shown);
    }

    // What a level left out counts as seen.
    [Fact]
    public void FailureLeftOutByALevel_DoesntComeBackLater()
    {
        Level(NotificationLevel.Off);
        _announcer.SelfUpdateChanged(Failed(UpgradeFailure.DigestMismatch));
        Level(NotificationLevel.All);
        _announcer.AutoRulesChanged();
        Assert.Empty(_toasts.Shown);
    }

    [Fact]
    public void OtherAccounts_SaysWhatToDo() =>
        Assert.Equal("Close Tiny Tracker for the other accounts on this PC, then try again",
            Shown(Failed(UpgradeFailure.OtherAccounts)).Body);

    // One that can pass is tried again after 12 h; a clicked one shows in the flyout the user is in.
    [Theory]
    [InlineData(UpgradeFailure.GitHubUnreachable, true)]
    [InlineData(UpgradeFailure.DigestMismatch, false)]
    public void FailureThatCanPass_OrAClickedOne_IsntTold(UpgradeFailure failure, bool automatic)
    {
        _announcer.SelfUpdateChanged(Failed(failure, automatic));
        Assert.Empty(_toasts.Shown);
    }

    [Fact]
    public void OnlyFailures_ShowsTheFailure_NotTheOffer()
    {
        Level(NotificationLevel.Failures);
        _announcer.SelfUpdateChanged(Available);
        _announcer.SelfUpdateChanged(Failed(UpgradeFailure.DigestMismatch));
        Assert.Equal(["selfFailed"], _toasts.Shown.Select(t => t.Kind));
    }

    [Fact]
    public void NeedsMe_LeavesOutTheUpdatedToast()
    {
        Level(NotificationLevel.NeedsMe);
        _announcer.SelfUpdated(new SelfVersion(0, 2, 0));
        _announcer.SelfUpdateChanged(Available);
        Assert.Equal(["self"], _toasts.Shown.Select(t => t.Kind));
    }

    private static SelfUpdateState Failed(UpgradeFailure failure, bool automatic = true) =>
        new(SelfUpdateStage.Failed, Release) { Outcome = new UpgradeOutcome(UpgradeResult.Failed, failure), Automatic = automatic };

    private Toast Shown(SelfUpdateState state)
    {
        _announcer.SelfUpdateChanged(state);
        return Assert.Single(_toasts.Shown);
    }

    private void Level(NotificationLevel level) => _settings.Update(f => f with { Settings = f.Settings with { Notifications = level } });

    // The restarted app says so after an update it installed by itself (spec §4.7).
    [Fact]
    public void UpdateItInstalledByItself_IsToldOnce()
    {
        _announcer.SelfUpdated(new SelfVersion(0, 2, 0));
        var toast = Assert.Single(_toasts.Shown);
        Assert.Equal(("selfUpdated", "Tiny Tracker was updated to 0.2.0"), (toast.Kind, toast.Title));
        Assert.Equal([("What's new", ToastAction.WhatsNew)], toast.Buttons.Select(b => (b.Text, b.Action)));
        _announcer.SelfUpdateChanged(new SelfUpdateState(SelfUpdateStage.None, null));
        Assert.Single(_toasts.Shown);
    }

    [Fact]
    public void UpdatedToast_WaitsDuringAGame_Too()
    {
        _conditions.Away = true;
        _announcer.SelfUpdated(new SelfVersion(0, 2, 0));
        Assert.Empty(_toasts.Shown);
        _conditions.Away = false;
        _time.Advance(Announcer.BusyRecheck);
        _ui.Pump();
        Assert.Equal("selfUpdated", Assert.Single(_toasts.Shown).Kind);
    }

    private sealed class FakeToasts : IToasts
    {
        public List<Toast> Shown { get; } = [];
        public List<string> Hidden { get; } = [];

        public void Show(Toast toast) => Shown.Add(toast);

        public void Hide(string kind) => Hidden.Add(kind);
    }
}
