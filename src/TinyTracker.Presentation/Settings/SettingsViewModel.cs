using System.Collections.ObjectModel;
using System.Globalization;
using System.Security;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
using TinyTracker.Presentation.Text;

namespace TinyTracker.Presentation.Settings;

// What the Settings page asks of Windows.
public interface IDesktop
{
    void OpenLink(string url);

    void OpenFolder(string path);

    // False when another app holds the clipboard.
    bool Copy(string text);

    // Null when winget can't answer.
    Task<string?> WinGetVersionAsync(CancellationToken ct);

    // Windows' Save and Open dialogs; null when the user cancels. Throws FileDialogException when a dialog doesn't open.
    Task<string?> PickSaveFileAsync(string suggestedName);

    Task<string?> PickOpenFileAsync();
}

// A Save or Open dialog that didn't open, with its cause's code.
public sealed class FileDialogException : Exception
{
    public FileDialogException(Exception error) : base(error.Message, error) => HResult = error.HResult;
}

// The Settings page (spec §4.5). Runs on the UI thread.
public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    public static readonly TimeSpan FeedbackShownFor = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan WinGetTimeout = TimeSpan.FromSeconds(5);
    // Virtual-key codes the recorder treats apart.
    private const int TabKey = 0x09, EscapeKey = 0x1B;
    private static readonly int[] ModifierKeys = [0x10, 0x11, 0x12, 0x5B, 0x5C, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5];
    private static readonly NoticeKind[] ListResults =
        [NoticeKind.Restored, NoticeKind.NotABackup, NoticeKind.BackupNotRead, NoticeKind.RestoreFailed, NoticeKind.WinGet, NoticeKind.BackupNotSaved, NoticeKind.FileDialogFailed];

    private readonly SettingsStore _settings;
    private readonly SettingsWriter _writer;
    private readonly CheckScheduler _scheduler;
    private readonly StartupEntry _startup;
    private readonly IDesktop _desktop;
    private readonly IPackageSource _packages;
    private readonly IShortcutKeys _keys;
    private readonly ISilentMode _silent;
    private readonly SilentModeAvailability _silentAvailability;
    private readonly IProxyOption _proxy;
    private readonly SpeedLimitAvailability _limitAvailability;
    private readonly SpeedLimit _limit;
    private readonly TimeProvider _time;
    private readonly FileLog _log;
    private readonly Action<Action> _post;
    private readonly Func<(DateTimeOffset? At, CheckProblem Problem, DateTimeOffset? GoodAt)> _lastCheck;
    private readonly string _version;
    private readonly string _logs;
    private readonly Func<CultureInfo> _culture;
    private bool _quiet;
    private int _intervalRequest;
    private int _autoAppsRequest;
    private int _waitRequest;
    private int _pauseRequest;
    private int _notifyRequest;
    private int _shortcutRequest;
    private int _limitRequest;
    private int _selfUpdateRequest;
    private int _securityRequest;
    private int _windowRequest;
    private int _whatsNewRequest;
    // The "to" list's hours: every hour but the window's first.
    private List<int> _toHours = [];
    // A silent mode change waits for its answer.
    private bool _silentChanging;
    // So does turning the speed limit on.
    private bool _limitChanging;
    // The helper turned winget's option on: every save carries it, so a save that failed can't lose it.
    private bool _turnedOnProxyOption;
    // Set while Back up or Restore runs.
    private bool _backingUp;
    private bool _restoring;
    private CancellationTokenSource? _copying;
    private ITimer? _feedback;

    // packages: winget, which says which restored apps are installed. limit: the speed limit updates run under, as Settings saves
    // it. installed: a copy that updates itself (spec §6.5). culture: the user's time format, for the install window's hours.
    public SettingsViewModel(SettingsStore settings, SettingsWriter writer, CheckScheduler scheduler, StartupEntry startup, IDesktop desktop, IPackageSource packages,
        IShortcutKeys keys,
        ISilentMode silent, IProxyOption proxy, SpeedLimit limit, TimeProvider time, FileLog log, Action<Action> post,
        Func<(DateTimeOffset? At, CheckProblem Problem, DateTimeOffset? GoodAt)> lastCheck, string version, string logsFolder, bool installed = false,
        Func<CultureInfo>? culture = null)
    {
        _culture = culture ?? (() => CultureInfo.CurrentCulture);
        _settings = settings;
        _writer = writer;
        _scheduler = scheduler;
        _startup = startup;
        _desktop = desktop;
        _packages = packages;
        _keys = keys;
        _silent = silent;
        _silentAvailability = silent.Availability;
        _proxy = proxy;
        _limitAvailability = proxy.Availability;
        _limit = limit;
        _time = time;
        _log = log;
        _post = post;
        _lastCheck = lastCheck;
        _version = version;
        _logs = logsFolder;
        AutoSelfUpdateAvailable = installed;
        VersionText = Words.Format(Strings.VersionLabel, version);
        Quietly(() => IntervalIndex = IndexOf(AppSettings.CheckIntervalChoices, AppSettings.DefaultCheckIntervalHours));
        CanChangeSilentMode = SilentModeAvailable;
        SilentModeDescription = SilentModeHelp();
        CanChangeSpeedLimit = SpeedLimitAvailable;
        SpeedLimitDescription = SpeedLimitHelp();
        AutoSelfUpdateDescription = AutoSelfUpdateHelp();
    }

    public string VersionText { get; }

    public string AutoSelfUpdateText { get; } = Words.Format(Strings.AutoSelfUpdate, AppInfo.Name);

    public string SupportText { get; } = Words.Format(Strings.Support, AppInfo.Name);

    // Built once, so a ComboBox never loses its selection to a new list.
    public IReadOnlyList<string> IntervalChoices { get; } = [.. AppSettings.CheckIntervalChoices.Select(Words.Hours)];

    public IReadOnlyList<string> WaitChoices { get; } = [.. AppSettings.WaitDayChoices.Select(Words.WaitDays)];

    // In NotificationLevel's order.
    public IReadOnlyList<string> NotificationChoices { get; } = [Strings.NotifyAll, Strings.NotifyNeedsMe, Strings.NotifyFailures, Strings.NotifyOff];

    public ObservableCollection<Notice> Notices { get; } = [];

    // Raised on the UI thread once a change the Updates page follows is saved: the Auto rules, or the What's new links.
    public event EventHandler? AutoRulesChanged;

    // Raised on the UI thread once restored apps are saved, so a check runs for them.
    public event EventHandler? AppsRestored;

    // The app list's help, or what Restore is doing.
    [ObservableProperty]
    public partial string AppListText { get; private set; } = Strings.AppListHelp;

    public bool SilentModeAvailable => _silentAvailability == SilentModeAvailability.Available;

    public bool SpeedLimitAvailable => _limitAvailability == SpeedLimitAvailability.Available;

    public bool AutoSelfUpdateAvailable { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChooseAppsName))]
    public partial string TrackedText { get; private set; } = "";

    // Narrator reads the Choose apps row as one name.
    public string ChooseAppsName => Words.Format(Strings.SpokenJoin, Strings.ChooseApps, TrackedText);

    [ObservableProperty]
    public partial int IntervalIndex { get; set; }

    [ObservableProperty]
    public partial bool StartWithWindows { get; set; }

    [ObservableProperty]
    public partial bool SilentMode { get; set; }

    // Off while a change waits for its answer.
    [ObservableProperty]
    public partial bool CanChangeSilentMode { get; private set; }

    // Under the switch: what it does, why it's greyed, or how a change goes.
    [ObservableProperty]
    public partial string SilentModeDescription { get; private set; } = "";

    // Update apps automatically: every tracked app on Auto, but those with their own choice (spec §4.5).
    [ObservableProperty]
    public partial bool AutoUpdateApps { get; set; }

    [ObservableProperty]
    public partial int WaitIndex { get; set; }

    [ObservableProperty]
    public partial bool SecurityFirst { get; set; }

    // The install window's hours, in the user's time format; "to" leaves out the "from" hour.
    [ObservableProperty]
    public partial bool InstallWindowEnabled { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<string> WindowFromChoices { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<string> WindowToChoices { get; private set; } = [];

    [ObservableProperty]
    public partial int WindowFromIndex { get; set; }

    [ObservableProperty]
    public partial int WindowToIndex { get; set; }

    [ObservableProperty]
    public partial bool ShowWhatsNew { get; set; }

    [ObservableProperty]
    public partial bool PauseDuringGames { get; set; }

    [ObservableProperty]
    public partial bool SpeedLimitEnabled { get; set; }

    // Off while turning on waits for its answer.
    [ObservableProperty]
    public partial bool CanChangeSpeedLimit { get; private set; }

    // Under the switch: what it does, why it's greyed, or how turning it on goes.
    [ObservableProperty]
    public partial string SpeedLimitDescription { get; private set; } = "";

    // The box, in KB/s, while the limit is on.
    [ObservableProperty]
    public partial bool ShowsSpeedLimitBox { get; private set; }

    [ObservableProperty]
    public partial double SpeedLimitValue { get; set; }

    [ObservableProperty]
    public partial string SpeedLimitMBps { get; private set; } = "";

    [ObservableProperty]
    public partial int NotificationIndex { get; set; }

    // Under the dropdown: what the level shows.
    [ObservableProperty]
    public partial string NotificationsDescription { get; private set; } = "";

    [ObservableProperty]
    public partial string ShortcutText { get; private set; } = Strings.ShortcutNone;

    // Under the row: "Shortcut in use", or what a recorded one lacks.
    [ObservableProperty]
    public partial string ShortcutNote { get; private set; } = "";

    [ObservableProperty]
    public partial bool IsRecording { get; private set; }

    [ObservableProperty]
    public partial bool AutoSelfUpdate { get; set; }

    // Under the switch: why it's greyed, or that without silent mode updates wait for the click.
    [ObservableProperty]
    public partial string AutoSelfUpdateDescription { get; private set; } = "";

    // Nothing has been logged until there's a problem, and then the folder exists.
    [ObservableProperty]
    public partial bool CanOpenLogs { get; private set; }

    [ObservableProperty]
    public partial bool IsCopying { get; private set; }

    [ObservableProperty]
    public partial string CopyText { get; private set; } = Strings.CopyDiagnosticsHelp;

    // Each time the page shows. Values are set quietly: showing them saves nothing and writes nothing.
    public void Open()
    {
        var settings = _settings.Current.Settings;
        Quietly(() =>
        {
            IntervalIndex = IndexOf(AppSettings.CheckIntervalChoices, settings.CheckIntervalHours);
            StartWithWindows = ReadStartup();
            AutoUpdateApps = settings.AutoUpdateApps;
            WaitIndex = IndexOf(AppSettings.WaitDayChoices, settings.AutoInstallWaitDays);
            SecurityFirst = settings.SecurityFirst;
            PauseDuringGames = settings.PauseDuringGames;
            NotificationIndex = (int)settings.Notifications;
            ShowWhatsNew = settings.ShowWhatsNew;
        });
        NotificationsDescription = Words.NotificationHelp(settings.Notifications);
        ShowWindow(settings);
        // A change that waits for its answer keeps the switch as the user left it.
        if (!_silentChanging)
        {
            Quietly(() => SilentMode = SilentModeAvailable && settings.SilentMode);
            CanChangeSilentMode = SilentModeAvailable;
            SilentModeDescription = SilentModeHelp();
        }
        KeepSilentModeHonest();
        if (!_limitChanging)
        {
            Quietly(() => SpeedLimitEnabled = SpeedLimitAvailable && settings.SpeedLimitEnabled);
            CanChangeSpeedLimit = SpeedLimitAvailable;
            SpeedLimitDescription = SpeedLimitHelp();
        }
        ShowSpeedLimit(settings);
        KeepSpeedLimitHonest();
        // A saved shortcut another app took is tried again each time the page shows.
        if (_keys.Problem != ShortcutProblem.None) _keys.TryUse(settings.OpenShortcut);
        ShortcutNote = Note(_keys.Problem);
        ShowShortcut(settings.OpenShortcut);
        Quietly(() => AutoSelfUpdate = AutoSelfUpdateAvailable && settings.AutoSelfUpdate);
        AutoSelfUpdateDescription = AutoSelfUpdateHelp();
        CanOpenLogs = Directory.Exists(_logs);
        CountTracked();
        // Ticks from Choose apps may still be saving; count again once they land.
        if (!_writer.Idle.IsCompleted) _writer.Update(file => file, _ => CountTracked());
        if (_settings.Unreadable) Show(Notice.SettingsUnreadable);
        else Hide(NoticeKind.SettingsUnreadable);
    }

    // When the page hides. A copy still gathering is dropped, so the clipboard never changes behind the user's back.
    public void Close()
    {
        CancelRecording();
        _copying?.Cancel();
        _copying = null;
        IsCopying = false;
        _feedback?.Dispose();
        _feedback = null;
        CopyText = Strings.CopyDiagnosticsHelp;
    }

    public void Dispose() => Close();

    // The shortcut button was clicked: the next keys record a new shortcut.
    public void StartRecording()
    {
        if (IsRecording) return;
        _shortcutRequest++;
        IsRecording = true;
        ShortcutNote = "";
        _keys.Pause();
        ShortcutText = Strings.ShortcutRecording;
    }

    // A key pressed while recording. True when the recorder took it, so neither the button nor the flyout's Esc acts on it.
    public bool Record(ShortcutModifiers modifiers, int key)
    {
        if (!IsRecording) return false;
        // Tab and Shift+Tab move on, as they always do.
        if (key == TabKey && (modifiers & ~ShortcutModifiers.Shift) == ShortcutModifiers.None)
        {
            CancelRecording();
            return false;
        }
        if (ModifierKeys.Contains(key)) return true;
        if (key == EscapeKey && modifiers == ShortcutModifiers.None)
        {
            UseShortcut(null);
            return true;
        }
        var shortcut = new Shortcut(modifiers, key);
        if (shortcut.IsValid()) UseShortcut(shortcut);
        else ShortcutNote = Strings.ShortcutNeedsModifier;
        return true;
    }

    // Words for why a shortcut doesn't work.
    private static string Note(ShortcutProblem problem) => problem switch
    {
        ShortcutProblem.InUse => Strings.ShortcutInUse,
        ShortcutProblem.Failed => Strings.ShortcutFailed,
        _ => "",
    };

    // Clicking away, or the page hiding: Windows gets back the shortcut the file holds.
    public void CancelRecording()
    {
        if (!IsRecording) return;
        IsRecording = false;
        var stored = _settings.Current.Settings.OpenShortcut;
        _keys.TryUse(stored);
        ShortcutNote = Note(_keys.Problem);
        ShowShortcut(stored);
    }

    // One another app owns isn't saved, and the one before stays. One that can't be saved is given back up,
    // unless the user has started on another since.
    private void UseShortcut(Shortcut? shortcut)
    {
        var request = ++_shortcutRequest;
        IsRecording = false;
        if (_keys.TryUse(shortcut) is var problem and not ShortcutProblem.None)
        {
            // A refusal other than another app owning it goes to the log with its code; the start logs the saved one's.
            if (problem == ShortcutProblem.Failed) _log.Warn($"Shortcut not set: error {_keys.Error}");
            ShortcutNote = Note(problem);
            ShowShortcut(_settings.Current.Settings.OpenShortcut);
            return;
        }
        ShortcutNote = "";
        ShowShortcut(shortcut);
        _writer.Update(file => file with { Settings = file.Settings with { OpenShortcut = shortcut } }, error =>
        {
            if (error is null) return;
            Show(Notice.SaveFailed(error));
            if (request != _shortcutRequest) return;
            var stored = _settings.Current.Settings.OpenShortcut;
            ShortcutNote = Note(_keys.TryUse(stored));
            ShowShortcut(stored);
        });
    }

    private void ShowShortcut(Shortcut? shortcut) => ShortcutText = shortcut is null ? Strings.ShortcutNone : Words.Shortcut(shortcut, _keys.KeyName(shortcut.Key));

    partial void OnIntervalIndexChanged(int value)
    {
        if (_quiet || value < 0 || value >= AppSettings.CheckIntervalChoices.Count) return;
        var hours = AppSettings.CheckIntervalChoices[value];
        var request = ++_intervalRequest;
        _writer.Update(file => file with { Settings = file.Settings with { CheckIntervalHours = hours } }, error =>
        {
            if (error is not null) Show(Notice.SaveFailed(error));
            // Only the newest change's answer counts. The page then shows, and checks follow, what the file holds.
            if (request != _intervalRequest) return;
            var stored = _settings.Current.Settings.CheckIntervalHours;
            Quietly(() => IntervalIndex = IndexOf(AppSettings.CheckIntervalChoices, stored));
            var interval = TimeSpan.FromHours(stored);
            if (_scheduler.Interval != interval) _scheduler.SetInterval(interval);
        });
    }

    partial void OnWaitIndexChanged(int value)
    {
        if (_quiet || value < 0 || value >= AppSettings.WaitDayChoices.Count) return;
        var days = AppSettings.WaitDayChoices[value];
        SaveSetting(++_waitRequest, () => _waitRequest, s => s with { AutoInstallWaitDays = days },
            s => WaitIndex = IndexOf(AppSettings.WaitDayChoices, s.AutoInstallWaitDays), rules: true);
    }

    // The rows and their Auto rules follow once it's saved.
    partial void OnAutoUpdateAppsChanged(bool value)
    {
        if (_quiet) return;
        SaveSetting(++_autoAppsRequest, () => _autoAppsRequest, s => s with { AutoUpdateApps = value }, s => AutoUpdateApps = s.AutoUpdateApps, rules: true);
    }

    partial void OnPauseDuringGamesChanged(bool value)
    {
        if (_quiet) return;
        SaveSetting(++_pauseRequest, () => _pauseRequest, s => s with { PauseDuringGames = value }, s => PauseDuringGames = s.PauseDuringGames, rules: true);
    }

    partial void OnNotificationIndexChanged(int value)
    {
        if (_quiet || value < 0 || value >= NotificationChoices.Count) return;
        var level = (NotificationLevel)value;
        NotificationsDescription = Words.NotificationHelp(level);
        SaveSetting(++_notifyRequest, () => _notifyRequest, s => s with { Notifications = level }, s =>
        {
            NotificationIndex = (int)s.Notifications;
            NotificationsDescription = Words.NotificationHelp(s.Notifications);
        }, rules: false);
    }

    // Its own Auto rules run again once it's saved.
    partial void OnAutoSelfUpdateChanged(bool value)
    {
        if (_quiet) return;
        SaveSetting(++_selfUpdateRequest, () => _selfUpdateRequest, s => s with { AutoSelfUpdate = value }, s => AutoSelfUpdate = AutoSelfUpdateAvailable && s.AutoSelfUpdate,
            rules: true);
    }

    partial void OnSecurityFirstChanged(bool value)
    {
        if (_quiet) return;
        SaveSetting(++_securityRequest, () => _securityRequest, s => s with { SecurityFirst = value }, s => SecurityFirst = s.SecurityFirst, rules: true);
    }

    // The rows' links follow once it's saved.
    partial void OnShowWhatsNewChanged(bool value)
    {
        if (_quiet) return;
        SaveSetting(++_whatsNewRequest, () => _whatsNewRequest, s => s with { ShowWhatsNew = value }, s => ShowWhatsNew = s.ShowWhatsNew, rules: true);
    }

    partial void OnInstallWindowEnabledChanged(bool value)
    {
        if (_quiet) return;
        SaveWindow(s => s with { InstallWindowEnabled = value });
    }

    // A "from" hour equal to the "to" hour moves "to" one hour on (spec §4.5). The "to" list follows at once.
    partial void OnWindowFromIndexChanged(int value)
    {
        if (_quiet || value is < 0 or > 23) return;
        var to = _settings.Current.Settings.InstallWindowTo;
        ShowHours(value, to == value ? (value + 1) % 24 : to);
        SaveWindow(s => s with { InstallWindowFrom = value, InstallWindowTo = s.InstallWindowTo == value ? (value + 1) % 24 : s.InstallWindowTo });
    }

    partial void OnWindowToIndexChanged(int value)
    {
        if (_quiet || value < 0 || value >= _toHours.Count) return;
        var hour = _toHours[value];
        SaveWindow(s => s with { InstallWindowTo = hour });
    }

    private void SaveWindow(Func<AppSettings, AppSettings> change) => SaveSetting(++_windowRequest, () => _windowRequest, change, ShowWindow, rules: true);

    private void ShowWindow(AppSettings settings)
    {
        Quietly(() => InstallWindowEnabled = settings.InstallWindowEnabled);
        ShowHours(settings.InstallWindowFrom, settings.InstallWindowTo);
    }

    // A new time format shows in the hours at once.
    public void RegionChanged() => ShowHours(_settings.Current.Settings.InstallWindowFrom, _settings.Current.Settings.InstallWindowTo);

    // A new list clears its dropdown's choice, so a list is built only when it changes, and its index is then set from -1.
    private void ShowHours(int from, int to) => Quietly(() =>
    {
        var fromChoices = Enumerable.Range(0, 24).Select(h => Words.Hour(h, _culture())).ToList();
        if (!fromChoices.SequenceEqual(WindowFromChoices))
        {
            WindowFromIndex = -1;
            WindowFromChoices = fromChoices;
        }
        WindowFromIndex = from;
        var toHours = Enumerable.Range(0, 24).Where(h => h != from).ToList();
        var toChoices = toHours.Select(h => Words.Hour(h, _culture())).ToList();
        if (!toChoices.SequenceEqual(WindowToChoices))
        {
            WindowToIndex = -1;
            _toHours = toHours;
            WindowToChoices = toChoices;
        }
        WindowToIndex = _toHours.IndexOf(to);
    });

    // A saved change to an auto-install rule runs the rules again. Only the newest change's answer moves the control,
    // which then shows what the file holds.
    private void SaveSetting(int request, Func<int> newest, Func<AppSettings, AppSettings> change, Action<AppSettings> show, bool rules)
    {
        _writer.Update(file => file with { Settings = change(file.Settings) }, error =>
        {
            if (error is null && rules) AutoRulesChanged?.Invoke(this, EventArgs.Empty);
            if (error is not null) Show(Notice.SaveFailed(error));
            if (request == newest()) Quietly(() => show(_settings.Current.Settings));
        });
    }

    // The switch counts as on once the task exists, and as off once it's gone: the file follows the answer (spec §6.6).
    // Turning on shows a prompt. Turning off runs the task itself, so it prompts only when that fails.
    partial void OnSilentModeChanged(bool value)
    {
        if (_quiet) return;
        _silentChanging = true;
        CanChangeSilentMode = false;
        SilentModeDescription = value ? Strings.WaitingForPermission : SilentModeHelp();
        _ = Task.Run(async () =>
        {
            (SwitchResult Result, string? Code) answer;
            try
            {
                answer = value ? await _silent.TurnOnAsync(CancellationToken.None) : await _silent.TurnOffAsync(CancellationToken.None);
            }
            catch (Exception e)
            {
                answer = (SwitchResult.Failed, $"0x{e.HResult:X8}");
            }
            _post(() => SilentModeAnswered(value, answer));
        });
    }

    private void SilentModeAnswered(bool on, (SwitchResult Result, string? Code) answer)
    {
        _silentChanging = false;
        CanChangeSilentMode = SilentModeAvailable;
        SilentModeDescription = answer.Result == SwitchResult.Declined ? Strings.PermissionDeclined : SilentModeHelp();
        switch (answer.Result)
        {
            case SwitchResult.Done:
                Hide(NoticeKind.SilentModeNotChanged);
                Hide(NoticeKind.SilentModeTaskMissing);
                SaveSilentMode(on);
                return;
            case SwitchResult.Failed:
                _log.Warn($"Silent mode not turned {(on ? "on" : "off")}: {answer.Code}");
                Hide(NoticeKind.SilentModeNotChanged);
                Show(Notice.SilentModeNotChanged(on, answer.Code));
                break;
        }
        Quietly(() => SilentMode = SilentModeAvailable && _settings.Current.Settings.SilentMode);
    }

    // Saved changes to silent mode run the auto-install rules again: Auto apps that need admin install by themselves only in it.
    private void SaveSilentMode(bool on)
    {
        _writer.Update(file => file with { Settings = file.Settings with { SilentMode = on } }, error =>
        {
            if (error is null) AutoRulesChanged?.Invoke(this, EventArgs.Empty);
            else Show(Notice.SaveFailed(error));
            if (!_silentChanging) Quietly(() => SilentMode = SilentModeAvailable && _settings.Current.Settings.SilentMode);
            AutoSelfUpdateDescription = AutoSelfUpdateHelp();
        });
    }

    // Silent mode needs this user's task, running this copy's helper. At start and each time the page shows, a task that's gone
    // turns it off (spec §6.6), as does an account or copy it can't work on. The Task Scheduler is asked off the UI thread.
    public void KeepSilentModeHonest()
    {
        if (_silentChanging || !_settings.Current.Settings.SilentMode) return;
        if (!SilentModeAvailable)
        {
            _log.Warn("Silent mode can't work here, so silent mode is off");
            SaveSilentMode(false);
            return;
        }
        _ = Task.Run(() =>
        {
            if (!_silent.TaskExists) _post(SilentModeTaskMissing);
        });
    }

    // Also when a click found no task to start the helper; that click prompts instead.
    public void SilentModeTaskMissing()
    {
        if (_silentChanging || !_settings.Current.Settings.SilentMode) return;
        _log.Warn("Silent mode's task is missing, so silent mode is off");
        Show(Notice.SilentModeTaskMissing);
        SaveSilentMode(false);
    }

    // Turning on needs winget's proxy option: when it's off, the helper turns it on, prompting outside silent mode (spec §6.4).
    // The switch is on once the option reads on. Turning off needs no prompt: the option stays on until uninstall.
    partial void OnSpeedLimitEnabledChanged(bool value)
    {
        if (_quiet) return;
        if (!value)
        {
            SaveSpeedLimit(settings => settings with { SpeedLimitEnabled = false });
            return;
        }
        _limitChanging = true;
        CanChangeSpeedLimit = false;
        _ = Task.Run(async () =>
        {
            (SwitchResult Result, string? Code) answer;
            var turnedOn = false;
            try
            {
                var before = await _proxy.IsOnAsync(CancellationToken.None);
                if (before == true) answer = (SwitchResult.Done, null);
                else
                {
                    if (_proxy.Prompts) _post(() => SpeedLimitDescription = Strings.WaitingForPermission);
                    answer = await _proxy.TurnOnAsync(CancellationToken.None);
                    // Only an option that read off is Tiny Tracker's to turn off again.
                    turnedOn = before == false && answer.Result == SwitchResult.Done;
                }
            }
            catch (Exception e)
            {
                answer = (SwitchResult.Failed, $"0x{e.HResult:X8}");
            }
            _post(() => SpeedLimitAnswered(answer, turnedOn));
        });
    }

    // turnedOn: the helper turned winget's option on, so the uninstaller turns it off again.
    private void SpeedLimitAnswered((SwitchResult Result, string? Code) answer, bool turnedOn)
    {
        _limitChanging = false;
        CanChangeSpeedLimit = SpeedLimitAvailable;
        SpeedLimitDescription = answer.Result == SwitchResult.Declined ? Strings.PermissionDeclined : SpeedLimitHelp();
        if (answer.Result == SwitchResult.Done)
        {
            Hide(NoticeKind.SpeedLimitNotOn);
            Hide(NoticeKind.SpeedLimitOptionOff);
            _turnedOnProxyOption |= turnedOn;
            SaveSpeedLimit(settings => settings with { SpeedLimitEnabled = true });
            return;
        }
        if (answer.Result == SwitchResult.Failed)
        {
            _log.Warn($"The speed limit wasn't turned on: {answer.Code}");
            Show(Notice.SpeedLimitNotOn(answer.Code));
        }
        Quietly(() => SpeedLimitEnabled = false);
    }

    // Whole KB/s: outside the range the nearest end, and not a number, what the box showed.
    partial void OnSpeedLimitValueChanged(double oldValue, double newValue)
    {
        if (_quiet) return;
        var shown = (int)oldValue;
        var kbps = double.IsNaN(newValue) ? shown : (int)Math.Clamp(Math.Round(newValue), AppSettings.MinSpeedLimitKBps, AppSettings.MaxSpeedLimitKBps);
        Quietly(() => SpeedLimitValue = kbps);
        SpeedLimitMBps = Words.MegabytesPerSecond(kbps);
        if (kbps != shown) SaveSpeedLimit(settings => settings with { SpeedLimitKBps = kbps });
    }

    // The downloads follow what the file holds, and so does the page once the newest change is answered.
    private void SaveSpeedLimit(Func<AppSettings, AppSettings> change)
    {
        var request = ++_limitRequest;
        var turnedOn = _turnedOnProxyOption;
        _writer.Update(file => file with { Settings = change(file.Settings), TurnedOnProxyOption = file.TurnedOnProxyOption || turnedOn }, error =>
        {
            if (error is not null) Show(Notice.SaveFailed(error));
            var stored = _settings.Current.Settings;
            _limit.Set(SpeedLimit.Of(stored));
            if (request != _limitRequest) return;
            if (!_limitChanging) Quietly(() => SpeedLimitEnabled = SpeedLimitAvailable && stored.SpeedLimitEnabled);
            ShowSpeedLimit(stored);
        });
    }

    private void ShowSpeedLimit(AppSettings settings)
    {
        Quietly(() => SpeedLimitValue = settings.SpeedLimitKBps);
        SpeedLimitMBps = Words.MegabytesPerSecond(settings.SpeedLimitKBps);
        ShowsSpeedLimitBox = SpeedLimitAvailable && settings.SpeedLimitEnabled;
    }

    // At start and each time the page shows: a limit left on where it can't work is saved off, and one whose winget option went
    // off is turned off with a notice (spec §6.4). winget is asked off the UI thread.
    public void KeepSpeedLimitHonest()
    {
        if (_limitChanging || !_settings.Current.Settings.SpeedLimitEnabled) return;
        if (!SpeedLimitAvailable)
        {
            _log.Warn("The speed limit can't work here, so it's off");
            SaveSpeedLimit(settings => settings with { SpeedLimitEnabled = false });
            return;
        }
        _ = Task.Run(async () =>
        {
            if (await _proxy.IsOnAsync(CancellationToken.None) == false) _post(SpeedLimitOptionOff);
        });
    }

    // Also when winget refused the limit's proxy during an update.
    public void SpeedLimitOptionOff()
    {
        if (_limitChanging || !_settings.Current.Settings.SpeedLimitEnabled) return;
        _log.Warn("winget's proxy option is off, so the speed limit is off");
        Show(Notice.SpeedLimitOptionOff);
        SaveSpeedLimit(settings => settings with { SpeedLimitEnabled = false });
    }

    private string SpeedLimitHelp() => _limitAvailability switch
    {
        SpeedLimitAvailability.NotAdmin => Strings.SpeedLimitNotAdmin,
        SpeedLimitAvailability.Blocked => Strings.SpeedLimitBlocked,
        _ => Strings.SpeedLimitHelp,
    };

    private string AutoSelfUpdateHelp() =>
        !AutoSelfUpdateAvailable ? Words.Format(Strings.SilentModeNotInstalled, AppInfo.Name)
        : SilentModeAvailable && _settings.Current.Settings.SilentMode ? ""
        : Strings.SelfUpdateWaitsForClick;

    private string SilentModeHelp() => _silentAvailability switch
    {
        SilentModeAvailability.NotInstalled => Words.Format(Strings.SilentModeNotInstalled, AppInfo.Name),
        SilentModeAvailability.NotAdmin => Strings.SilentModeNotAdmin,
        _ => Strings.SilentModeHelp,
    };

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_quiet) return;
        try
        {
            _startup.Set(value);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
        {
            _log.Warn($"Start with Windows not changed: {e.Message}");
            Show(Notice.StartupNotChanged(e));
        }
        // Once the switch has finished its own change, it shows what Windows now holds.
        _post(() => Quietly(() => StartWithWindows = ReadStartup()));
    }

    [RelayCommand]
    private void OpenLogs()
    {
        if (Directory.Exists(_logs)) _desktop.OpenFolder(_logs);
    }

    [RelayCommand]
    private void OpenGitHub() => _desktop.OpenLink(AppInfo.RepositoryUrl);

    [RelayCommand]
    private void OpenLicense() => _desktop.OpenLink(AppInfo.LicenseUrl);

    [RelayCommand]
    private void OpenTipPage() => _desktop.OpenLink(AppInfo.TipUrl);

    [RelayCommand]
    private void Dismiss(Notice notice) => Notices.Remove(notice);

    // The facts the UI owns are read here; only the winget version is asked for off the UI thread.
    // The button stays enabled, so the keyboard focus stays on it; a press while copying does nothing.
    [RelayCommand]
    private void CopyDiagnostics()
    {
        if (_copying is not null) return;
        var copying = new CancellationTokenSource();
        _copying = copying;
        _feedback?.Dispose();
        _feedback = null;
        IsCopying = true;
        CopyText = Strings.Copying;
        var (at, problem, goodAt) = _lastCheck();
        var facts = DiagnosticFacts.Gather(_version, _settings.Current.Apps.Count, _settings.Current.Settings, ReadStartup(), at, problem, goodAt);
        _ = Task.Run(async () =>
        {
            string? winget = null;
            try
            {
                using var timeout = new CancellationTokenSource(WinGetTimeout, _time);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, copying.Token);
                winget = await _desktop.WinGetVersionAsync(linked.Token);
            }
            catch (Exception e)
            {
                if (e is not OperationCanceledException) _log.Warn($"winget version not read: {e.Message}");
            }
            _post(() => Copy(copying, facts with { WinGet = winget }));
        });
    }

    private void Copy(CancellationTokenSource copying, DiagnosticFacts facts)
    {
        copying.Dispose();
        if (copying != _copying) return;
        _copying = null;
        IsCopying = false;
        CopyText = _desktop.Copy(facts.Text()) ? Strings.Copied : Strings.CopyFailed;
        ITimer? timer = null;
        timer = _time.CreateTimer(_ => _post(() =>
        {
            if (_feedback != timer) return;
            _feedback = null;
            timer?.Dispose();
            CopyText = Strings.CopyDiagnosticsHelp;
        }), null, FeedbackShownFor, Timeout.InfiniteTimeSpan);
        _feedback = timer;
    }

    // Back up and Restore (spec §4.5). The buttons stay enabled, so focus stays on them; a click while one runs does nothing.
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task BackUpAsync()
    {
        if (_backingUp) return;
        _backingUp = true;
        try
        {
            if (await PickAsync(() => _desktop.PickSaveFileAsync(AppListBackup.FileName)) is not { } path) return;
            ShowListResult(null);
            var file = _settings.Current;
            try
            {
                await Task.Run(() => AppListBackup.Save(path, file.Apps, file.Settings));
            }
            catch (IOException e)
            {
                // The code only: the path can hold the user's name.
                _log.Warn($"List not backed up: 0x{e.HResult:X8}");
                ShowListResult(Notice.BackupNotSaved(e));
            }
        }
        finally
        {
            _backingUp = false;
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RestoreAsync()
    {
        if (_restoring) return;
        _restoring = true;
        try
        {
            if (await PickAsync(_desktop.PickOpenFileAsync) is not { } path) return;
            ShowListResult(null);
            AppListText = Strings.FindingInstalledApps;
            await RestoreFromAsync(path);
        }
        finally
        {
            AppListText = Strings.AppListHelp;
            _restoring = false;
        }
    }

    // Adds the file's apps that winget finds installed; tracked apps stay as they are.
    private async Task RestoreFromAsync(string path)
    {
        AppList? backup;
        try
        {
            backup = await Task.Run(() => AppListBackup.Load(path));
        }
        catch (IOException e)
        {
            _log.Warn($"List not restored: 0x{e.HResult:X8}");
            ShowListResult(Notice.BackupNotRead(e));
            return;
        }
        if (backup is null)
        {
            ShowListResult(Notice.NotABackup);
            return;
        }
        var tracked = _settings.Current.Apps;
        // From a file made with the switch on, every app follows this PC's switch; from any other, only an Auto that's on
        // carries over, as the app's own choice while the switch is off (spec §4.5).
        var appsAuto = _settings.Current.Settings.AutoUpdateApps;
        var wanted = backup.Apps.Where(a => !tracked.Any(t => t.Matches(a.Id, TrackedApp.WinGet)))
            .Select(a => new TrackedApp { Id = a.Id, Source = TrackedApp.WinGet, Name = a.Name, AutoChoice = !backup.AutoUpdateApps && a.Auto && !appsAuto ? true : null })
            .ToList();
        if (wanted.Count == 0)
        {
            ShowListResult(backup.Apps.Count > 0 ? Notice.AllTracked : Notice.Restored(0, []));
            return;
        }
        CatalogRead read;
        try
        {
            using var deadline = new CancellationTokenSource(CheckRunner.Deadline, _time);
            read = await Task.Run(() => _packages.ReadInstalledAsync(wanted, deadline.Token));
        }
        catch (PackageSourceException e)
        {
            _log.Warn($"List not restored: {e.Problem}");
            // Trying again won't mend winget itself: the Store does, as on Updates.
            ShowListResult(e.Problem is CheckProblem.WinGetMissing or CheckProblem.WinGetTooOld
                ? Notice.ForProblem(e.Problem, e.Detail)! with { Closable = true, Actions = [new(Strings.OpenStore, () => _desktop.OpenLink(Notice.AppInstallerStoreLink))] }
                : Notice.RestoreFailed(e.Detail));
            return;
        }
        catch (OperationCanceledException)
        {
            _log.Warn("List not restored: winget took too long");
            ShowListResult(Notice.RestoreFailed(null));
            return;
        }
        // winget's id and name: the file's may be old or edited.
        var installed = new List<TrackedApp>();
        var missing = new List<string>();
        foreach (var app in wanted)
        {
            if (read.Installed.FirstOrDefault(p => app.Matches(p.Id, p.Source)) is { } package) installed.Add(app with { Id = package.Id, Name = package.Name });
            else missing.Add(app.Name.Length > 0 ? app.Name : app.Id);
        }
        var added = 0;
        _writer.Update(file =>
        {
            var fresh = installed.Where(a => !file.Apps.Any(t => t.Matches(a.Id, a.Source))).ToList();
            added = fresh.Count;
            return fresh.Count == 0 ? file : file with { Apps = [.. file.Apps, .. fresh] };
        }, error =>
        {
            if (error is not null)
            {
                Show(Notice.SaveFailed(error));
                return;
            }
            ShowListResult(Notice.Restored(added, missing));
            CountTracked();
            if (added > 0) AppsRestored?.Invoke(this, EventArgs.Empty);
        });
    }

    // Null for Cancel too; a dialog that doesn't open says so.
    private async Task<string?> PickAsync(Func<Task<string?>> pick)
    {
        try
        {
            return await pick();
        }
        catch (FileDialogException e)
        {
            ShowListResult(Notice.FileDialogFailed(e));
            return null;
        }
    }

    // One result of Back up or Restore at a time: the newest.
    private void ShowListResult(Notice? notice)
    {
        foreach (var kind in ListResults) Hide(kind);
        if (notice is not null) Show(notice);
    }

    private void CountTracked() => TrackedText = Words.AppsTracked(_settings.Current.Apps.Count);

    private bool ReadStartup()
    {
        try
        {
            return _startup.IsOn;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
        {
            _log.Warn($"Start with Windows not read: {e.Message}");
            return false;
        }
    }

    private void Quietly(Action set)
    {
        var quiet = _quiet;
        _quiet = true;
        try
        {
            set();
        }
        finally
        {
            _quiet = quiet;
        }
    }

    private void Show(Notice notice)
    {
        if (Notices.Any(n => n.Kind == notice.Kind)) return;
        Notices.Add(notice);
    }

    private void Hide(NoticeKind kind)
    {
        if (Notices.FirstOrDefault(n => n.Kind == kind) is { } notice) Notices.Remove(notice);
    }

    private static int IndexOf(IReadOnlyList<int> choices, int value)
    {
        for (var i = 0; i < choices.Count; i++)
            if (choices[i] == value) return i;
        return 0;
    }
}
