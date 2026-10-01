using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using TinyTracker.Core;
using TinyTracker.Core.Checking;
using TinyTracker.Core.Elevation;
using TinyTracker.Core.Installing;
using TinyTracker.Core.Launch;
using TinyTracker.Core.Logging;
using TinyTracker.Core.Scheduling;
using TinyTracker.Core.Settings;
using TinyTracker.Core.Storage;
using TinyTracker.Core.Tracking;
using TinyTracker.Presentation.Settings;
using Xunit;
using static TinyTracker.Presentation.Tests.Fixtures;

namespace TinyTracker.Presentation.Tests.Settings;

public sealed class SettingsViewModelTests : IAsyncDisposable
{
    private const string Exe = @"C:\Program Files\Tiny Tracker\TinyTracker.exe";
    private const string SilentHelp = "Admin updates install silently. Needs one approval to turn on.";
    private const string LimitHelp = "Caps how fast updates download. Needs one approval the first time.";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly TempFolder _folder = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly TestUi _ui = new();
    private readonly FakeStartupValues _startup = new();
    private readonly FakeDesktop _desktop = new();
    private readonly FakePackages _packages = new();
    private readonly FakeShortcutKeys _keys = new();
    private readonly FakeSilentMode _silent = new();
    private readonly FakeProxyOption _proxy = new();
    private readonly SpeedLimit _limit = new();
    private readonly CheckScheduler _scheduler;
    private readonly SettingsStore _settings;
    private readonly FileLog _log;
    private readonly SettingsWriter _writer;
    private readonly SettingsViewModel _vm;

    public SettingsViewModelTests()
    {
        _scheduler = new CheckScheduler(_time, TimeSpan.FromHours(6));
        _settings = new SettingsStore(SettingsPath);
        _settings.Update(f => f with { Apps = [App("Example.Editor"), App("Example.Paint")] });
        _log = new FileLog(_folder.PathOf("app.log"), _time);
        _writer = new SettingsWriter(_settings, _log, _ui.Post);
        _vm = Create();
    }

    // Saves a test left queued land, and later log lines are dropped, before the folder goes.
    public async ValueTask DisposeAsync()
    {
        await _writer.Idle.WaitAsync(Wait);
        _vm.Dispose();
        _scheduler.Dispose();
        _log.Close();
        _folder.Dispose();
    }

    private string SettingsPath => _folder.PathOf("settings.json");

    private SettingsViewModel Create(bool installed = false, Func<CultureInfo>? culture = null) => new(_settings, _writer, _scheduler, new StartupEntry(_startup, Exe), _desktop,
        _packages, _keys, _silent, _proxy, _limit, _time, _log, _ui.Post, () => (Now, CheckProblem.None, Now), "0.1.0", _folder.PathOf("logs"), installed,
        culture ?? (() => CultureInfo.InvariantCulture));

    private void SilentModeStored(bool on) => _settings.Update(f => f with { Settings = f.Settings with { SilentMode = on } });

    private void LimitStored(bool on, int kbps = AppSettings.DefaultSpeedLimitKBps) =>
        _settings.Update(f => f with { Settings = f.Settings with { SpeedLimitEnabled = on, SpeedLimitKBps = kbps } });

    private string LogText() => File.ReadAllText(_folder.PathOf("app.log"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task Saved()
    {
        await _writer.Idle.WaitAsync(Wait, Ct);
        _ui.Pump();
    }

    private async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (true)
        {
            _ui.Pump();
            if (condition()) return;
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting.");
            await Task.Delay(5, Ct);
        }
    }

    // Written behind the store's back, so any save would show.
    private void PlantMarker() => File.WriteAllText(SettingsPath, "{ \"marker\": true }");

    private bool MarkerKept() => File.ReadAllText(SettingsPath).Contains("marker", StringComparison.Ordinal);

    private void BlockSaves()
    {
        File.Delete(SettingsPath);
        Directory.CreateDirectory(SettingsPath);
    }

    [Fact]
    public async Task Open_ShowsTheStoredValues_AndWritesNothing()
    {
        _settings.Update(f => f with { Settings = f.Settings with { CheckIntervalHours = 12 } });
        _startup.Run = StartupCommand.Format(Exe);
        PlantMarker();
        _vm.Open();
        await Saved();
        Assert.Equal(("12 hours", true, "Choose apps, 2 apps tracked"), (_vm.IntervalChoices[_vm.IntervalIndex], _vm.StartWithWindows, _vm.ChooseAppsName));
        Assert.Equal(0, _startup.Writes);
        Assert.True(MarkerKept());
    }

    [Fact]
    public async Task OpenAfterTaskManagerTurnedTheEntryOff_ShowsOff_AndWritesNothing()
    {
        _startup.Run = StartupCommand.Format(Exe);
        _vm.Open();
        _startup.Approved = [3, 0, 0, 0];
        _vm.Open();
        await Saved();
        Assert.False(_vm.StartWithWindows);
        Assert.Equal(0, _startup.Writes);
        Assert.NotNull(_startup.Approved);
    }

    [Fact]
    public void AppName_ComesFromOnePlace() =>
        Assert.Equal(("Update Tiny Tracker automatically", "v0.1.0", "Support Tiny Tracker"), (_vm.AutoSelfUpdateText, _vm.VersionText, _vm.SupportText));

    [Fact]
    public async Task IntervalChange_IsSaved_AndTimesTheChecks()
    {
        _vm.Open();
        _vm.IntervalIndex = 0;
        await Saved();
        Assert.Equal(1, _settings.Current.Settings.CheckIntervalHours);
        Assert.Equal(TimeSpan.FromHours(1), _scheduler.Interval);
        Assert.Empty(_vm.Notices);
    }

    [Fact]
    public async Task IntervalThatCantBeSaved_SnapsBack_AndKeepsTheChecksAsTheyWere()
    {
        _vm.Open();
        BlockSaves();
        _vm.IntervalIndex = 0;
        await Saved();
        Assert.Equal("6 hours", _vm.IntervalChoices[_vm.IntervalIndex]);
        Assert.Equal(TimeSpan.FromHours(6), _scheduler.Interval);
        var notice = Assert.Single(_vm.Notices);
        Assert.Equal((NoticeKind.SaveFailed, "Code: 0x80070005"), (notice.Kind, notice.Details));
    }

    [Fact]
    public async Task OlderRefusal_DoesNotUndoANewerChoiceStillBeingSaved()
    {
        _vm.Open();
        using var gate = new ManualResetEventSlim();
        using var between = new ManualResetEventSlim();
        try
        {
            BlockSaves();
            _vm.IntervalIndex = 0;
            _writer.Update(file =>
            {
                Directory.Delete(SettingsPath);
                between.Set();
                gate.Wait(Wait);
                return file;
            });
            _vm.IntervalIndex = 4;
            Assert.True(between.Wait(Wait, Ct));
            _ui.Pump();
            Assert.Equal("24 hours", _vm.IntervalChoices[_vm.IntervalIndex]);
            Assert.Equal(TimeSpan.FromHours(6), _scheduler.Interval);
            Assert.Equal(NoticeKind.SaveFailed, Assert.Single(_vm.Notices).Kind);
        }
        finally
        {
            gate.Set();
        }
        await Saved();
        Assert.Equal("24 hours", _vm.IntervalChoices[_vm.IntervalIndex]);
        Assert.Equal(24, _settings.Current.Settings.CheckIntervalHours);
        Assert.Equal(TimeSpan.FromHours(24), _scheduler.Interval);
    }

    [Fact]
    public async Task NoSelection_IsIgnored()
    {
        _vm.Open();
        PlantMarker();
        _vm.IntervalIndex = -1;
        await Saved();
        Assert.True(MarkerKept());
    }

    [Fact]
    public void StartWithWindows_TurnsTheEntryOnAndOff()
    {
        _vm.Open();
        _vm.StartWithWindows = true;
        _ui.Pump();
        Assert.Equal(StartupCommand.Format(Exe), _startup.Run);
        Assert.True(_vm.StartWithWindows);
        _vm.StartWithWindows = false;
        _ui.Pump();
        Assert.Null(_startup.Run);
        Assert.False(_vm.StartWithWindows);
    }

    [Fact]
    public void StartWithWindowsThatFails_SnapsBackAfterTheSwitch_AndExplains()
    {
        _vm.Open();
        _startup.Fail = new UnauthorizedAccessException("denied");
        _vm.StartWithWindows = true;
        Assert.True(_vm.StartWithWindows);
        _ui.Pump();
        Assert.False(_vm.StartWithWindows);
        Assert.Equal(1, _startup.Writes);
        var notice = Assert.Single(_vm.Notices);
        Assert.Equal((NoticeKind.StartupNotChanged, "Code: 0x80070005"), (notice.Kind, notice.Details));
    }

    [Fact]
    public void StartWithWindowsThatCantBeRead_ShowsOff_AndIsLogged()
    {
        _startup.ReadFail = new System.Security.SecurityException("locked by policy");
        _vm.Open();
        Assert.False(_vm.StartWithWindows);
        Assert.Contains("WARN Start with Windows not read: locked by policy", File.ReadAllText(_folder.PathOf("app.log")));
    }

    // Only an installed copy updates itself (spec §6.5).
    [Fact]
    public void SelfUpdateOfACopyThatIsntInstalled_ShowsOff_EvenWhenStoredOn()
    {
        _vm.Open();
        Assert.True(_settings.Current.Settings.AutoSelfUpdate);
        Assert.Equal((false, false, "Works once Tiny Tracker is installed"), (_vm.AutoSelfUpdateAvailable, _vm.AutoSelfUpdate, _vm.AutoSelfUpdateDescription));
    }

    [Fact]
    public async Task SelfUpdateSwitch_OfAnInstalledCopy_IsSaved_AndRunsTheRulesAgain()
    {
        using var vm = Create(installed: true);
        var rules = 0;
        vm.AutoRulesChanged += (_, _) => rules++;
        vm.Open();
        Assert.True(vm.AutoSelfUpdateAvailable && vm.AutoSelfUpdate);
        vm.AutoSelfUpdate = false;
        await Saved();
        Assert.False(_settings.Current.Settings.AutoSelfUpdate);
        Assert.Equal(1, rules);
    }

    [Fact]
    public async Task SelfUpdateSwitch_ShowsWhatsStored_AndWritesNothing()
    {
        _settings.Update(f => f with { Settings = f.Settings with { AutoSelfUpdate = false } });
        using var vm = Create(installed: true);
        PlantMarker();
        vm.Open();
        await Saved();
        Assert.False(vm.AutoSelfUpdate);
        Assert.True(MarkerKept());
    }

    // Without silent mode, installing needs a prompt, so it waits for the click (spec §4.5).
    [Fact]
    public void WithoutSilentMode_TheSwitchSaysUpdatesWaitForTheClick()
    {
        using var vm = Create(installed: true);
        vm.Open();
        Assert.Equal("Updates wait for your click without silent mode", vm.AutoSelfUpdateDescription);
        SilentModeStored(true);
        vm.Open();
        Assert.Equal("", vm.AutoSelfUpdateDescription);
    }

    [Fact]
    public async Task TurningSilentModeOn_TakesTheNoteAway()
    {
        using var vm = Create(installed: true);
        vm.Open();
        vm.SilentMode = true;
        _silent.Answer.SetResult((SwitchResult.Done, null));
        await Until(() => _settings.Current.Settings.SilentMode);
        await Saved();
        Assert.Equal("", vm.AutoSelfUpdateDescription);
    }

    [Fact]
    public async Task Open_ShowsTheSpeedLimitAsStored_AndWritesNothing()
    {
        LimitStored(true, 2000);
        _proxy.On = true;
        PlantMarker();
        _vm.Open();
        await Until(() => _proxy.Reads == 1);
        await Saved();
        Assert.True(_vm.SpeedLimitAvailable && _vm.SpeedLimitEnabled && _vm.CanChangeSpeedLimit && _vm.ShowsSpeedLimitBox);
        Assert.Equal((2000.0, Nb("≈ 1.95 MB/s"), LimitHelp), (_vm.SpeedLimitValue, _vm.SpeedLimitMBps, _vm.SpeedLimitDescription));
        Assert.True(MarkerKept());
    }

    [Fact]
    public async Task TurningTheLimitOn_WhileWinGetsOptionIsOn_SavesIt_WithNoPrompt()
    {
        _proxy.On = true;
        _vm.Open();
        _vm.SpeedLimitEnabled = true;
        Assert.False(_vm.CanChangeSpeedLimit);
        await Until(() => _settings.Current.Settings.SpeedLimitEnabled && _vm.CanChangeSpeedLimit);
        await Saved();
        Assert.Equal((0, false, 17500), (_proxy.TurnOns, _settings.Current.TurnedOnProxyOption, _limit.KBps));
        Assert.True(_vm.SpeedLimitEnabled && _vm.ShowsSpeedLimitBox);
        Assert.Equal((17500.0, Nb("≈ 17.1 MB/s"), LimitHelp), (_vm.SpeedLimitValue, _vm.SpeedLimitMBps, _vm.SpeedLimitDescription));
    }

    [Fact]
    public async Task TurningTheLimitOn_TakesOnePrompt_ThenSavesIt()
    {
        _vm.Open();
        _vm.SpeedLimitEnabled = true;
        await Until(() => _vm.SpeedLimitDescription == "Waiting for permission…");
        Assert.Equal((false, false, false), (_vm.CanChangeSpeedLimit, _vm.ShowsSpeedLimitBox, _settings.Current.Settings.SpeedLimitEnabled));
        _proxy.Answer.SetResult((SwitchResult.Done, null));
        await Until(() => _settings.Current.Settings.SpeedLimitEnabled && _vm.CanChangeSpeedLimit);
        await Saved();
        Assert.Equal((1, true, 17500), (_proxy.TurnOns, _settings.Current.TurnedOnProxyOption, _limit.KBps));
        Assert.Equal((true, true, LimitHelp), (_vm.SpeedLimitEnabled, _vm.ShowsSpeedLimitBox, _vm.SpeedLimitDescription));
    }

    // Silent mode's task starts the helper, with no prompt to wait for.
    [Fact]
    public async Task InSilentMode_TurningTheLimitOn_WaitsForNoPermission()
    {
        _proxy.Prompts = false;
        _vm.Open();
        _vm.SpeedLimitEnabled = true;
        await Until(() => _proxy.TurnOns == 1);
        _ui.Pump();
        Assert.Equal(LimitHelp, _vm.SpeedLimitDescription);
        _proxy.Answer.SetResult((SwitchResult.Done, null));
        await Until(() => _vm.CanChangeSpeedLimit);
    }

    [Fact]
    public async Task DeclinedPrompt_TurnsTheLimitBackOff_AndSaysSo()
    {
        _proxy.Answer.SetResult((SwitchResult.Declined, null));
        _vm.Open();
        _vm.SpeedLimitEnabled = true;
        await Until(() => _vm.CanChangeSpeedLimit);
        await Saved();
        Assert.Equal((false, false, "Permission was declined", 0), (_vm.SpeedLimitEnabled, _settings.Current.Settings.SpeedLimitEnabled, _vm.SpeedLimitDescription, _limit.KBps));
        Assert.Empty(_vm.Notices);
        _vm.Open();
        Assert.Equal(LimitHelp, _vm.SpeedLimitDescription);
    }

    [Fact]
    public async Task LimitThatCouldntBeTurnedOn_SaysWhy()
    {
        _proxy.Answer.SetResult((SwitchResult.Failed, "still off for this user"));
        _vm.Open();
        _vm.SpeedLimitEnabled = true;
        await Until(() => _vm.CanChangeSpeedLimit);
        await Saved();
        Assert.False(_vm.SpeedLimitEnabled || _settings.Current.Settings.SpeedLimitEnabled);
        var notice = Assert.Single(_vm.Notices);
        Assert.Equal(("The speed limit couldn't be turned on", "still off for this user", true), (notice.Title, notice.Details, notice.Closable));
        Assert.Contains("WARN The speed limit wasn't turned on: still off for this user", LogText());
    }

    // The uninstaller turns winget's option off only where Tiny Tracker turned it on (spec §10).
    [Fact]
    public async Task OptionTheHelperTurnedOn_IsStillSaved_AfterASaveFailed()
    {
        _vm.Open();
        BlockSaves();
        _proxy.Answer.SetResult((SwitchResult.Done, null));
        _vm.SpeedLimitEnabled = true;
        await Until(() => _vm.CanChangeSpeedLimit);
        await Saved();
        Assert.False(_vm.SpeedLimitEnabled);
        Assert.Equal(NoticeKind.SaveFailed, Assert.Single(_vm.Notices).Kind);
        Directory.Delete(SettingsPath);
        _proxy.On = true;
        _vm.SpeedLimitEnabled = true;
        await Until(() => _settings.Current.Settings.SpeedLimitEnabled && _vm.CanChangeSpeedLimit);
        await Saved();
        Assert.Equal((1, true), (_proxy.TurnOns, _settings.Current.TurnedOnProxyOption));
    }

    // winget couldn't say, so the option may have been on already: then it isn't Tiny Tracker's to turn off.
    [Fact]
    public async Task OptionThatCouldntBeReadFirst_IsntCountedAsTurnedOn()
    {
        _proxy.On = null;
        _proxy.Answer.SetResult((SwitchResult.Done, null));
        _vm.Open();
        _vm.SpeedLimitEnabled = true;
        await Until(() => _settings.Current.Settings.SpeedLimitEnabled && _vm.CanChangeSpeedLimit);
        await Saved();
        Assert.Equal((1, false), (_proxy.TurnOns, _settings.Current.TurnedOnProxyOption));
    }

    [Fact]
    public async Task TurningTheLimitOff_NeedsNoPrompt_AndTheDownloadsFollow()
    {
        LimitStored(true);
        _limit.Set(17500);
        _proxy.On = true;
        _vm.Open();
        _vm.SpeedLimitEnabled = false;
        await Saved();
        Assert.Equal((false, 0, 0, false), (_settings.Current.Settings.SpeedLimitEnabled, _limit.KBps, _proxy.TurnOns, _vm.ShowsSpeedLimitBox));
    }

    [Fact]
    public async Task Box_SavesWholeKilobytes_AndTheDownloadsFollow()
    {
        LimitStored(true);
        _proxy.On = true;
        _vm.Open();
        _vm.SpeedLimitValue = 2500.4;
        await Saved();
        Assert.Equal((2500, 2500, 2500.0, Nb("≈ 2.44 MB/s")), (_settings.Current.Settings.SpeedLimitKBps, _limit.KBps, _vm.SpeedLimitValue, _vm.SpeedLimitMBps));
    }

    [Theory]
    [InlineData(50, 100)]
    [InlineData(5_000_000, 1_000_000)]
    public async Task BoxOutsideTheRange_SnapsToTheNearestEnd(double typed, int kept)
    {
        LimitStored(true);
        _proxy.On = true;
        _vm.Open();
        _vm.SpeedLimitValue = typed;
        await Saved();
        Assert.Equal((kept, (double)kept), (_settings.Current.Settings.SpeedLimitKBps, _vm.SpeedLimitValue));
    }

    [Fact]
    public async Task BoxThatIsntANumber_GoesBackToTheSavedValue()
    {
        LimitStored(true, 3000);
        _proxy.On = true;
        _vm.Open();
        await Until(() => _proxy.Reads == 1);
        await Saved();
        PlantMarker();
        _vm.SpeedLimitValue = double.NaN;
        await Saved();
        Assert.Equal(3000.0, _vm.SpeedLimitValue);
        Assert.True(MarkerKept());
    }

    [Fact]
    public async Task Box_KeepsTheLastValue_WhileAnEarlierOneIsStillSaving()
    {
        LimitStored(true);
        _proxy.On = true;
        _vm.Open();
        await Until(() => _proxy.Reads == 1);
        await Saved();
        using var gate = new ManualResetEventSlim();
        try
        {
            _writer.Update(file =>
            {
                gate.Wait(Wait);
                return file;
            });
            _vm.SpeedLimitValue = 2000;
            _vm.SpeedLimitValue = 17500;
        }
        finally
        {
            gate.Set();
        }
        await Saved();
        Assert.Equal((17500, 17500, 17500.0), (_settings.Current.Settings.SpeedLimitKBps, _limit.KBps, _vm.SpeedLimitValue));
    }

    [Theory]
    [InlineData(SpeedLimitAvailability.NotAdmin, "Needs an administrator account")]
    [InlineData(SpeedLimitAvailability.Blocked, "Blocked by your organization's policy")]
    public void SpeedLimit_IsGreyedWithWhy_WhereItCantWork(SpeedLimitAvailability availability, string why)
    {
        LimitStored(true);
        _proxy.Availability = availability;
        using var vm = Create();
        vm.Open();
        Assert.Equal((false, false, false, false, why), (vm.SpeedLimitAvailable, vm.CanChangeSpeedLimit, vm.SpeedLimitEnabled, vm.ShowsSpeedLimitBox, vm.SpeedLimitDescription));
    }

    [Theory]
    [InlineData(SpeedLimitAvailability.NotAdmin)]
    [InlineData(SpeedLimitAvailability.Blocked)]
    public async Task SpeedLimitLeftOn_WhereItCantWork_IsSavedOff(SpeedLimitAvailability availability)
    {
        LimitStored(true);
        _proxy.Availability = availability;
        using var vm = Create();
        vm.KeepSpeedLimitHonest();
        await Saved();
        Assert.False(_settings.Current.Settings.SpeedLimitEnabled);
        Assert.Equal(0, _proxy.Reads);
        Assert.Empty(vm.Notices);
    }

    [Fact]
    public async Task ProxyOptionTurnedOffElsewhere_TurnsTheLimitOff_AndSaysWhy()
    {
        LimitStored(true);
        _limit.Set(17500);
        _vm.KeepSpeedLimitHonest();
        await Until(() => !_settings.Current.Settings.SpeedLimitEnabled);
        await Saved();
        _vm.Open();
        Assert.Equal((false, 0), (_vm.SpeedLimitEnabled, _limit.KBps));
        var notice = Assert.Single(_vm.Notices);
        Assert.Equal(("The speed limit was turned off because winget's proxy option is off", NoticeSeverity.Warning, true), (notice.Title, notice.Severity, notice.Closable));
        Assert.Contains("WARN winget's proxy option is off, so the speed limit is off", LogText());
    }

    // The queue saw winget refuse the limit's proxy during an update.
    [Fact]
    public async Task ProxyRefusedDuringAnUpdate_TurnsTheLimitOff_Too()
    {
        LimitStored(true);
        _vm.SpeedLimitOptionOff();
        await Saved();
        Assert.False(_settings.Current.Settings.SpeedLimitEnabled);
        Assert.Equal(NoticeKind.SpeedLimitOptionOff, Assert.Single(_vm.Notices).Kind);
    }

    [Fact]
    public async Task OptionWinGetCantTellAbout_LeavesTheLimitOn()
    {
        LimitStored(true);
        _proxy.On = null;
        _vm.KeepSpeedLimitHonest();
        await Until(() => _proxy.Reads == 1);
        await Saved();
        Assert.True(_settings.Current.Settings.SpeedLimitEnabled);
        Assert.Empty(_vm.Notices);
    }

    [Fact]
    public async Task WithTheLimitOff_WinGetIsntAsked()
    {
        _vm.KeepSpeedLimitHonest();
        _vm.SpeedLimitOptionOff();
        await Saved();
        Assert.Equal(0, _proxy.Reads);
        Assert.Empty(_vm.Notices);
    }

    [Fact]
    public async Task Open_ShowsSilentModeAsStored_AndWritesNothing()
    {
        SilentModeStored(true);
        PlantMarker();
        _vm.Open();
        await Until(() => _silent.TaskChecks == 1);
        await Saved();
        Assert.True(_vm.SilentModeAvailable && _vm.SilentMode && _vm.CanChangeSilentMode);
        Assert.Equal(SilentHelp, _vm.SilentModeDescription);
        Assert.True(MarkerKept());
    }

    [Fact]
    public async Task TurningSilentModeOn_WaitsForPermission_ThenSavesIt_AndRunsTheAutoRulesAgain()
    {
        var rules = 0;
        _vm.AutoRulesChanged += (_, _) => rules++;
        _vm.Open();
        _vm.SilentMode = true;
        Assert.Equal(("Waiting for permission…", false), (_vm.SilentModeDescription, _vm.CanChangeSilentMode));
        await Until(() => _silent.Changes.Count == 1);
        // Showing the page again leaves a change that waits alone.
        _vm.Open();
        Assert.Equal((true, "Waiting for permission…"), (_vm.SilentMode, _vm.SilentModeDescription));
        Assert.False(_settings.Current.Settings.SilentMode);
        _silent.Answer.SetResult((SwitchResult.Done, null));
        await Until(() => rules == 1);
        Assert.True(_settings.Current.Settings.SilentMode && _vm.SilentMode && _vm.CanChangeSilentMode);
        Assert.Equal(SilentHelp, _vm.SilentModeDescription);
        Assert.Equal([true], _silent.Changes);
    }

    [Fact]
    public async Task DeclinedPrompt_TurnsSilentModeBackOff_AndSaysSo_UntilThePageShowsAgain()
    {
        _silent.Answer.SetResult((SwitchResult.Declined, null));
        _vm.Open();
        _vm.SilentMode = true;
        await Until(() => _vm.CanChangeSilentMode);
        Assert.Equal((false, false, "Permission was declined"), (_vm.SilentMode, _settings.Current.Settings.SilentMode, _vm.SilentModeDescription));
        Assert.Empty(_vm.Notices);
        _vm.Open();
        Assert.Equal(SilentHelp, _vm.SilentModeDescription);
    }

    [Theory]
    [InlineData(false, "Silent mode couldn't be turned on")]
    [InlineData(true, "Silent mode couldn't be turned off")]
    public async Task SwitchThatFails_ShowsWhatTheFileHolds_AndWhy(bool stored, string title)
    {
        SilentModeStored(stored);
        _silent.Answer.SetResult((SwitchResult.Failed, "0x80070005"));
        _vm.Open();
        _vm.SilentMode = !stored;
        await Until(() => _vm.CanChangeSilentMode);
        Assert.Equal((stored, stored), (_vm.SilentMode, _settings.Current.Settings.SilentMode));
        var notice = Assert.Single(_vm.Notices);
        Assert.Equal((title, "0x80070005", true), (notice.Title, notice.Details, notice.Closable));
        Assert.Contains($"WARN Silent mode not turned {(stored ? "off" : "on")}: 0x80070005", LogText());
    }

    [Fact]
    public async Task TurningSilentModeOff_WaitsForNoPrompt_AndSavesItOff()
    {
        SilentModeStored(true);
        var rules = 0;
        _vm.AutoRulesChanged += (_, _) => rules++;
        _vm.Open();
        _vm.SilentMode = false;
        Assert.Equal((SilentHelp, false), (_vm.SilentModeDescription, _vm.CanChangeSilentMode));
        _silent.Answer.SetResult((SwitchResult.Done, null));
        await Until(() => rules == 1);
        Assert.False(_settings.Current.Settings.SilentMode || _vm.SilentMode);
        Assert.Equal([false], _silent.Changes);
    }

    [Theory]
    [InlineData(SilentModeAvailability.NotInstalled, "Works once Tiny Tracker is installed")]
    [InlineData(SilentModeAvailability.NotAdmin, "Needs an administrator account")]
    public void SilentMode_IsGreyedWithWhy_WhereItCantWork(SilentModeAvailability availability, string why)
    {
        SilentModeStored(true);
        _silent.Availability = availability;
        using var vm = Create();
        vm.Open();
        Assert.Equal((false, false, false, why), (vm.SilentModeAvailable, vm.CanChangeSilentMode, vm.SilentMode, vm.SilentModeDescription));
    }

    // Such as an account that's no longer an admin's: the task would start the helper unelevated, and every admin update would fail.
    [Theory]
    [InlineData(SilentModeAvailability.NotInstalled)]
    [InlineData(SilentModeAvailability.NotAdmin)]
    public async Task SilentModeLeftOn_WhereItCantWork_IsSavedOff(SilentModeAvailability availability)
    {
        SilentModeStored(true);
        _silent.Availability = availability;
        using var vm = Create();
        var rules = 0;
        vm.AutoRulesChanged += (_, _) => rules++;
        vm.KeepSilentModeHonest();
        await Until(() => rules == 1);
        Assert.False(_settings.Current.Settings.SilentMode);
        Assert.Equal(0, _silent.TaskChecks);
        Assert.Empty(vm.Notices);
    }

    [Fact]
    public async Task MissingTask_TurnsSilentModeOff_AndSaysWhy()
    {
        SilentModeStored(true);
        _silent.TaskExists = false;
        var rules = 0;
        _vm.AutoRulesChanged += (_, _) => rules++;
        _vm.KeepSilentModeHonest();
        await Until(() => rules == 1);
        _vm.Open();
        Assert.False(_settings.Current.Settings.SilentMode || _vm.SilentMode);
        var notice = Assert.Single(_vm.Notices);
        Assert.Equal(("Silent mode was turned off because its task is missing", NoticeSeverity.Warning, true), (notice.Title, notice.Severity, notice.Closable));
        Assert.Contains("WARN Silent mode's task is missing", LogText());
    }

    [Fact]
    public async Task TaskThatDidntStartAtAClick_TurnsSilentModeOff_Too()
    {
        SilentModeStored(true);
        _vm.SilentModeTaskMissing();
        await Saved();
        Assert.False(_settings.Current.Settings.SilentMode);
        Assert.Equal(NoticeKind.SilentModeTaskMissing, Assert.Single(_vm.Notices).Kind);
    }

    [Fact]
    public async Task WithSilentModeOff_NoTaskIsLookedFor()
    {
        _vm.Open();
        _vm.KeepSilentModeHonest();
        _vm.SilentModeTaskMissing();
        await Saved();
        Assert.Equal(0, _silent.TaskChecks);
        Assert.Empty(_vm.Notices);
    }

    [Fact]
    public async Task MissingTask_WhileTheSwitchChanges_IsLeftToTheChange()
    {
        SilentModeStored(true);
        _vm.Open();
        _vm.SilentMode = false;
        _vm.SilentModeTaskMissing();
        _vm.KeepSilentModeHonest();
        _silent.Answer.SetResult((SwitchResult.Done, null));
        await Until(() => _vm.CanChangeSilentMode);
        await Saved();
        Assert.False(_settings.Current.Settings.SilentMode);
        Assert.Empty(_vm.Notices);
    }

    [Fact]
    public async Task Open_ShowsTheStoredWaitAndGamePause_AndWritesNothing()
    {
        _settings.Update(f => f with { Settings = f.Settings with { AutoInstallWaitDays = 3, PauseDuringGames = false } });
        PlantMarker();
        _vm.Open();
        await Saved();
        Assert.Equal(("3 days", false), (_vm.WaitChoices[_vm.WaitIndex], _vm.PauseDuringGames));
        Assert.True(MarkerKept());
    }

    [Fact]
    public async Task WaitDays_AreSaved_AndTheAutoRulesRunAgain()
    {
        var changed = 0;
        _vm.AutoRulesChanged += (_, _) => changed++;
        _vm.Open();
        _vm.WaitIndex = 3;
        await Saved();
        Assert.Equal(7, _settings.Current.Settings.AutoInstallWaitDays);
        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task GamePause_IsSaved_AndTheAutoRulesRunAgain()
    {
        var changed = 0;
        _vm.AutoRulesChanged += (_, _) => changed++;
        _vm.Open();
        Assert.True(_vm.PauseDuringGames);
        _vm.PauseDuringGames = false;
        await Saved();
        Assert.False(_settings.Current.Settings.PauseDuringGames);
        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task NotificationLevel_IsSaved_AndSaysWhatItShows()
    {
        var changed = 0;
        _vm.AutoRulesChanged += (_, _) => changed++;
        _vm.Open();
        Assert.Equal((0, "Updates ready, requests to allow or close, and results."), (_vm.NotificationIndex, _vm.NotificationsDescription));
        _vm.NotificationIndex = 2;
        await Saved();
        Assert.Equal(NotificationLevel.Failures, _settings.Current.Settings.Notifications);
        Assert.Equal("Only updates that failed.", _vm.NotificationsDescription);
        Assert.Equal(0, changed);
    }

    [Fact]
    public void NotificationChoices_AreTheFourLevels() => Assert.Equal(["All", "When I need to act", "Only failures", "Off"], _vm.NotificationChoices);

    // Update apps automatically (spec §4.5).
    [Fact]
    public async Task AutoUpdateApps_IsSaved_AndTheRulesRunAgain()
    {
        var changed = 0;
        _vm.AutoRulesChanged += (_, _) => changed++;
        _vm.Open();
        Assert.False(_vm.AutoUpdateApps);
        _vm.AutoUpdateApps = true;
        await Saved();
        Assert.True(_settings.Current.Settings.AutoUpdateApps);
        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task Open_ShowsTheStoredAutoSwitch_AndWritesNothing()
    {
        _settings.Update(f => f with { Settings = f.Settings with { AutoUpdateApps = true } });
        PlantMarker();
        _vm.Open();
        await Saved();
        Assert.True(_vm.AutoUpdateApps);
        Assert.True(MarkerKept());
    }

    [Fact]
    public async Task SecurityFirst_IsSaved_AndTheRulesRunAgain()
    {
        var changed = 0;
        _vm.AutoRulesChanged += (_, _) => changed++;
        _vm.Open();
        Assert.True(_vm.SecurityFirst);
        _vm.SecurityFirst = false;
        await Saved();
        Assert.False(_settings.Current.Settings.SecurityFirst);
        Assert.Equal(1, changed);
    }

    // The rows show the change at once.
    [Fact]
    public async Task ShowWhatsNew_IsSaved_AndTheRowsFollow()
    {
        var changed = 0;
        _vm.AutoRulesChanged += (_, _) => changed++;
        _vm.Open();
        Assert.True(_vm.ShowWhatsNew);
        _vm.ShowWhatsNew = false;
        await Saved();
        Assert.False(_settings.Current.Settings.ShowWhatsNew);
        Assert.Equal(1, changed);
    }

    // The install window: off, with 22:00 to 06:00 ready, and a "to" list without the "from" hour (spec §4.5).
    [Fact]
    public void InstallWindow_IsOff_WithItsHoursReady()
    {
        _vm.Open();
        Assert.False(_vm.InstallWindowEnabled);
        Assert.Equal(24, _vm.WindowFromChoices.Count);
        Assert.Equal("22:00", _vm.WindowFromChoices[_vm.WindowFromIndex]);
        Assert.Equal(23, _vm.WindowToChoices.Count);
        Assert.DoesNotContain("22:00", _vm.WindowToChoices);
        Assert.Equal("06:00", _vm.WindowToChoices[_vm.WindowToIndex]);
    }

    [Fact]
    public async Task InstallWindow_TurnedOn_IsSaved_AndTheRulesRunAgain()
    {
        var changed = 0;
        _vm.AutoRulesChanged += (_, _) => changed++;
        _vm.Open();
        _vm.InstallWindowEnabled = true;
        await Saved();
        Assert.True(_settings.Current.Settings.InstallWindowEnabled);
        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task FromHour_IsSaved_AndLeavesTheToList()
    {
        _vm.Open();
        _vm.WindowFromIndex = 1;
        await Saved();
        Assert.Equal((1, 6), (_settings.Current.Settings.InstallWindowFrom, _settings.Current.Settings.InstallWindowTo));
        Assert.DoesNotContain("01:00", _vm.WindowToChoices);
        Assert.Contains("22:00", _vm.WindowToChoices);
        Assert.Equal("06:00", _vm.WindowToChoices[_vm.WindowToIndex]);
    }

    [Fact]
    public async Task FromHourEqualToTheToHour_MovesItOneHourOn()
    {
        _vm.Open();
        _vm.WindowFromIndex = 6;
        await Saved();
        Assert.Equal((6, 7), (_settings.Current.Settings.InstallWindowFrom, _settings.Current.Settings.InstallWindowTo));
        Assert.Equal("07:00", _vm.WindowToChoices[_vm.WindowToIndex]);
    }

    [Fact]
    public async Task ToHour_IsSaved()
    {
        _vm.Open();
        _vm.WindowToIndex = _vm.WindowToChoices.ToList().IndexOf("05:00");
        await Saved();
        Assert.Equal((22, 5), (_settings.Current.Settings.InstallWindowFrom, _settings.Current.Settings.InstallWindowTo));
    }

    // A new list would clear the "from" dropdown's pick; the new "to" list's index moves through -1 even when it's the same number.
    [Fact]
    public async Task FromHour_KeepsItsList_AndSetsTheToIndexAfresh()
    {
        _vm.Open();
        var fromChoices = _vm.WindowFromChoices;
        var toIndexes = new List<int>();
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.WindowToIndex)) toIndexes.Add(_vm.WindowToIndex);
        };
        _vm.WindowFromIndex = 23;
        await Saved();
        Assert.Same(fromChoices, _vm.WindowFromChoices);
        Assert.Equal([-1, 6], toIndexes);
        Assert.Equal("06:00", _vm.WindowToChoices[_vm.WindowToIndex]);
    }

    [Fact]
    public void RegionChange_ShowsTheHoursInTheNewFormat()
    {
        var culture = CultureInfo.InvariantCulture;
        using var vm = Create(culture: () => culture);
        vm.Open();
        var twelveHour = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        twelveHour.DateTimeFormat.ShortTimePattern = "h:mm tt";
        culture = twelveHour;
        vm.RegionChanged();
        Assert.Equal((Nb("10:00 PM"), Nb("6:00 AM")), (vm.WindowFromChoices[vm.WindowFromIndex], vm.WindowToChoices[vm.WindowToIndex]));
    }

    [Fact]
    public async Task WaitThatCantBeSaved_SnapsBack_AndExplains()
    {
        var changed = 0;
        _vm.AutoRulesChanged += (_, _) => changed++;
        _vm.Open();
        BlockSaves();
        _vm.WaitIndex = 2;
        await Saved();
        Assert.Equal("Off", _vm.WaitChoices[_vm.WaitIndex]);
        Assert.Equal(NoticeKind.SaveFailed, Assert.Single(_vm.Notices).Kind);
        Assert.Equal(0, changed);
    }

    // Back up and Restore (spec §4.5).
    private string Backup(string text)
    {
        _desktop.Picked = _folder.PathOf("list.json");
        File.WriteAllText(_desktop.Picked, text);
        return _desktop.Picked;
    }

    [Fact]
    public async Task BackUp_SavesTheTrackedApps_WhereTheUserPicks()
    {
        _desktop.Picked = _folder.PathOf("list.json");
        await _vm.BackUpCommand.ExecuteAsync(null);
        Assert.Equal([AppListBackup.FileName], _desktop.Suggested);
        Assert.Equal(["Example.Editor", "Example.Paint"], AppListBackup.Load(_desktop.Picked)!.Apps.Select(a => a.Id));
        Assert.Empty(_vm.Notices);
    }

    // The log gets the code, not the path, which can hold the user's name.
    [Fact]
    public async Task BackUp_ThatCantBeSaved_SaysSo()
    {
        _desktop.Picked = _folder.PathOf("list.json");
        Directory.CreateDirectory(_desktop.Picked);
        await _vm.BackUpCommand.ExecuteAsync(null);
        var notice = Assert.Single(_vm.Notices);
        Assert.Equal((NoticeKind.BackupNotSaved, "The list couldn't be saved there."), (notice.Kind, notice.Title));
        Assert.Contains("WARN List not backed up: 0x80070005", LogText());
        Assert.DoesNotContain(_folder.Root, LogText());
    }

    [Fact]
    public async Task NewResult_ReplacesTheLastOne()
    {
        Backup("{ }");
        await _vm.RestoreCommand.ExecuteAsync(null);
        _desktop.Picked = _folder.PathOf("folder");
        Directory.CreateDirectory(_desktop.Picked);
        await _vm.BackUpCommand.ExecuteAsync(null);
        Assert.Equal(NoticeKind.BackupNotSaved, Assert.Single(_vm.Notices).Kind);
        _desktop.Picked = _folder.PathOf("list.json");
        await _vm.BackUpCommand.ExecuteAsync(null);
        Assert.Empty(_vm.Notices);
    }

    // The buttons stay enabled, so focus stays on them.
    [Fact]
    public async Task SecondClick_WhileADialogIsOpen_DoesNothing()
    {
        _desktop.Dialog = new();
        var backUp = _vm.BackUpCommand.ExecuteAsync(null);
        var restore = _vm.RestoreCommand.ExecuteAsync(null);
        await _vm.BackUpCommand.ExecuteAsync(null).WaitAsync(Wait, Ct);
        await _vm.RestoreCommand.ExecuteAsync(null).WaitAsync(Wait, Ct);
        Assert.True(_vm.BackUpCommand.CanExecute(null) && _vm.RestoreCommand.CanExecute(null));
        Assert.Equal((1, 1), (_desktop.Suggested.Count, _desktop.Opened));
        _desktop.Dialog.SetResult();
        await Task.WhenAll(backUp, restore);
    }

    [Fact]
    public async Task Cancel_DoesNothing()
    {
        await _vm.BackUpCommand.ExecuteAsync(null);
        await _vm.RestoreCommand.ExecuteAsync(null);
        Assert.Empty(_vm.Notices);
        Assert.Empty(_packages.Asked);
    }

    [Fact]
    public async Task Restore_AddsTheInstalledApps_WithTheirAuto_AndSaysWhatHappened()
    {
        var restored = 0;
        _vm.AppsRestored += (_, _) => restored++;
        Backup("""
            { "format": 1, "apps": [
              { "id": "Example.Editor", "name": "Example Editor", "auto": true },
              { "id": "Contoso.Paint", "name": "Contoso Paint", "auto": true },
              { "id": "Contoso.Chat", "name": "Contoso Chat" } ] }
            """);
        _packages.Installed.AddRange(["Example.Editor", "Contoso.Paint"]);
        await _vm.RestoreCommand.ExecuteAsync(null);
        await Saved();
        Assert.Equal([("Example.Editor", (bool?)null), ("Example.Paint", null), ("Contoso.Paint", true)], _settings.Current.Apps.Select(a => (a.Id, a.AutoChoice)));
        var notice = Assert.Single(_vm.Notices);
        Assert.Equal(("Added 1 app.", "Not installed here: Contoso Chat."), (notice.Title, notice.Message));
        Assert.Equal(1, restored);
        Assert.Equal(["installed"], _packages.Reads);
    }

    // Only an Auto that's on carries over, so with the switch on every restored app follows it (spec §4.5).
    [Fact]
    public async Task Restore_WithTheSwitchOn_LetsEveryAppFollowIt()
    {
        _settings.Update(f => f with { Settings = f.Settings with { AutoUpdateApps = true } });
        Backup("""
            { "format": 1, "apps": [
              { "id": "Contoso.Paint", "name": "Contoso Paint", "auto": true },
              { "id": "Contoso.Chat", "name": "Contoso Chat" } ] }
            """);
        _packages.Installed.AddRange(["Contoso.Paint", "Contoso.Chat"]);
        await _vm.RestoreCommand.ExecuteAsync(null);
        await Saved();
        Assert.Equal([("Contoso.Paint", (bool?)null), ("Contoso.Chat", null)],
            _settings.Current.Apps.Where(a => a.Id.StartsWith("Contoso.", StringComparison.Ordinal)).Select(a => (a.Id, a.AutoChoice)));
    }

    // Its apps were on Auto through the switch, so they follow this PC's switch, and turning it off stops them (spec §4.5).
    [Fact]
    public async Task Restore_OfAFileMadeWithTheSwitchOn_LetsEveryAppFollowThisPCsSwitch()
    {
        Backup("""
            { "format": 1, "autoUpdateApps": true, "apps": [
              { "id": "Contoso.Paint", "name": "Contoso Paint", "auto": true },
              { "id": "Contoso.Chat", "name": "Contoso Chat", "auto": false } ] }
            """);
        _packages.Installed.AddRange(["Contoso.Paint", "Contoso.Chat"]);
        await _vm.RestoreCommand.ExecuteAsync(null);
        await Saved();
        Assert.Equal([("Contoso.Paint", (bool?)null), ("Contoso.Chat", null)],
            _settings.Current.Apps.Where(a => a.Id.StartsWith("Contoso.", StringComparison.Ordinal)).Select(a => (a.Id, a.AutoChoice)));
    }

    [Fact]
    public async Task Restore_OfAnotherFile_SaysSo()
    {
        Backup("{ }");
        await _vm.RestoreCommand.ExecuteAsync(null);
        var notice = Assert.Single(_vm.Notices);
        Assert.Equal((NoticeKind.NotABackup, "This file isn't a Tiny Tracker app list."), (notice.Kind, notice.Title));
        Assert.Empty(_packages.Asked);
    }

    [Fact]
    public async Task Restore_OfAFileThatCantBeRead_SaysSo()
    {
        _desktop.Picked = _folder.PathOf("gone.json");
        await _vm.RestoreCommand.ExecuteAsync(null);
        var notice = Assert.Single(_vm.Notices);
        Assert.Equal((NoticeKind.BackupNotRead, "This file couldn't be read."), (notice.Kind, notice.Title));
        Assert.Contains("WARN List not restored: 0x80070002", LogText());
        Assert.DoesNotContain(_folder.Root, LogText());
    }

    [Fact]
    public async Task Restore_WhenEveryAppIsTrackedAlready_SaysSo_WithoutAskingWinGet()
    {
        Backup("""{ "format": 1, "apps": [ { "id": "example.editor" }, { "id": "Example.Paint" } ] }""");
        await _vm.RestoreCommand.ExecuteAsync(null);
        Assert.Equal("Every app in this file is tracked already.", Assert.Single(_vm.Notices).Title);
        Assert.Empty(_packages.Asked);
    }

    [Fact]
    public async Task Restore_OfAnEmptyList_AddsNothing()
    {
        Backup("""{ "format": 1, "apps": [] }""");
        await _vm.RestoreCommand.ExecuteAsync(null);
        var notice = Assert.Single(_vm.Notices);
        Assert.Equal(("No apps were added.", ""), (notice.Title, notice.Message));
        Assert.Empty(_packages.Asked);
    }

    // A file's names and ids may be old or edited: the list takes winget's.
    [Fact]
    public async Task RestoredApp_TakesWinGetsIdAndName()
    {
        Backup("""{ "format": 1, "apps": [ { "id": "contoso.paint", "name": "Paint" } ] }""");
        _packages.Installed.Add("Contoso.Paint");
        await _vm.RestoreCommand.ExecuteAsync(null);
        await Saved();
        Assert.Equal(("Contoso.Paint", "Contoso Paint"), _settings.Current.Apps.Select(a => (a.Id, a.Name)).Last());
    }

    [Fact]
    public async Task Restore_WhenWinGetCantAnswer_SaysSo_AndAddsNothing()
    {
        Backup("""{ "format": 1, "apps": [ { "id": "Contoso.Paint" } ] }""");
        _packages.Fails = new PackageSourceException(CheckProblem.WinGetUnreachable, "RPC server unavailable");
        await _vm.RestoreCommand.ExecuteAsync(null);
        var notice = Assert.Single(_vm.Notices);
        Assert.Equal((NoticeKind.RestoreFailed, "The list couldn't be restored. Try again in a moment.", "RPC server unavailable"), (notice.Kind, notice.Title, notice.Details));
        Assert.Equal(2, _settings.Current.Apps.Count);
    }

    // winget itself needs updating, which trying again won't do (spec §4.5).
    [Theory]
    [InlineData(CheckProblem.WinGetMissing)]
    [InlineData(CheckProblem.WinGetTooOld)]
    public async Task Restore_WhenWinGetNeedsAnUpdate_OffersTheStore(CheckProblem problem)
    {
        Backup("""{ "format": 1, "apps": [ { "id": "Contoso.Paint" } ] }""");
        _packages.Fails = new PackageSourceException(problem, "winget 1.11.510 is older than 1.29.280.", new InvalidOperationException("Class not registered") { HResult = unchecked((int)0x80040154) });
        await _vm.RestoreCommand.ExecuteAsync(null);
        var notice = Assert.Single(_vm.Notices);
        Assert.Equal((NoticeKind.WinGet, "winget needs an update", "winget 1.11.510 is older than 1.29.280. (0x80040154)"), (notice.Kind, notice.Title, notice.Details));
        Assert.Equal(["Open Store"], notice.Actions.Select(a => a.Text));
        notice.Actions[0].Run();
        Assert.Equal([Notice.AppInstallerStoreLink], _desktop.Links);
        Backup("{ }");
        await _vm.RestoreCommand.ExecuteAsync(null);
        Assert.Equal(NoticeKind.NotABackup, Assert.Single(_vm.Notices).Kind);
    }

    // A dialog that doesn't open says so, where Cancel says nothing (spec §4.5).
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FileDialogThatDoesntOpen_SaysSo(bool backUp)
    {
        _desktop.DialogFails = new InvalidOperationException("The dialog failed.") { HResult = unchecked((int)0x80004005) };
        await (backUp ? _vm.BackUpCommand : _vm.RestoreCommand).ExecuteAsync(null);
        var notice = Assert.Single(_vm.Notices);
        Assert.Equal((NoticeKind.FileDialogFailed, "The file dialog didn't open.", "Code: 0x80004005"), (notice.Kind, notice.Title, notice.Details));
        // It doesn't stick: the next click opens the dialog.
        _desktop.DialogFails = null;
        await (backUp ? _vm.BackUpCommand : _vm.RestoreCommand).ExecuteAsync(null);
        Assert.Equal(2, backUp ? _desktop.Suggested.Count : _desktop.Opened);
    }

    [Fact]
    public async Task Restore_SaysWhatItsDoing_WhileWinGetReads_AndASecondClickDoesNothing()
    {
        Backup("""{ "format": 1, "apps": [ { "id": "Contoso.Paint" } ] }""");
        _packages.Gate = new();
        var restore = _vm.RestoreCommand.ExecuteAsync(null);
        await Until(() => _packages.Asked.Count == 1);
        Assert.Equal("Finding which apps are installed…", _vm.AppListText);
        await _vm.RestoreCommand.ExecuteAsync(null).WaitAsync(Wait, Ct);
        Assert.Equal(1, _desktop.Opened);
        _packages.Gate.SetResult();
        await restore;
        Assert.Equal("Save the apps you track to a file, or add them back from one.", _vm.AppListText);
    }

    [Fact]
    public async Task Restore_WhenWinGetTakesTooLong_SaysSo()
    {
        Backup("""{ "format": 1, "apps": [ { "id": "Contoso.Paint" } ] }""");
        _packages.Gate = new();
        var restore = _vm.RestoreCommand.ExecuteAsync(null);
        await Until(() => _packages.Asked.Count == 1);
        _time.Advance(CheckRunner.Deadline);
        await restore;
        var notice = Assert.Single(_vm.Notices);
        Assert.Equal((NoticeKind.RestoreFailed, null), (notice.Kind, notice.Details));
    }

    [Fact]
    public void Shortcut_ShowsInWords()
    {
        _keys.TryUse(Shortcut.Default);
        _vm.Open();
        Assert.Equal(("Win + Shift + U", "", false), (_vm.ShortcutText, _vm.ShortcutNote, _vm.IsRecording));
    }

    [Fact]
    public async Task RecordedShortcut_IsUsedAndSaved()
    {
        _keys.TryUse(Shortcut.Default);
        _vm.Open();
        _vm.StartRecording();
        Assert.Equal(("Press a shortcut…", true, null), (_vm.ShortcutText, _vm.IsRecording, _keys.Current));
        Assert.True(_vm.Record(ShortcutModifiers.Control, 0x11));
        Assert.True(_vm.IsRecording);
        Assert.True(_vm.Record(ShortcutModifiers.Control | ShortcutModifiers.Shift, 0x4B));
        await Saved();
        var shortcut = new Shortcut(ShortcutModifiers.Control | ShortcutModifiers.Shift, 0x4B);
        Assert.Equal((shortcut, shortcut, "Ctrl + Shift + K", false), (_keys.Current, _settings.Current.Settings.OpenShortcut, _vm.ShortcutText, _vm.IsRecording));
    }

    [Fact]
    public void ShortcutWithoutCtrlAltOrWin_AsksForOne()
    {
        _vm.Open();
        _vm.StartRecording();
        Assert.True(_vm.Record(ShortcutModifiers.Shift, 0x4B));
        Assert.Equal((true, "Use Ctrl, Alt or Win with a key"), (_vm.IsRecording, _vm.ShortcutNote));
    }

    [Fact]
    public async Task Esc_ClearsTheShortcut()
    {
        _keys.TryUse(Shortcut.Default);
        _vm.Open();
        _vm.StartRecording();
        Assert.True(_vm.Record(ShortcutModifiers.None, 0x1B));
        await Saved();
        Assert.Equal((null, null, "None"), (_keys.Current, _settings.Current.Settings.OpenShortcut, _vm.ShortcutText));
    }

    [Fact]
    public async Task ShortcutInUse_IsNotSaved_AndTheOldOneStays()
    {
        _keys.TryUse(Shortcut.Default);
        var taken = new Shortcut(ShortcutModifiers.Control | ShortcutModifiers.Alt, 0x4B);
        _keys.Taken.Add(taken);
        _vm.Open();
        _vm.StartRecording();
        Assert.True(_vm.Record(taken.Modifiers, taken.Key));
        await Saved();
        Assert.Equal((Shortcut.Default, Shortcut.Default, "Win + Shift + U", "Shortcut in use", false),
            (_keys.Current, _settings.Current.Settings.OpenShortcut, _vm.ShortcutText, _vm.ShortcutNote, _vm.IsRecording));
    }

    [Fact]
    public void Cancel_BringsTheOldOneBack()
    {
        _keys.TryUse(Shortcut.Default);
        _vm.Open();
        _vm.StartRecording();
        _vm.CancelRecording();
        Assert.Equal((Shortcut.Default, "Win + Shift + U", false), (_keys.Current, _vm.ShortcutText, _vm.IsRecording));
    }

    [Fact]
    public void Tab_CancelsAndMovesOn()
    {
        _keys.TryUse(Shortcut.Default);
        _vm.Open();
        _vm.StartRecording();
        Assert.False(_vm.Record(ShortcutModifiers.None, 0x09));
        Assert.Equal((false, Shortcut.Default), (_vm.IsRecording, _keys.Current));
    }

    // Windows can refuse a shortcut for another reason than another app owning it (spec §4.8).
    [Fact]
    public async Task ShortcutWindowsRefuses_SaysSo_AndLogsTheCode()
    {
        _keys.TryUse(Shortcut.Default);
        var refused = new Shortcut(ShortcutModifiers.Control | ShortcutModifiers.Alt, 0x4B);
        _keys.Refused.Add(refused);
        _vm.Open();
        _vm.StartRecording();
        Assert.True(_vm.Record(refused.Modifiers, refused.Key));
        await Saved();
        Assert.Equal((Shortcut.Default, Shortcut.Default, "Win + Shift + U", "Couldn't set this shortcut"),
            (_keys.Current, _settings.Current.Settings.OpenShortcut, _vm.ShortcutText, _vm.ShortcutNote));
        Assert.Contains("WARN Shortcut not set: error 87", LogText());
    }

    // Tried again each time Settings opens, which the log doesn't repeat: the start logged it.
    [Fact]
    public void SavedShortcutWindowsRefuses_SaysSo_AndIsTriedAgainWhenSettingsOpens()
    {
        _keys.Refused.Add(Shortcut.Default);
        _keys.TryUse(Shortcut.Default);
        _vm.Open();
        _vm.Open();
        Assert.Equal("Couldn't set this shortcut", _vm.ShortcutNote);
        Assert.DoesNotContain("Shortcut not set", File.Exists(_folder.PathOf("app.log")) ? LogText() : "");
        _keys.Refused.Clear();
        _vm.Open();
        Assert.Equal(("", Shortcut.Default), (_vm.ShortcutNote, _keys.Current));
    }

    [Fact]
    public void SavedShortcutInUse_SaysSo_AndIsTriedAgainWhenSettingsOpens()
    {
        _keys.Taken.Add(Shortcut.Default);
        _keys.TryUse(Shortcut.Default);
        _vm.Open();
        Assert.Equal("Shortcut in use", _vm.ShortcutNote);
        _keys.Taken.Clear();
        _vm.Open();
        Assert.Equal(("", Shortcut.Default), (_vm.ShortcutNote, _keys.Current));
    }

    [Fact]
    public void ShiftTab_CancelsToo()
    {
        _keys.TryUse(Shortcut.Default);
        _vm.Open();
        _vm.StartRecording();
        Assert.False(_vm.Record(ShortcutModifiers.Shift, 0x09));
        Assert.Equal((false, Shortcut.Default), (_vm.IsRecording, _keys.Current));
    }

    [Fact]
    public async Task RefusedSaveOfAnOlderShortcut_LeavesANewerRecordingAlone()
    {
        _keys.TryUse(Shortcut.Default);
        _vm.Open();
        _vm.StartRecording();
        BlockSaves();
        Assert.True(_vm.Record(ShortcutModifiers.Control | ShortcutModifiers.Shift, 0x4B));
        _vm.StartRecording();
        await Saved();
        Assert.Equal((true, null, "Press a shortcut…"), (_vm.IsRecording, _keys.Current, _vm.ShortcutText));
        _vm.CancelRecording();
        Assert.Equal((Shortcut.Default, "Win + Shift + U"), (_keys.Current, _vm.ShortcutText));
    }

    [Fact]
    public async Task ShortcutThatCantBeSaved_GoesBackToTheOldOne()
    {
        _keys.TryUse(Shortcut.Default);
        _vm.Open();
        _vm.StartRecording();
        BlockSaves();
        Assert.True(_vm.Record(ShortcutModifiers.Control | ShortcutModifiers.Shift, 0x4B));
        await Saved();
        Assert.Equal((Shortcut.Default, "Win + Shift + U"), (_keys.Current, _vm.ShortcutText));
        Assert.Equal(NoticeKind.SaveFailed, Assert.Single(_vm.Notices).Kind);
    }

    [Fact]
    public async Task ShortcutThatCantBeSaved_SaysInUse_WhenTheOldOneWasTakenMeanwhile()
    {
        _keys.TryUse(Shortcut.Default);
        _vm.Open();
        _vm.StartRecording();
        BlockSaves();
        Assert.True(_vm.Record(ShortcutModifiers.Control | ShortcutModifiers.Shift, 0x4B));
        _keys.Taken.Add(Shortcut.Default);
        await Saved();
        Assert.Equal(("Win + Shift + U", "Shortcut in use"), (_vm.ShortcutText, _vm.ShortcutNote));
    }

    [Fact]
    public void HidingThePage_WhileRecording_Cancels()
    {
        _keys.TryUse(Shortcut.Default);
        _vm.Open();
        _vm.StartRecording();
        _vm.Close();
        Assert.Equal((Shortcut.Default, false), (_keys.Current, _vm.IsRecording));
    }

    [Fact]
    public async Task TrackedCount_FollowsTicksStillBeingSaved()
    {
        using var gate = new ManualResetEventSlim();
        try
        {
            _writer.Update(file =>
            {
                gate.Wait(Wait);
                return file with { Apps = [.. file.Apps, App("Example.Clock")] };
            });
            _vm.Open();
            Assert.Equal("2 apps tracked", _vm.TrackedText);
        }
        finally
        {
            gate.Set();
        }
        await Saved();
        Assert.Equal("3 apps tracked", _vm.TrackedText);
    }

    [Fact]
    public void UnreadableSettings_ShowTheirNotice()
    {
        using (new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None)) _settings.Load();
        _vm.Open();
        Assert.Equal(NoticeKind.SettingsUnreadable, Assert.Single(_vm.Notices).Kind);
    }

    [Fact]
    public void LogsFolder_OpensOnlyOnceItExists()
    {
        _vm.Open();
        Assert.False(_vm.CanOpenLogs);
        Directory.CreateDirectory(_folder.PathOf("logs"));
        _vm.Open();
        Assert.True(_vm.CanOpenLogs);
        _vm.OpenLogsCommand.Execute(null);
        Assert.Equal([_folder.PathOf("logs")], _desktop.Folders);
    }

    [Fact]
    public void Links_GoToTheRepositoryTheLicenseAndTheTipPage()
    {
        _vm.OpenGitHubCommand.Execute(null);
        _vm.OpenLicenseCommand.Execute(null);
        _vm.OpenTipPageCommand.Execute(null);
        Assert.Equal([AppInfo.RepositoryUrl, AppInfo.LicenseUrl, AppInfo.TipUrl], _desktop.Links);
    }

    [Fact]
    public async Task Copy_PutsTheFactsOnTheClipboard_ThenTheTextComesBack()
    {
        _vm.Open();
        _vm.CopyDiagnosticsCommand.Execute(null);
        Assert.Equal("Copying…", _vm.CopyText);
        _vm.CopyDiagnosticsCommand.Execute(null);
        await Until(() => !_vm.IsCopying);
        Assert.Equal("Copied to the clipboard", _vm.CopyText);
        var text = Assert.Single(_desktop.Copied);
        Assert.Contains("Tiny Tracker 0.1.0", text);
        Assert.Contains("winget 1.29.380", text);
        Assert.Contains("Tracked apps: 2", text);
        _time.Advance(SettingsViewModel.FeedbackShownFor);
        _ui.Pump();
        Assert.Equal("For bug reports. No personal data is included.", _vm.CopyText);
    }

    [Fact]
    public async Task CopyThatFails_SaysSo()
    {
        _desktop.ClipboardBusy = true;
        _vm.CopyDiagnosticsCommand.Execute(null);
        await Until(() => !_vm.IsCopying);
        Assert.Equal("Couldn't copy. Try again.", _vm.CopyText);
    }

    [Fact]
    public async Task HidingThePageWhileCopying_LeavesTheClipboardAlone()
    {
        var answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _desktop.Version = answer.Task;
        _vm.CopyDiagnosticsCommand.Execute(null);
        await Until(() => _desktop.Asked > 0);
        _vm.Close();
        answer.SetResult("1.29.380");
        var deadline = DateTime.UtcNow + Wait;
        while (_ui.Pump() == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting.");
            await Task.Delay(5, Ct);
        }
        Assert.Empty(_desktop.Copied);
        Assert.Equal(("For bug reports. No personal data is included.", false), (_vm.CopyText, _vm.IsCopying));
    }

    [Fact]
    public async Task SlowWinGet_IsLeftOut_AfterFiveSeconds()
    {
        _desktop.Version = new TaskCompletionSource<string?>().Task;
        _vm.CopyDiagnosticsCommand.Execute(null);
        await Until(() => _desktop.Asked > 0);
        _time.Advance(SettingsViewModel.WinGetTimeout);
        await Until(() => !_vm.IsCopying);
        Assert.Contains("winget unavailable", Assert.Single(_desktop.Copied));
    }

    private sealed class FakeStartupValues : IStartupValues
    {
        public string? Run { get; set; }
        public byte[]? Approved { get; set; }
        public int Writes { get; private set; }
        public Exception? Fail { get; set; }
        public Exception? ReadFail { get; set; }

        public string? ReadRun() => ReadFail is null ? Run : throw ReadFail;

        public void WriteRun(string command)
        {
            Writes++;
            if (Fail is not null) throw Fail;
            Run = command;
        }

        public void DeleteRun()
        {
            Writes++;
            if (Fail is not null) throw Fail;
            Run = null;
        }

        public byte[]? ReadApproved() => Approved;

        public void DeleteApproved() => Approved = null;
    }

    // Each change waits for the answer a test gives.
    private sealed class FakeSilentMode : ISilentMode
    {
        private readonly List<bool> _changes = [];
        private volatile bool _exists = true;
        private int _taskChecks;

        public SilentModeAvailability Availability { get; set; } = SilentModeAvailability.Available;
        public TaskCompletionSource<(SwitchResult Result, string? Code)> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int TaskChecks => Volatile.Read(ref _taskChecks);

        public List<bool> Changes
        {
            get
            {
                lock (_changes) return [.. _changes];
            }
        }

        public bool TaskExists
        {
            get
            {
                Interlocked.Increment(ref _taskChecks);
                return _exists;
            }
            set => _exists = value;
        }

        public Task<(SwitchResult Result, string? Code)> TurnOnAsync(CancellationToken ct) => Change(true);

        public Task<(SwitchResult Result, string? Code)> TurnOffAsync(CancellationToken ct) => Change(false);

        private Task<(SwitchResult Result, string? Code)> Change(bool on)
        {
            lock (_changes) _changes.Add(on);
            return Answer.Task;
        }
    }

    // Answers as the test says; turning on waits for Answer.
    private sealed class FakeProxyOption : IProxyOption
    {
        private int _reads;
        private int _turnOns;

        public SpeedLimitAvailability Availability { get; set; } = SpeedLimitAvailability.Available;
        public bool Prompts { get; set; } = true;
        public bool? On { get; set; } = false;
        public TaskCompletionSource<(SwitchResult Result, string? Code)> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Reads => Volatile.Read(ref _reads);
        public int TurnOns => Volatile.Read(ref _turnOns);

        public Task<bool?> IsOnAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _reads);
            return Task.FromResult(On);
        }

        public Task<(SwitchResult Result, string? Code)> TurnOnAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _turnOns);
            return Answer.Task;
        }
    }

    // Mirrors the App's HotKey: a shortcut another app owns, or Windows refuses with error 87, isn't used, and the one before it stays.
    private sealed class FakeShortcutKeys : IShortcutKeys
    {
        private Shortcut? _paused;

        public Shortcut? Current { get; private set; }
        public HashSet<Shortcut> Taken { get; } = [];
        public HashSet<Shortcut> Refused { get; } = [];
        public ShortcutProblem Problem { get; private set; }
        public int Error { get; private set; }

        public ShortcutProblem TryUse(Shortcut? shortcut)
        {
            var before = _paused ?? Current;
            _paused = null;
            Current = null;
            var problem = shortcut is null ? ShortcutProblem.None : ProblemWith(shortcut);
            if (problem == ShortcutProblem.None)
            {
                Current = shortcut;
                Problem = ShortcutProblem.None;
                return problem;
            }
            if (problem == ShortcutProblem.Failed) Error = 87;
            if (before is not null && ProblemWith(before) == ShortcutProblem.None) Current = before;
            Problem = Current is null ? problem : ShortcutProblem.None;
            return problem;
        }

        private ShortcutProblem ProblemWith(Shortcut shortcut) =>
            Taken.Contains(shortcut) ? ShortcutProblem.InUse : Refused.Contains(shortcut) ? ShortcutProblem.Failed : ShortcutProblem.None;

        public void Pause()
        {
            _paused = Current;
            Current = null;
        }

        public string KeyName(int key) => key is >= 0x41 and <= 0x5A ? ((char)key).ToString() : $"0x{key:X2}";
    }

    private sealed class FakeDesktop : IDesktop
    {
        private int _asked;

        public List<string> Links { get; } = [];
        public List<string> Folders { get; } = [];
        public List<string> Copied { get; } = [];
        public bool ClipboardBusy { get; set; }
        public Task<string?> Version { get; set; } = Task.FromResult<string?>("1.29.380");
        public int Asked => Volatile.Read(ref _asked);

        public void OpenLink(string url) => Links.Add(url);

        public void OpenFolder(string path) => Folders.Add(path);

        public bool Copy(string text)
        {
            if (ClipboardBusy) return false;
            Copied.Add(text);
            return true;
        }

        public Task<string?> WinGetVersionAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _asked);
            return Version.WaitAsync(ct);
        }

        // Both dialogs answer with it; null is Cancel. Dialog: keeps them open until set. DialogFails: why they don't open.
        public string? Picked { get; set; }
        public TaskCompletionSource? Dialog { get; set; }
        public Exception? DialogFails { get; set; }
        public List<string> Suggested { get; } = [];
        public int Opened { get; private set; }

        public async Task<string?> PickSaveFileAsync(string suggestedName)
        {
            Suggested.Add(suggestedName);
            if (DialogFails is { } error) throw new FileDialogException(error);
            if (Dialog is { } dialog) await dialog.Task;
            return Picked;
        }

        public async Task<string?> PickOpenFileAsync()
        {
            Opened++;
            if (DialogFails is { } error) throw new FileDialogException(error);
            if (Dialog is { } dialog) await dialog.Task;
            return Picked;
        }
    }

    // Installed: the ids winget finds, as winget spells them. Gate: holds the read until set. Reads: "check" or "installed" each.
    private sealed class FakePackages : IPackageSource
    {
        public List<string> Installed { get; } = [];
        public List<IReadOnlyList<TrackedApp>> Asked { get; } = [];
        public List<string> Reads { get; } = [];
        public Exception? Fails { get; set; }
        public TaskCompletionSource? Gate { get; set; }

        public Task<CatalogRead> ReadAsync(IReadOnlyList<TrackedApp> apps, CancellationToken ct) => Read("check", apps, ct);

        public Task<CatalogRead> ReadInstalledAsync(IReadOnlyList<TrackedApp> apps, CancellationToken ct) => Read("installed", apps, ct);

        private async Task<CatalogRead> Read(string kind, IReadOnlyList<TrackedApp> apps, CancellationToken ct)
        {
            Reads.Add(kind);
            Asked.Add(apps);
            if (Gate is { } gate) await gate.Task.WaitAsync(ct);
            if (Fails is { } error) throw error;
            return new CatalogRead([.. Installed.Where(id => apps.Any(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase))).Select(id => Package(id))], []);
        }
    }
}
