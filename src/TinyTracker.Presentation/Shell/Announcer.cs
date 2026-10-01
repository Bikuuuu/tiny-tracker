using TinyTracker.Core;
using TinyTracker.Core.Checking;
using TinyTracker.Core.Installing;
using TinyTracker.Core.SelfUpdate;
using TinyTracker.Core.Settings;
using TinyTracker.Core.Storage;
using TinyTracker.Core.Tracking;
using TinyTracker.Core.Versions;
using TinyTracker.Presentation.Settings;
using TinyTracker.Presentation.Text;
using TinyTracker.Presentation.Updates;

namespace TinyTracker.Presentation.Shell;

// The toasts (spec §4.7): new versions to install or allow, once each, apps to close, finished batches and our own update. The
// level picks which show; the rest and what the flyout shows count as seen. Full screen and a lock hold them. UI thread.
public sealed class Announcer : IDisposable
{
    // The queue idle this long ends a batch, so Auto apps installing one after another make one toast.
    public static readonly TimeSpan BatchQuiet = TimeSpan.FromSeconds(3);
    // Windows has no event for a game ending or the PC unlocking, so waiting toasts look again this often.
    public static readonly TimeSpan BusyRecheck = TimeSpan.FromMinutes(1);

    private readonly IToasts _toasts;
    private readonly ISystemConditions _system;
    private readonly SettingsStore _settings;
    private readonly SettingsWriter _writer;
    private readonly Func<bool> _flyoutOpen;
    private readonly TimeProvider _time;
    private readonly Action<Action> _post;
    private static readonly string[] Kinds = ["ready", "permission", "close", "batch", "self", "selfUpdated", "selfFailed"];

    // New versions not announced yet, by app: ones the user installs, and Auto ones that wait for permission.
    private readonly Dictionary<string, (string Name, string Version)> _ready = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Name, string Version)> _permission = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _installing = new(StringComparer.OrdinalIgnoreCase);
    // Versions installed while the app runs: a check that started before an install still offers them.
    private readonly List<(string Key, string Version)> _installed = [];
    // Versions announced while the app runs; Offer.Announced keeps them for the next start.
    private readonly List<(string Key, string Version)> _announced = [];
    private Batch _batch = new();
    // A batch that ended and waits to be announced.
    private Batch? _ended;
    private ITimer? _quiet;
    private ITimer? _recheck;
    private bool _disposed;
    // Tiny Tracker's own update as last heard, a version that waits to be announced, one it installed by itself, and an automatic
    // one that failed for good, with the last such failure told.
    private SelfUpdateState? _self;
    private SelfRelease? _selfReady;
    private SelfVersion? _selfUpdated;
    private SelfUpdateState? _selfFailed;
    private (SelfVersion Version, UpgradeOutcome Outcome)? _selfFailedTold;
    // Announced while the app runs; SelfUpdateBook.Announced keeps it for the next start.
    private SelfVersion? _selfAnnounced;

    public Announcer(IToasts toasts, ISystemConditions system, SettingsStore settings, SettingsWriter writer, Func<bool> flyoutOpen, TimeProvider time, Action<Action> post)
    {
        _toasts = toasts;
        _system = system;
        _settings = settings;
        _writer = writer;
        _flyoutOpen = flyoutOpen;
        _time = time;
        _post = post;
    }

    public void CheckFinished(CheckCompleted check)
    {
        foreach (var app in check.Apps)
        {
            // A version no longer offered is news again if it ever comes back.
            var key = Key(app.App.Id, app.App.Source);
            _announced.RemoveAll(i => string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase) && !PackageVersion.Same(i.Version, app.App.Offer?.Version));
            _installed.RemoveAll(i => string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase) && !PackageVersion.Same(i.Version, app.App.Offer?.Version));
            Consider(app);
        }
        Announce();
    }

    public void InstallChanged(InstallItem item)
    {
        var key = Key(item.Request.Package.Id, item.Request.Package.Source);
        if (item.Done is not { } done)
        {
            _installing.Add(key);
            _quiet?.Dispose();
            _quiet = null;
            return;
        }
        _installing.Remove(key);
        // One that ends while the flyout is open is on the page: seen.
        var batch = _flyoutOpen() ? new Batch() : _batch;
        switch (done.Outcome.Result)
        {
            case UpgradeResult.Updated or UpgradeResult.RestartNeeded:
                batch.Installed.Add(item.Request.Name);
                if (done.Outcome.Result == UpgradeResult.RestartNeeded) batch.Restart.Add(item.Request.Name);
                _ready.Remove(key);
                _permission.Remove(key);
                _installed.Add((key, item.Request.ToVersion));
                break;
            // The user chose it.
            case UpgradeResult.Cancelled or UpgradeResult.PermissionDeclined:
                break;
            // Each has its own toast.
            case UpgradeResult.NeedsAdmin:
                batch.Permission.Add((item.Request.Name, key, item.Request.ToVersion));
                break;
            case UpgradeResult.AppInUse:
                batch.InUse.Add(item.Request.Name);
                break;
            default:
                batch.Failed.Add(item.Request.Name);
                break;
        }
        // Reading the app again after its install can find a newer version first.
        if (done.After is { } after) Consider(after);
        if (_installing.Count > 0) return;
        if (_batch.IsEmpty)
        {
            Announce();
            return;
        }
        _quiet?.Dispose();
        ITimer? timer = null;
        timer = _time.CreateTimer(_ => _post(() =>
        {
            if (_quiet != timer) return;
            _quiet = null;
            timer?.Dispose();
            EndBatch();
        }), null, BatchQuiet, Timeout.InfiniteTimeSpan);
        _quiet = timer;
    }

    // What the flyout shows counts as announced, installs that ended too, and its toasts leave the notification center.
    public void FlyoutOpened()
    {
        Drop();
        _batch = new Batch();
        MarkAnnounced([.. _settings.Current.Apps.Where(a => a.Offer is { Announced: false }).Select(a => (Key(a.Id, a.Source), a.Offer!.Version))]);
        if (_self is { Stage: not SelfUpdateStage.None, Release: { } shown }) MarkSelfAnnounced(shown.Version);
        foreach (var kind in Kinds) _toasts.Hide(kind);
    }

    // Tiny Tracker's own update moved on: one that waits for the click is news once per version. With Update Tiny Tracker
    // automatically and silent mode on, it installs by itself, so it isn't.
    public void SelfUpdateChanged(SelfUpdateState state)
    {
        _self = state;
        var settings = _settings.Current.Settings;
        _selfReady = state is { Stage: SelfUpdateStage.Available, Declined: false, Release: { } release } && (!settings.AutoSelfUpdate || !settings.SilentMode)
            && !SelfAnnounced(release.Version) ? release : null;
        // One whose reason can pass is tried again after 12 h, and a clicked one shows in the flyout the user is in. A failure is
        // told once, whatever release date a later check brings; a retry or a newer release makes a held one old news.
        if (state is { Stage: SelfUpdateStage.Failed, Automatic: true, Outcome: { } outcome, Release: { } failed } && !outcome.IsTemporary())
        {
            if (_selfFailedTold != (failed.Version, outcome)) _selfFailed = state;
            _selfFailedTold = (failed.Version, outcome);
        }
        else if (state.Stage != SelfUpdateStage.Failed) _selfFailed = null;
        Announce();
    }

    // Silent mode or Update Tiny Tracker automatically changed, so its own update may now wait for the click, or no longer.
    // Update apps automatically calls this too, and changes nothing here.
    public void AutoRulesChanged()
    {
        if (_self is { } self) SelfUpdateChanged(self);
    }

    // It installed by itself, and the restarted app says so.
    public void SelfUpdated(SelfVersion version)
    {
        _selfUpdated = version;
        Announce();
    }

    public void Dispose()
    {
        _disposed = true;
        _quiet?.Dispose();
        _recheck?.Dispose();
    }

    // A version is news until a toast names it or the flyout shows it (Offer.Announced keeps that across starts); an Auto app
    // that installs by itself isn't, one waiting for permission has its own toast. Each check says afresh what's still news.
    private void Consider(AppCheck app)
    {
        var key = Key(app.App.Id, app.App.Source);
        var version = app.App.Offer?.Version;
        _ready.Remove(key);
        _permission.Remove(key);
        if (app.Status != AppStatus.Available || app.App.Offer is not { Announced: false } || Listed(_announced, key, version) || Listed(_installed, key, version)) return;
        if (!app.App.IsAuto(_settings.Current.Settings) || app.Package is null) _ready[key] = (NameOf(app.App), version!);
        else if (AutoInstallRules.WaitsForPermission(app.Package, _settings.Current.Settings)) _permission[key] = (NameOf(app.App), version!);
    }

    private void Drop()
    {
        // A version the permission toast named for an install is news no more, either.
        MarkAnnounced([.. _ready.Concat(_permission).Select(r => (r.Key, r.Value.Version)), .. _ended?.Permission.Select(p => (p.Key, p.Version)) ?? []]);
        MarkSelfAnnounced(_selfReady?.Version);
        _ready.Clear();
        _permission.Clear();
        _ended = null;
        _selfReady = null;
        _selfUpdated = null;
        _selfFailed = null;
        Recheck(false);
    }

    private void EndBatch()
    {
        if (_disposed || _installing.Count > 0 || _batch.IsEmpty) return;
        _ended = _ended is null ? _batch : _ended.With(_batch);
        _batch = new Batch();
        Announce();
    }

    private void Announce()
    {
        if (_disposed) return;
        if (_ready.Count == 0 && _permission.Count == 0 && _ended is null && _selfReady is null && _selfUpdated is null && _selfFailed is null)
        {
            Recheck(false);
            return;
        }
        var toasts = Toasts(_settings.Current.Settings.Notifications);
        if (toasts.Count == 0 || _flyoutOpen())
        {
            Drop();
            return;
        }
        if (_system.Busy())
        {
            Recheck(true);
            return;
        }
        foreach (var toast in toasts) _toasts.Show(toast);
        Drop();
    }

    // What waits, as far as the level shows it.
    private List<Toast> Toasts(NotificationLevel level)
    {
        var toasts = new List<Toast>();
        if (_ready.Count > 0 && level.Shows(ToastNews.Ready)) toasts.Add(ReadyToast());
        var permission = Sorted(_permission.Values.Select(p => p.Name).Concat(_ended?.Permission.Select(p => p.Name) ?? []));
        if (permission.Count > 0 && level.Shows(ToastNews.Permission)) toasts.Add(PermissionToast(permission));
        if (_ended is { InUse.Count: > 0 } && level.Shows(ToastNews.Close)) toasts.Add(CloseToast(Sorted(_ended.InUse)));
        if (_ended is { HasResults: true } batch && Shows(level, batch)) toasts.Add(BatchToast(batch));
        if (_selfReady is { } ready && level.Shows(ToastNews.SelfAvailable)) toasts.Add(SelfReadyToast(ready));
        if (_selfUpdated is { } updated && level.Shows(ToastNews.SelfUpdated)) toasts.Add(SelfUpdatedToast(updated));
        if (_selfFailed is { } failed && level.Shows(ToastNews.SelfFailed)) toasts.Add(SelfFailedToast(failed));
        return toasts;
    }

    // A batch shows when the level shows any of what it holds.
    private static bool Shows(NotificationLevel level, Batch batch) =>
        batch.Failed.Count > 0 && level.Shows(ToastNews.Failed) || batch.Restart.Count > 0 && level.Shows(ToastNews.Restart)
        || batch.Installed.Count > 0 && level.Shows(ToastNews.Installed);

    // Noted at once and saved off the UI thread.
    private void MarkAnnounced(List<(string Key, string Version)> versions)
    {
        var fresh = versions.Where(v => !Listed(_announced, v.Key, v.Version)).ToList();
        if (fresh.Count == 0) return;
        _announced.AddRange(fresh);
        _writer.Update(file => file with
        {
            Apps = file.Apps.Select(a => a.Offer is { Announced: false } offer && Listed(fresh, Key(a.Id, a.Source), offer.Version)
                ? a with { Offer = offer with { Announced = true } }
                : a).ToList(),
        });
    }

    // Noted at once and saved off the UI thread; it and older versions are news no more.
    private void MarkSelfAnnounced(SelfVersion? version)
    {
        if (version is null || SelfAnnounced(version)) return;
        _selfAnnounced = version;
        _writer.Update(file => SelfVersion.Parse(file.SelfUpdate.Announced) is { } saved && !version.IsNewerThan(saved)
            ? file
            : file with { SelfUpdate = file.SelfUpdate with { Announced = version.ToString() } });
    }

    private bool SelfAnnounced(SelfVersion version) =>
        _selfAnnounced is { } known && !version.IsNewerThan(known)
        || SelfVersion.Parse(_settings.Current.SelfUpdate.Announced) is { } saved && !version.IsNewerThan(saved);

    private static bool Listed(List<(string Key, string Version)> list, string key, string? version) =>
        list.Any(i => string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase) && PackageVersion.Same(i.Version, version));

    private void Recheck(bool on)
    {
        if (on) _recheck ??= _time.CreateTimer(_ => _post(Announce), null, BusyRecheck, BusyRecheck);
        else if (_recheck is not null)
        {
            _recheck.Dispose();
            _recheck = null;
        }
    }

    private Toast ReadyToast() =>
        new("ready", Words.UpdatesReady(_ready.Count), Words.Names(Sorted(_ready.Values.Select(r => r.Name))),
            [new ToastButton(Strings.ToastUpdateAll, ToastAction.UpdateAll), new ToastButton(Strings.ToastView, ToastAction.View)]);

    private static Toast PermissionToast(List<string> names) =>
        new("permission", Words.NeedPermission(names.Count), Words.Names(names),
            [new ToastButton(Strings.Install, ToastAction.Install), new ToastButton(Strings.ToastLater, ToastAction.Later)]);

    // One app is named in the title.
    private static Toast CloseToast(List<string> names) =>
        new("close", Words.NeedToClose(names), names.Count == 1 ? "" : Words.Names(names),
            [new ToastButton(Strings.CloseAndUpdate, ToastAction.CloseAndUpdate), new ToastButton(Strings.ToastLater, ToastAction.Later)]);

    private static Toast BatchToast(Batch batch)
    {
        var (installed, failed) = (batch.Installed.Count, batch.Failed.Count);
        var title = failed == 0 ? Words.Installed(installed)
            : installed == 0 ? Words.Failed(failed)
            : Words.Format(Strings.BatchMixed, Words.Installed(installed), failed);
        var body = batch.Restart.Count switch
        {
            0 => Words.Names(Sorted(batch.Installed.Concat(batch.Failed))),
            1 => Words.Format(Strings.RestartToFinishApp, batch.Restart[0]),
            var apps => Words.Format(Strings.RestartToFinishApps, apps),
        };
        return new Toast("batch", title, body, [new ToastButton(Strings.ToastView, ToastAction.View)]);
    }

    private static Toast SelfReadyToast(SelfRelease release) =>
        new("self", Words.Format(Strings.SelfUpdateReady, AppInfo.Name, release.Version), "",
            [new ToastButton(Strings.Update, ToastAction.SelfUpdate), new ToastButton(Strings.ToastLater, ToastAction.Later)]);

    private static Toast SelfUpdatedToast(SelfVersion version) =>
        new("selfUpdated", Words.Format(Strings.SelfUpdated, AppInfo.Name, version), "", [new ToastButton(Strings.WhatsNew, ToastAction.WhatsNew)]);

    private static Toast SelfFailedToast(SelfUpdateState state) =>
        new("selfFailed", Words.Format(Strings.SelfUpdateFailed, AppInfo.Name), SelfUpdateView.Reason(state.Outcome), [new ToastButton(Strings.ToastView, ToastAction.View)]);

    private static List<string> Sorted(IEnumerable<string> names) => [.. names.Distinct().Order(StringComparer.CurrentCultureIgnoreCase)];

    private static string Key(string id, string source) => $"{source}|{id}";

    private static string NameOf(TrackedApp app) => app.Name.Length > 0 ? app.Name : app.Id;

    private sealed class Batch
    {
        public List<string> Installed { get; } = [];
        public List<string> Failed { get; } = [];
        public List<string> Restart { get; } = [];
        public List<(string Name, string Key, string Version)> Permission { get; } = [];
        public List<string> InUse { get; } = [];

        // What the batch toast counts.
        public bool HasResults => Installed.Count > 0 || Failed.Count > 0;

        public bool IsEmpty => !HasResults && Permission.Count == 0 && InUse.Count == 0;

        public Batch With(Batch later)
        {
            Installed.AddRange(later.Installed);
            Failed.AddRange(later.Failed);
            Restart.AddRange(later.Restart);
            Permission.AddRange(later.Permission);
            InUse.AddRange(later.InUse);
            return this;
        }
    }
}
