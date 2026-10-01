using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TinyTracker.Core;
using TinyTracker.Core.Checking;
using TinyTracker.Core.History;
using TinyTracker.Core.Installing;
using TinyTracker.Core.Scheduling;
using TinyTracker.Core.SelfUpdate;
using TinyTracker.Core.Settings;
using TinyTracker.Core.Storage;
using TinyTracker.Core.Tracking;
using TinyTracker.Core.Versions;
using TinyTracker.Presentation.History;
using TinyTracker.Presentation.Settings;
using TinyTracker.Presentation.Shell;
using TinyTracker.Presentation.Text;

namespace TinyTracker.Presentation.Updates;

// The Updates page. Runs on the UI thread; UpdatesWiring hands it the Core's events there.
public sealed partial class UpdatesViewModel : ObservableObject, IDisposable, IHistoryRows
{
    public static readonly TimeSpan UpdatedShownFor = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan UndoShownFor = TimeSpan.FromSeconds(5);
    // Keeps "checked 2 min ago" and "Next check in …" current while the flyout is open.
    public static readonly TimeSpan TickEvery = TimeSpan.FromSeconds(30);
    // Windows has no event for a full-screen app ending, so an Auto app it holds back is looked at again this often.
    public static readonly TimeSpan FullScreenRecheck = TimeSpan.FromMinutes(1);
    private const string AdminFallbackTip = "adminFallback";
    private const string HiddenIconsTip = "hiddenIcons";
    private const int NewAppsShown = 3;

    private readonly CheckScheduler _scheduler;
    private readonly IInstaller _installer;
    private readonly ISystemConditions _system;
    private readonly SettingsStore _settings;
    private readonly SettingsWriter _writer;
    private readonly HistoryStore _history;
    private readonly HistoryWriter _historyWriter;
    private readonly TimeProvider _time;
    private readonly Action<Action> _post;
    private readonly Action<string> _openLink;
    private readonly ISelfUpdate? _self;
    private readonly Func<CultureInfo> _culture;
    private readonly Dictionary<string, UpdateRow> _rows = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset? _checkedAt;
    private bool _checking;
    private bool _shown;
    private bool _checkAgain;
    private bool _expandedByUser;
    private ITimer? _tick;
    private ITimer? _recheck;
    // Set for the install window's start while an update waits for it.
    private ITimer? _windowOpens;
    // The Auto app installing by itself, until its install ends.
    private string? _autoKey;
    // Why Tiny Tracker's own update hasn't installed by itself, as of the last refresh.
    private AutoBlock _selfAuto = AutoBlock.AutoOff;
    // Versions installed while the app runs: a check that read the apps before an install ended still offers them.
    private readonly List<(string Key, string Version)> _installed = [];
    // New apps answered with Track or No thanks.
    private readonly HashSet<string> _answered = new(StringComparer.OrdinalIgnoreCase);
    // The last check's new apps, in name order; three show at a time.
    private IReadOnlyList<ListedApp> _newApps = [];
    private bool _disposed;

    // self: Tiny Tracker's own update, for an installed copy. culture: the user's time format, for the install window's hours.
    public UpdatesViewModel(CheckScheduler scheduler, IInstaller installer, ISystemConditions system, SettingsStore settings, SettingsWriter writer, HistoryStore history,
        HistoryWriter historyWriter, TimeProvider time, Action<Action> post, Action<string> openLink, ISelfUpdate? self = null, Func<CultureInfo>? culture = null)
    {
        _culture = culture ?? (() => CultureInfo.CurrentCulture);
        _scheduler = scheduler;
        _installer = installer;
        _system = system;
        _settings = settings;
        _writer = writer;
        _history = history;
        _historyWriter = historyWriter;
        _time = time;
        _post = post;
        _openLink = openLink;
        _self = self;
        SelfRow = new SelfUpdateRow(this);
        // The page's button does what the row's does, while the row is on the page.
        WhatsNew = new WhatsNewViewModel(openLink, row => !row.IsRemoved && _rows.ContainsValue(row), Primary);
        Refresh();
    }

    public ObservableCollection<UpdateRow> Updates { get; } = [];
    public ObservableCollection<UpdateRow> UpToDate { get; } = [];
    public ObservableCollection<Notice> Notices { get; } = [];

    // Tiny Tracker's own update, above the apps' (spec §4.3).
    public SelfUpdateRow SelfRow { get; }

    // The What's new page, for the row it was opened from (spec §4.9).
    public WhatsNewViewModel WhatsNew { get; }

    // Raised when What's new should show its page.
    public event EventHandler? NotesRequested;

    [ObservableProperty]
    public partial bool HasSelfUpdate { get; private set; }

    [ObservableProperty]
    public partial string Summary { get; private set; } = "";

    [ObservableProperty]
    public partial string NextCheck { get; private set; } = "";

    [ObservableProperty]
    public partial string UpdateAllText { get; private set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateAllCommand))]
    public partial bool CanUpdateAll { get; private set; }

    [ObservableProperty]
    public partial bool IsChecking { get; private set; }

    // The refresh icon turns only while a check runs and the flyout shows (spec §9).
    [ObservableProperty]
    public partial bool IsSpinning { get; private set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; private set; }

    [ObservableProperty]
    public partial bool HasUpdates { get; private set; }

    [ObservableProperty]
    public partial bool HasUpToDate { get; private set; }

    [ObservableProperty]
    public partial string UpToDateText { get; private set; } = "";

    [ObservableProperty]
    public partial IReadOnlyList<UpdateRow> UpToDatePreview { get; private set; } = [];

    [ObservableProperty]
    public partial bool IsUpToDateExpanded { get; private set; }

    // The problem of the last check, as a banner.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial Notice? Problem { get; private set; }

    // What the banner binds to: x:Bind doesn't rerun a function once its argument is null.
    public bool HasProblem => Problem is not null;

    [ObservableProperty]
    public partial bool IsWorking { get; private set; }

    [ObservableProperty]
    public partial TrayState Tray { get; private set; } = new(TrayIconKind.Idle, "");

    // For diagnostics: when the last check ran and what it ran into, and when the last good one ran.
    public DateTimeOffset? LastCheckAt { get; private set; }

    public CheckProblem LastProblem { get; private set; }

    public DateTimeOffset? LastGoodCheckAt => _checkedAt;

    // Raised on the UI thread each time the rows are shown anew.
    public event EventHandler? RowsChanged;

    // Raised when a new app's notice was answered, once the next one shows, for the page's focus (spec §4.8): with the notice
    // that took its place, or null when none did.
    public event EventHandler<Notice?>? NewAppAnswered;

    public void Dispose()
    {
        _disposed = true;
        _tick?.Dispose();
        _recheck?.Dispose();
        _windowOpens?.Dispose();
        foreach (var row in _rows.Values) row.Timer?.Dispose();
    }

    // Shows the notices for files that were damaged or can't be read at startup.
    public void ShowStartupNotices(bool settingsRecovered, bool historyRecovered)
    {
        if (settingsRecovered) Show(Notice.SettingsRecovered);
        if (historyRecovered) Show(Notice.HistoryRecovered);
        RefreshFileNotices();
    }

    // winget doesn't answer the admin helper, so admin updates ask one by one. A tip says so once, ever (spec §6.6).
    public void AdminFallback() => ShowTip(AdminFallbackTip, Notice.AdminFallback);

    // Shown once, ever.
    private void ShowTip(string name, Notice tip)
    {
        if (_settings.Current.TipsShown.Contains(name)) return;
        Show(tip);
        _writer.Update(file => file.TipsShown.Contains(name) ? file : file with { TipsShown = [.. file.TipsShown, name] });
    }

    // Opening the flyout, on any page, refreshes data older than 15 minutes.
    public void FlyoutOpened() => _scheduler.FlyoutOpened();

    // The page's times and refresh icon move only while it shows.
    public void Shown()
    {
        _shown = true;
        _tick ??= _time.CreateTimer(_ => _post(Refresh), null, TickEvery, TickEvery);
        Refresh();
    }

    public void Hidden()
    {
        _shown = false;
        _tick?.Dispose();
        _tick = null;
        Refresh();
    }

    public void CheckStarted()
    {
        _checking = true;
        Refresh();
    }

    public void CheckFinished(CheckCompleted check)
    {
        _checking = false;
        LastCheckAt = check.At;
        LastProblem = check.Problem;
        // While settings.json can't be read, its own notice already says changes aren't saved.
        Problem = check.Problem == CheckProblem.SettingsNotSaved && _settings.Unreadable ? null : Notice.ForProblem(check.Problem, check.Detail);
        if (check.Problem == CheckProblem.None) _checkedAt = check.At;
        foreach (var gone in check.Untracked)
            if (_rows.Remove(Key(gone), out var removed)) removed.Timer?.Dispose();
        foreach (var app in check.Apps)
        {
            var key = Key(app.App);
            _installed.RemoveAll(i => string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase) && !PackageVersion.Same(i.Version, app.App.Offer?.Version));
            if (_rows.TryGetValue(key, out var row))
            {
                row.Check = app;
                if (row.Install?.Done is not null && Outdated(row.Install, app)) row.Install = null;
            }
            else if (IsTracked(app.App))
            {
                _rows[key] = new UpdateRow(this, app, _time.GetUtcNow());
            }
        }
        RefreshFileNotices();
        if (check.NewApps is { } newApps) ShowNewApps(newApps);
        if (_checkAgain)
        {
            _checkAgain = false;
            _scheduler.CheckNow();
        }
        Refresh();
    }

    private void ShowNewApps(IReadOnlyList<ListedApp> apps)
    {
        _newApps = [.. apps.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)];
        FillNewApps();
    }

    // One notice per new app while the checks offer it, at most three at a time, in name order and together above any tip
    // (spec §4.3). An answer counts at once, though the check that was running still lists the app.
    private void FillNewApps()
    {
        var known = _settings.Current.KnownApps ?? [];
        var shown = _newApps.Where(a => !_answered.Contains(a.Id) && !known.Contains(a.Id, StringComparer.OrdinalIgnoreCase)
            && !_settings.Current.Apps.Any(t => t.Matches(a.Id, TrackedApp.WinGet))).Take(NewAppsShown).ToList();
        foreach (var notice in Notices.Where(n => n.Kind == NoticeKind.NewApp && !shown.Any(a => SameId(a.Id, n.Key))).ToList()) Notices.Remove(notice);
        Notice? previous = null;
        foreach (var app in shown)
        {
            var notice = Notices.FirstOrDefault(n => n.Kind == NoticeKind.NewApp && SameId(app.Id, n.Key));
            // A check may name the app anew.
            if (notice is not null && notice.Title != Words.Format(Strings.NewApp, app.Name))
            {
                Notices.Remove(notice);
                notice = null;
            }
            if (notice is null)
            {
                notice = Notice.NewApp(app.Id, app.Name, () => Track(app), () => Decline(app));
                Notices.Insert(previous is not null ? Notices.IndexOf(previous) + 1 : FirstNewAppSlot(), notice);
            }
            previous = notice;
        }
    }

    // Where the first new-app notice goes: where the group is, or above the first tip.
    private int FirstNewAppSlot()
    {
        var index = Notices.ToList().FindIndex(n => n.Kind is NoticeKind.NewApp or NoticeKind.HiddenIconsTip or NoticeKind.AdminFallback);
        return index < 0 ? Notices.Count : index;
    }

    // It follows the Auto switch; the check after the save shows its row.
    private void Track(ListedApp app)
    {
        Answer(app.Id);
        var tracked = new TrackedApp { Id = app.Id, Source = TrackedApp.WinGet, Name = app.Name };
        _writer.Update(file => file.Apps.Any(a => a.Matches(app.Id, TrackedApp.WinGet)) ? file : file with { Apps = [.. file.Apps, tracked] },
            error => Answered(app.Id, error, () => ApplyTrackedApps(added: true)));
    }

    private void Decline(ListedApp app)
    {
        Answer(app.Id);
        _writer.Update(file => file.KnownApps is null ? file : file with { KnownApps = [.. file.KnownApps, app.Id] }, error => Answered(app.Id, error, null));
    }

    private void Answer(string id)
    {
        var place = NewAppNotices().FindIndex(n => SameId(id, n.Key));
        _answered.Add(id);
        FillNewApps();
        NewAppAnswered?.Invoke(this, NewAppNotices().ElementAtOrDefault(place));
    }

    private List<Notice> NewAppNotices() => [.. Notices.Where(n => n.Kind == NoticeKind.NewApp)];

    // A save that fails says so, and the app is offered again.
    private void Answered(string id, Exception? error, Action? then)
    {
        if (error is null)
        {
            then?.Invoke();
            return;
        }
        _answered.Remove(id);
        Show(Notice.SaveFailed(error));
        FillNewApps();
    }

    private static bool SameId(string id, string? other) => string.Equals(id, other, StringComparison.OrdinalIgnoreCase);

    public void InstallChanged(InstallItem item)
    {
        var ended = item.Done is not null && string.Equals(_autoKey, Key(item.Request.Package), StringComparison.OrdinalIgnoreCase);
        if (ended) _autoKey = null;
        if (item.Done is { Phantom: false, Outcome.Result: UpgradeResult.Updated or UpgradeResult.RestartNeeded })
            _installed.Add((Key(item.Request.Package), item.Request.ToVersion));
        // A removed row still follows its install, so Undo shows where it is.
        if (!_rows.TryGetValue(Key(item.Request.Package), out var row))
        {
            // The next Auto app may go now.
            if (ended) Refresh();
            return;
        }
        row.Install = item;
        if (item.Done is { } done)
        {
            if (done.After is { } after) row.Check = after;
            if (done.Outcome.Result == UpgradeResult.Cancelled)
            {
                row.Install = null;
                // A cancelled update waits out the cooldown, whoever started it.
                if (row.Check.App.IsAuto(_settings.Current.Settings)) RememberAutoAttempt(row, _time.GetUtcNow());
            }
            else if (done.Outcome.Result == UpgradeResult.Updated && !done.Phantom && !row.IsRemoved) After(row, UpdatedShownFor, () => row.Install = null);
        }
        Refresh();
    }

    // The PC's state, the clock or the auto-install settings changed: the rows' waits and the footer follow.
    public void ConditionsChanged()
    {
        StopWindowTimer();
        Refresh();
    }

    // Tiny Tracker's own update moved on.
    public void SelfUpdateChanged() => Refresh();

    internal void UpdateSelf()
    {
        if (SelfRow.View.HasAction) _self?.Update(automatic: false);
    }

    internal void CancelSelf() => _self?.Cancel();

    internal void OpenSelfNotes()
    {
        if (_self?.State.Release is { } release) _openLink(release.NotesUrl);
    }

    // Leaving Choose apps drops rows of apps no longer tracked and checks added ones, after the ticks being saved. The first
    // time, a tip says where Windows puts the tray icon (spec §4.3); its save comes after, so the check doesn't wait for it.
    public void TrackedAppsChanged(bool added)
    {
        if (_writer.Idle.IsCompleted) ApplyTrackedApps(added);
        else _writer.Update(file => file, _ => ApplyTrackedApps(added));
        ShowTip(HiddenIconsTip, Notice.HiddenIconsTip);
    }

    private void ApplyTrackedApps(bool added)
    {
        if (_disposed) return;
        foreach (var key in _rows.Keys.ToList())
            if (!_rows[key].IsRemoved && !IsTracked(_rows[key].Check.App)) _rows.Remove(key);
        // New apps tracked from Choose apps or Restore.
        FillNewApps();
        if (added)
        {
            if (_checking) _checkAgain = true;
            else _scheduler.CheckNow();
        }
        Refresh();
    }

    [RelayCommand]
    private void CheckNow() => _scheduler.CheckNow();

    [RelayCommand(CanExecute = nameof(CanUpdateAll))]
    private void UpdateAll() => Enqueue(_rows.Values.Where(r => !r.IsRemoved && r.View.CountsForUpdateAll));

    [RelayCommand]
    private void OpenStore() => _openLink(Notice.AppInstallerStoreLink);

    [RelayCommand]
    private void ToggleUpToDate()
    {
        _expandedByUser = true;
        IsUpToDateExpanded = !IsUpToDateExpanded;
    }

    [RelayCommand]
    private void Dismiss(Notice notice) => Notices.Remove(notice);

    // The toasts' buttons: one prompt for every row that needs permission, and every app in use closed first.
    public void InstallNeedingPermission() => Enqueue(_rows.Values.Where(r => !r.IsRemoved && r.View.State == RowState.NeedsPermission));

    public void CloseAndUpdateInUse() => Enqueue(_rows.Values.Where(r => !r.IsRemoved && r.View.State == RowState.AppInUse), closeFirst: true);

    // Tiny Tracker's own icon comes from its uninstall entry.
    public string LocalIdOf(PackageKey package) => IsSelf(package) ? AppInfo.LocalId : _rows.TryGetValue(Key(package), out var row) ? row.LocalId : "";

    // History's Retry offers only what the row itself offers now, of this very version, and does what that button does.
    public bool CanRetry(PackageKey package, string version) => IsSelf(package) ? CanRetrySelf(version) : _rows.TryGetValue(Key(package), out var row) && CanRetry(row, version);

    public bool Retry(PackageKey package, string version)
    {
        if (IsSelf(package))
        {
            if (!CanRetrySelf(version)) return false;
            UpdateSelf();
            return true;
        }
        if (!_rows.TryGetValue(Key(package), out var row) || !CanRetry(row, version)) return false;
        Primary(row);
        return true;
    }

    private static bool IsSelf(PackageKey package) =>
        string.Equals(package.Id, SelfUpdater.Key.Id, StringComparison.OrdinalIgnoreCase) && string.Equals(package.Source, SelfUpdater.Key.Source, StringComparison.OrdinalIgnoreCase);

    private bool CanRetrySelf(string version) =>
        HasSelfUpdate && SelfRow.View.HasAction && _self?.State.Release?.Version is { } offered && SelfVersion.Parse(version) == offered;

    private static bool CanRetry(UpdateRow row, string version) =>
        !row.IsRemoved && row.View.Action is RowAction.Update or RowAction.Retry or RowAction.Install or RowAction.CloseAndUpdate && row.Check.Package is not null
        && row.Check.App.Offer is { } offer && PackageVersion.Same(offer.Version, version);

    internal void Primary(UpdateRow row)
    {
        switch (row.View.Action)
        {
            case RowAction.StopTracking:
                StopTracking(row);
                break;
            case RowAction.CloseAndUpdate:
                Enqueue([row], closeFirst: true);
                break;
            case RowAction.ForceClose:
                _installer.ForceClose(row.Key);
                break;
            case RowAction.None:
                break;
            default:
                Update(row);
                break;
        }
    }

    internal void Update(UpdateRow row) => Enqueue([row]);

    internal void Cancel(UpdateRow row) => _installer.Cancel(row.Key);

    internal void Skip(UpdateRow row)
    {
        if (row.Check.App.Offer is not { } offer) return;
        var version = offer.Version;
        // A failure of the skipped version is no longer anything to retry or update, unless the skip can't be saved.
        var failed = row.Install?.Done is not null ? row.Install : null;
        if (failed is not null) row.Install = null;
        var entry = new HistoryEntry
        {
            Time = _time.GetUtcNow(),
            Id = row.Key.Id,
            Source = row.Key.Source,
            Name = row.Name,
            Result = HistoryResult.Skipped,
            FromVersion = row.Check.Package?.InstalledVersion,
            ToVersion = version,
        };
        // History gets the skip once it's saved, unless an Undo or another skip came first.
        var skip = ++row.Skips;
        Change(row, app => app with { SkippedVersion = version }, check => check with { App = check.App with { SkippedVersion = version }, Status = AppStatus.Skipped }, () =>
        {
            if (row.Skips == skip) _historyWriter.Add(entry);
        }, () =>
        {
            if (row.Install is null) row.Install = failed;
        });
    }

    // Once saved, the skip's History entry goes too; one still waiting for its save isn't written at all.
    internal void UndoSkip(UpdateRow row)
    {
        row.Skips++;
        var skipped = row.Check.App.SkippedVersion;
        var status = row.Check.App.Offer is { Phantom: true } ? AppStatus.Phantom : row.Check.App.Offer is null ? AppStatus.UpToDate : AppStatus.Available;
        Change(row, app => app with { SkippedVersion = null }, check => check with { App = check.App with { SkippedVersion = null }, Status = status }, () =>
        {
            if (skipped is not null) _historyWriter.RemoveSkip(row.Key.Id, row.Key.Source, skipped);
        });
    }

    // The app's own choice; set back to the switch's value, it follows the switch again (spec §6.2).
    internal void ToggleAuto(UpdateRow row)
    {
        var settings = _settings.Current.Settings;
        var auto = !row.Check.App.IsAuto(settings);
        bool? choice = auto == settings.AutoUpdateApps ? null : auto;
        Change(row, app => app with { AutoChoice = choice }, check => check with { App = check.App with { AutoChoice = choice } });
    }

    // The notes' text shows on the What's new page; without it, the link opens.
    internal void OpenNotes(UpdateRow row)
    {
        if (row.Check.Package is not { } package) return;
        if (package.ReleaseNotes is not null)
        {
            WhatsNew.Show(row);
            NotesRequested?.Invoke(this, EventArgs.Empty);
        }
        else if (package.ReleaseNotesUrl is { } url) _openLink(url);
    }

    internal void StopTracking(UpdateRow row)
    {
        if (row.IsRemoved) return;
        if (row.View.IsActive) _installer.Cancel(row.Key);
        row.RemovedApp = _settings.Current.Apps.FirstOrDefault(a => a.Matches(row.Key.Id, row.Key.Source)) ?? row.Check.App;
        row.IsRemoved = true;
        _writer.Update(file => file with { Apps = file.Apps.Where(a => !a.Matches(row.Key.Id, row.Key.Source)).ToList() }, error =>
        {
            if (error is null) return;
            PutBack(row);
            Show(Notice.SaveFailed(error));
            Refresh();
        });
        After(row, UndoShownFor, () =>
        {
            if (!row.IsRemoved) return;
            _rows.Remove(Key(row.Key));
        });
        Refresh();
    }

    internal void UndoRemove(UpdateRow row)
    {
        if (!row.IsRemoved || row.RemovedApp is not { } app) return;
        PutBack(row);
        _writer.Update(file => file.Apps.Any(a => a.Matches(app.Id, app.Source)) ? file : file with { Apps = [.. file.Apps, app] }, error =>
        {
            if (error is null) return;
            Show(Notice.SaveFailed(error));
        });
        Refresh();
    }

    // The row may already have left the list after its 5 seconds. An update that finished meanwhile shows for its moment.
    private void PutBack(UpdateRow row)
    {
        row.Timer?.Dispose();
        row.Timer = null;
        row.IsRemoved = false;
        _rows.TryAdd(Key(row.Key), row);
        if (row.Install?.Done is { Phantom: false, Outcome.Result: UpgradeResult.Updated }) After(row, UpdatedShownFor, () => row.Install = null);
    }

    // What the last check found for a tracked app; Choose apps says why it can't list one.
    public AppStatus? StatusOf(string id, string source) => _rows.TryGetValue(Key(new PackageKey(id, source)), out var row) ? row.Check.Status : null;

    private static string Key(TrackedApp app) => Key(new PackageKey(app.Id, app.Source));

    private static string Key(PackageKey key) => $"{key.Source}|{key.Id}";

    private bool IsTracked(TrackedApp app) => _settings.Current.Apps.Any(a => a.Matches(app.Id, app.Source));

    // A finished install stops showing once the check offers something else.
    private static bool Outdated(InstallItem install, AppCheck check) =>
        check.Status is not (AppStatus.Available or AppStatus.Phantom) || check.App.Offer?.Version is not { } offered
        || !PackageVersion.Same(offered, install.Request.ToVersion);

    // Install goes through the helper; other updates as spec §6.3's table says. One that starts by itself never prompts.
    private void Enqueue(IEnumerable<UpdateRow> rows, bool byItself = false, bool closeFirst = false)
    {
        var silent = _settings.Current.Settings.SilentMode;
        var requests = rows
            .Where(r => r.Check.Package is not null && r.Check.App.Offer is not null && !r.View.IsActive)
            .Select(r =>
            {
                var version = r.Check.App.Offer!.Version;
                // Install, and what follows one for the same version (Close & update, Retry), goes through the helper.
                var needsAdmin = r.View.Action == RowAction.Install || r.Install?.Request is { NeedsAdmin: true } last && PackageVersion.Same(last.ToVersion, version);
                return new InstallRequest(r.Key, r.Name, r.Check.Package!.InstalledVersion, version)
                {
                    Route = needsAdmin ? InstallRoute.Helper : Routes.For(r.Check.Package!, silent),
                    NeedsAdmin = needsAdmin,
                    ByItself = byItself,
                    LocalId = r.Check.Package!.LocalId,
                    CloseFirst = closeFirst,
                };
            })
            .ToList();
        if (requests.Count > 0) _installer.Enqueue(requests);
    }

    // Shows the change at once and saves it off the UI thread; a change that can't be saved is undone.
    private void Change(UpdateRow row, Func<TrackedApp, TrackedApp> app, Func<AppCheck, AppCheck> check, Action? saved = null, Action? refused = null)
    {
        var before = row.Check;
        row.Check = check(row.Check);
        _writer.Update(file => file with { Apps = file.Apps.Select(a => a.Matches(row.Key.Id, row.Key.Source) ? app(a) : a).ToList() }, error =>
        {
            if (error is null)
            {
                saved?.Invoke();
                return;
            }
            row.Check = before;
            refused?.Invoke();
            Show(Notice.SaveFailed(error));
            Refresh();
        });
        Refresh();
    }

    // Runs once on the UI thread after a delay, unless the row starts another one first.
    private void After(UpdateRow row, TimeSpan delay, Action action)
    {
        row.Timer?.Dispose();
        ITimer? timer = null;
        timer = _time.CreateTimer(_ => _post(() =>
        {
            if (_disposed || row.Timer != timer) return;
            row.Timer = null;
            action();
            Refresh();
        }), null, delay, Timeout.InfiniteTimeSpan);
        row.Timer = timer;
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

    // History's file changed: its notice may be gone.
    public void FilesChanged() => RefreshFileNotices();

    private void RefreshFileNotices()
    {
        if (_settings.Unreadable) Show(Notice.SettingsUnreadable);
        else Hide(NoticeKind.SettingsUnreadable);
        if (_history.Unreadable) Show(Notice.HistoryUnreadable);
        else Hide(NoticeKind.HistoryUnreadable);
    }

    private void Refresh()
    {
        if (_disposed) return;
        var now = _time.GetUtcNow();
        var settings = _settings.Current.Settings;
        var window = InstallWindow.Of(settings);
        var windowStart = window is { } open ? Words.Hour(open.From, _culture()) : null;
        SystemState? system = null;
        foreach (var row in _rows.Values)
        {
            var isAuto = row.Check.App.IsAuto(settings);
            // An auto-install that failed for a reason that can pass is tried again once its cooldown is over.
            if (isAuto && row.Install?.Done?.Outcome.IsTemporary() == true
                && row.Check.App.Offer?.LastAutoAttempt is { } last && last <= now && now - last >= AutoInstallRules.AttemptCooldown)
                row.Install = null;
            // The rules matter only for an Auto app with an update and no install; Windows is asked once, and only then.
            var auto = !isAuto ? AutoBlock.AutoOff
                : row.Check.Status != AppStatus.Available || row.Install is not null ? AutoBlock.NotAvailable
                : AutoInstallRules.Check(row.Check, settings, system ??= _system.Read(), now, _time.LocalTimeZone);
            row.Show(now, auto, auto == AutoBlock.TooNew ? AutoInstallRules.DaysLeft(row.Check.App.Offer!, settings, now) : 0, windowStart, settings.ShowWhatsNew,
                settings.SecurityFirst, isAuto);
        }
        ShowSelf(settings, now, ref system, windowStart);
        var rows = _rows.Values.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var updates = rows.Where(r => !r.View.InUpToDateGroup || r.IsRemoved && Updates.Contains(r)).OrderBy(r => r.View.Rank).ToList();
        var upToDate = rows.Except(updates).ToList();
        CollectionSync.Apply(Updates, updates);
        CollectionSync.Apply(UpToDate, upToDate);

        // Tiny Tracker's own update is ready too, though Update all leaves it out.
        var pending = rows.Count(r => !r.IsRemoved && r.View.IsPending) + (HasSelfUpdate ? 1 : 0);
        var forAll = rows.Count(r => !r.IsRemoved && r.View.CountsForUpdateAll);
        IsEmpty = _settings.Current.Apps.Count == 0 && _rows.Count == 0;
        IsChecking = _checking;
        IsSpinning = _shown && _checking;
        HasUpdates = Updates.Count > 0 || HasSelfUpdate;
        HasUpToDate = UpToDate.Count > 0;
        UpToDateText = Words.UpToDate(UpToDate.Count);
        // Kept while its rows stay the same, so the icons don't reload during downloads.
        var preview = UpToDate.Take(4).ToList();
        if (!preview.SequenceEqual(UpToDatePreview)) UpToDatePreview = preview;
        if (!_expandedByUser) IsUpToDateExpanded = Updates.Count == 0;
        CanUpdateAll = forAll > 0;
        UpdateAllText = Words.Format(Strings.UpdateAll, forAll);
        Summary = IsEmpty ? ""
            : _checkedAt is { } checkedAt ? Words.Format(Strings.SummaryChecked, pending > 0 ? Words.UpdatesReady(pending) : Strings.AllUpToDate, Words.Ago(checkedAt, now))
            : _checking ? Strings.Checking : Strings.NotCheckedYet;
        NextCheck = _checking ? Strings.Checking
            : _scheduler.NextCheck is { } next ? Words.Format(Strings.NextCheckIn, Words.Until(next - now))
            : _scheduler.Hold switch
            {
                CheckHold.Offline => Strings.NextCheckWaitsForNetwork,
                CheckHold.BatterySaver => Strings.NextCheckWaitsForEnergySaver,
                // The scheduler started a check the page hasn't heard of yet.
                _ => Strings.Checking,
            };
        var installing = rows.FirstOrDefault(r => !r.IsRemoved && r.View.State is RowState.Downloading or RowState.Installing);
        var self = HasSelfUpdate && SelfRow.View.ShowProgress ? SelfRow.View : null;
        IsWorking = _checking || rows.Any(r => !r.IsRemoved && r.View.IsActive) || HasSelfUpdate && SelfRow.View.IsActive;
        var (name, percent) = installing is not null ? (installing.Name, installing.View is { Indeterminate: false } view ? (int)view.Percent : (int?)null)
            : self is not null ? (AppInfo.Name, self.Indeterminate ? null : (int?)self.Percent) : (null, null);
        Tray = TrayState.Of(IsWorking, _checking, name, percent, Problem, pending, IsEmpty, _checkedAt is not null);
        WatchFullScreen();
        WatchWindow(now, window);
        WhatsNew.Changed();
        RowsChanged?.Invoke(this, EventArgs.Empty);
        StartAutoInstall(now);
    }

    // One Auto app at a time, while nothing installs or checks, so rules are fresh (spec §6.2); the page hears of a check a turn
    // late, and a failed check's rows may be stale. A finished install waits for the user; a passing failure clears.
    private void StartAutoInstall(DateTimeOffset now)
    {
        if (_disposed || _checking || _scheduler.IsRunning || _autoKey is not null || _rows.Values.Any(r => r.View.IsActive) || HasSelfUpdate && SelfRow.View.IsActive) return;
        // Tiny Tracker's own update goes first: the app it restarts goes on with the rest.
        if (_selfAuto == AutoBlock.None)
        {
            _self!.Update(automatic: true);
            return;
        }
        if (LastProblem != CheckProblem.None) return;
        var next = _rows.Values
            .Where(r => !r.IsRemoved && r.AutoBlock == AutoBlock.None && !JustInstalled(r))
            .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .FirstOrDefault();
        if (next is null) return;
        _autoKey = Key(next.Key);
        RememberAutoAttempt(next, now);
        Enqueue([next], byItself: true);
    }

    private bool JustInstalled(UpdateRow row) => row.Check.App.Offer is { } offer
        && _installed.Any(i => string.Equals(i.Key, Key(row.Key), StringComparison.OrdinalIgnoreCase) && PackageVersion.Same(i.Version, offer.Version));

    // Shown at once and saved off the UI thread. Not undone when the save fails: that would also undo a change made meanwhile.
    private void RememberAutoAttempt(UpdateRow row, DateTimeOffset now)
    {
        if (row.Check.App.Offer is not { } offer) return;
        row.Check = row.Check with { App = row.Check.App with { Offer = offer with { LastAutoAttempt = now } } };
        var version = offer.Version;
        _writer.Update(file => file with
        {
            Apps = file.Apps.Select(a => a.Matches(row.Key.Id, row.Key.Source) && a.Offer is { } saved && PackageVersion.Same(saved.Version, version)
                ? a with { Offer = saved with { LastAutoAttempt = now } }
                : a).ToList(),
        });
    }

    // With silent mode on it installs by itself, so the row says why it waits, as an Auto app's does (spec §6.5). An automatic
    // attempt that failed for a reason that can pass goes again once its 12 hours are over (§6.2).
    private void ShowSelf(AppSettings settings, DateTimeOffset now, ref SystemState? system, string? windowStart)
    {
        if (_self is null) return;
        var state = _self.State;
        var again = state is { Stage: SelfUpdateStage.Failed, Automatic: true, Outcome: { } failed } && failed.IsTemporary();
        var auto = state is { Release: { } release } && (state.Stage == SelfUpdateStage.Available || again)
            ? SelfUpdateRules.Check(release, _self.Running, _settings.Current.SelfUpdate, settings, system ??= _system.Read(), now, _time.LocalTimeZone)
            : AutoBlock.AutoOff;
        _selfAuto = auto;
        var days = auto == AutoBlock.TooNew ? AutoInstallRules.DaysLeft(state.Release!.PublishedAt, settings, now) : 0;
        if (SelfUpdateView.Of(state, _self.Running, auto, days, windowStart, settings.ShowWhatsNew) is { } view) SelfRow.View = view;
        HasSelfUpdate = state.Stage != SelfUpdateStage.None && state.Release is not null;
    }

    private void WatchFullScreen()
    {
        var waiting = _rows.Values.Any(r => !r.IsRemoved && r.AutoBlock == AutoBlock.FullScreen) || _selfAuto == AutoBlock.FullScreen;
        if (waiting && _recheck is null) _recheck = _time.CreateTimer(_ => _post(Refresh), null, FullScreenRecheck, FullScreenRecheck);
        else if (!waiting && _recheck is not null)
        {
            _recheck.Dispose();
            _recheck = null;
        }
    }

    // While an update waits for the install window, one timer runs to its start. ConditionsChanged finds the start again.
    private void WatchWindow(DateTimeOffset now, InstallWindow? window)
    {
        var waiting = _rows.Values.Any(r => !r.IsRemoved && r.AutoBlock == AutoBlock.OutsideWindow) || _selfAuto == AutoBlock.OutsideWindow;
        if (waiting && window is { } open)
            _windowOpens ??= _time.CreateTimer(_ => _post(WindowOpened), null, open.NextStart(now, _time.LocalTimeZone) - now, Timeout.InfiniteTimeSpan);
        else StopWindowTimer();
    }

    private void WindowOpened()
    {
        StopWindowTimer();
        Refresh();
    }

    private void StopWindowTimer()
    {
        _windowOpens?.Dispose();
        _windowOpens = null;
    }
}
