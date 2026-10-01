using System.Globalization;
using System.Net.Http.Headers;
using TinyTracker.App.Interop;
using TinyTracker.App.Notifications;
using TinyTracker.Core;
using TinyTracker.Core.Checking;
using TinyTracker.Core.Elevation;
using TinyTracker.Core.Installing;
using TinyTracker.Core.Inventory;
using TinyTracker.Core.Launch;
using TinyTracker.Core.Logging;
using TinyTracker.Core.Scheduling;
using TinyTracker.Core.SelfUpdate;
using TinyTracker.Core.Storage;
using TinyTracker.Presentation;
using TinyTracker.Presentation.Choose;
#if DEBUG
using TinyTracker.Presentation.Demo;
#endif
using TinyTracker.Presentation.History;
using TinyTracker.Presentation.Settings;
using TinyTracker.Presentation.Shell;
using TinyTracker.Presentation.Updates;
using TinyTracker.WinGet;
using TinyTracker.WinGet.Cli;
using TinyTracker.WinGet.Closing;
using TinyTracker.WinGet.Elevation;
using TinyTracker.WinGet.ReleaseDates;
using TinyTracker.WinGet.SelfUpdate;

namespace TinyTracker.App;

// Everything the app runs on, built once on the UI thread and stopped on Quit.
public sealed class AppServices
{
    private readonly HttpClient _http = new();
    private readonly UiInbox _inbox;
    private readonly SettingsWriter _writer;
    private readonly HistoryWriter _historyWriter;
    private readonly SaveDrain _drain;

    // owner: the window a UAC prompt belongs to.
    public AppServices(Action<Action> post, Action<string> openLink, Func<bool> flyoutOpen, Func<nint> owner, IShortcutKeys keys, bool demo)
    {
        var time = TimeProvider.System;
        Demo = demo;
#if DEBUG
        Paths = demo ? new DataPaths(DemoFolder.Create(Path.GetTempPath())) : DataPaths.ForCurrentUser();
#else
        Paths = DataPaths.ForCurrentUser();
#endif
        FirstRun = !demo && !File.Exists(Paths.Settings);
        Log = new FileLog(Paths.Log, time);
        _inbox = new UiInbox(post, Log);
        Settings = new SettingsStore(Paths.Settings);
        var settingsRecovered = Settings.Load();
        History = new HistoryStore(Paths.History, time);
        var historyRecovered = History.Load();
#if DEBUG
        if (demo) foreach (var entry in DemoWinGet.History(time.GetUtcNow())) History.Add(entry);
#endif
        Version = typeof(AppServices).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        // raw.githubusercontent.com asks clients to say who they are.
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TinyTracker", Version));
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue($"(+{AppInfo.RepositoryUrl})"));

        IPackageSource source;
        IPackageUpgrader upgrader;
        IAppInventory inventory;
        IReleaseDates dates;
        ISilentMode silent;
        IElevation admin;
        IAppCloser closer;
        IProxyOption proxy;
        ISelfReleases? releases;
        InstallTimings? timings = null;
        Func<bool> setupRunning = () => false;
        var helperPath = Path.Combine(AppContext.BaseDirectory, "TinyTracker.Helper.exe");
        Limit = new SpeedLimit();
        var elevation = new HelperLauncher(helperPath, () => Settings.Current.Settings.SilentMode, owner, demo, () => _inbox.Deliver(SilentModeTaskMissing), Prompted, Limit);
#if DEBUG
        if (demo)
        {
            var fake = new DemoWinGet(time, Limit);
            (source, upgrader, inventory, dates, timings) = (fake, fake, fake, fake, DemoWinGet.Timings);
            silent = new DemoSilentMode();
            admin = new DemoHelperBridge(elevation, fake);
            closer = fake;
            proxy = new DemoProxyOption(admin);
            releases = new DemoSelfUpdate(time);
            Settings.Update(_ => DemoWinGet.Settings(time.GetUtcNow(), time.LocalTimeZone));
        }
        else
#endif
        {
            source = new WinGetPackageSource(async ct => await WinGetSession.OpenAsync(ct));
            inventory = new WinGetInventory(async ct => await WinGetSession.OpenAsync(ct));
            upgrader = new WinGetUpgrader(Limit);
            dates = new GitHubReleaseDates(_http, time);
            silent = new SilentModeSwitch(elevation, new HelperLauncher(helperPath, () => false, owner, demo: false, prompting: Prompted), helperPath);
            admin = elevation;
            closer = new AppCloser(AppCloser.FolderLookup(AppContext.BaseDirectory), AppCloser.OthersInside, Log);
            proxy = new ProxyOption(WinGetCli.Real, elevation);
            // Only a copy its Setup installed updates itself (spec §6.5).
            var installed = string.Equals(Environment.ProcessPath, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppInfo.Name, "TinyTracker.exe"),
                StringComparison.OrdinalIgnoreCase);
            releases = installed ? new GitHubReleases(_http, time) : null;
            // Setup holds its mutex while it runs; one a first virus scan slows takes it late, but runs from the update folder.
            var update = SetupHandOff.FolderIn(AppContext.BaseDirectory);
            setupRunning = () => SetupMutex.Held() || RunningApps.AnyIn(update);
        }
        Limit.Set(SpeedLimit.Of(Settings.Current.Settings));

        Conditions = new SystemConditions();
        Scheduler = new CheckScheduler(time, TimeSpan.FromHours(Settings.Current.Settings.CheckIntervalHours));
        Runner = new CheckRunner(Scheduler, Settings, source, dates, time, Log);
        Queue = new InstallQueue(upgrader, source, Settings, History, time, Log, timings, admin, closer, Limit);
        if (releases is not null && SelfVersion.Parse(Version) is { } running)
        {
            SelfUpdate = new SelfUpdater(running, admin, Queue, Settings, History, time, Log, setupRunning);
            SelfCheck = new SelfUpdateCheck(Scheduler, releases, SelfUpdate.Offer, time, Log);
#if DEBUG
            (releases as DemoSelfUpdate)?.Follow(SelfUpdate);
#endif
        }
        _writer = new SettingsWriter(Settings, Log, post);
        _historyWriter = new HistoryWriter(History, Log, post);
        _drain = new SaveDrain(_writer, _historyWriter, post);
        Updates = new UpdatesViewModel(Scheduler, Queue, Conditions, Settings, _writer, History, _historyWriter, time, post, openLink, SelfUpdate);
        Choose = new ChooseAppsViewModel(inventory, Settings, _writer, Log, post, Updates.StatusOf);
#if DEBUG
        // The demo never touches the registry.
        Startup = new StartupEntry(demo ? new DemoStartupValues() : new RegistryStartupValues(), Environment.ProcessPath!);
#else
        Startup = new StartupEntry(new RegistryStartupValues(), Environment.ProcessPath!);
#endif
        SettingsView = new SettingsViewModel(Settings, _writer, Scheduler, Startup, new Desktop(openLink, demo, Log, owner, Prompted), source, keys, silent, proxy, Limit,
            time, Log, post, () => (Updates.LastCheckAt, Updates.LastProblem, Updates.LastGoodCheckAt), Version, Path.GetDirectoryName(Paths.Log)!,
            installed: SelfUpdate is not null);
        HistoryView = new HistoryViewModel(History, _historyWriter, Updates, time, () => CultureInfo.CurrentCulture);
        Toasts = new ToastService(demo, Log);
        Announcer = new Announcer(Toasts, Conditions, Settings, _writer, flyoutOpen, time, post);
        History.Changed += (_, _) => _inbox.Deliver(() =>
        {
            HistoryView.Changed();
            Updates.FilesChanged();
        });
        Scheduler.CheckDue += _inbox.For<CheckTicket>(_ => Updates.CheckStarted());
        Runner.Completed += _inbox.For<CheckCompleted>(check =>
        {
            Updates.CheckFinished(check);
            Announcer.CheckFinished(check);
        });
        Queue.Changed += _inbox.For<InstallItem>(item =>
        {
            Updates.InstallChanged(item);
            Announcer.InstallChanged(item);
        });
        Queue.AdminFallback += (_, _) => _inbox.Deliver(Updates.AdminFallback);
        Queue.SpeedLimitRefused += (_, _) => _inbox.Deliver(SettingsView.SpeedLimitOptionOff);
        Conditions.Changed += (_, _) => _inbox.Deliver(ApplyConditions);
        SettingsView.AppsRestored += (_, _) => Updates.TrackedAppsChanged(added: true);
        SettingsView.AutoRulesChanged += (_, _) =>
        {
            Updates.ConditionsChanged();
            Announcer.AutoRulesChanged();
        };
        if (SelfUpdate is { } self)
        {
            self.Changed += _inbox.For<SelfUpdateState>(state =>
            {
                Updates.SelfUpdateChanged();
                Announcer.SelfUpdateChanged(state);
            });
            self.UpdatedByItself += _inbox.For<SelfVersion>(Announcer.SelfUpdated);
            // What came of a self-update that the last run started.
            _ = Task.Run(() => self.Restarted(self.Running));
        }
        ApplyConditions();
        Updates.ShowStartupNotices(settingsRecovered, historyRecovered);
        SettingsView.KeepSilentModeHonest();
        SettingsView.KeepSpeedLimitHonest();
    }

    public bool Demo { get; }
    // No settings file yet: the flyout opens on Choose apps.
    public bool FirstRun { get; }
    public string Version { get; }
    public DataPaths Paths { get; }
    public FileLog Log { get; }
    public SettingsStore Settings { get; }
    public HistoryStore History { get; }
    // Downloads run under it, as Settings saves it (spec §6.4).
    public SpeedLimit Limit { get; }
    public CheckScheduler Scheduler { get; }
    public CheckRunner Runner { get; }
    public InstallQueue Queue { get; }
    // Tiny Tracker's own update, for an installed copy or the demo.
    public SelfUpdater? SelfUpdate { get; }
    public SelfUpdateCheck? SelfCheck { get; }
    public UpdatesViewModel Updates { get; }
    public ChooseAppsViewModel Choose { get; }
    public StartupEntry Startup { get; }
    internal SystemConditions Conditions { get; }
    internal ToastService Toasts { get; }
    public Announcer Announcer { get; }
    public SettingsViewModel SettingsView { get; }
    public HistoryViewModel HistoryView { get; }

    // Raised on the prompt's own thread as a UAC prompt opens and closes.
    public event EventHandler<bool>? Prompting;

    // After sleep or a clock change: a check that came due runs a minute later (spec §6.1).
    public void Resumed()
    {
        Scheduler.Resumed();
        ApplyConditions();
    }

    // Signing out ends the process without Quit: toasts go now, and saves in flight get a moment. The demo's folder goes too,
    // once nothing can save into it.
    public void EndSession()
    {
        Toasts.ClearHistory();
        Toasts.RemoveDemo();
        var stopped = Task.CompletedTask;
        if (Demo)
        {
            Announcer.Dispose();
            _inbox.Dispose();
            SelfCheck?.Dispose();
            SelfUpdate?.Dispose();
            Queue.Dispose();
            Runner.Dispose();
            stopped = Task.WhenAll(Queue.Stopped, Runner.Stopped, SelfUpdate?.Stopped ?? Task.CompletedTask, SelfCheck?.Stopped ?? Task.CompletedTask);
        }
        // A failed save was logged already, so the wait never throws.
        var ended = Task.WhenAll(_writer.Idle, _historyWriter.Idle, stopped)
            .ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default)
            .Wait(TimeSpan.FromSeconds(1));
#if DEBUG
        if (!Demo || !ended) return;
        Log.Close();
        DemoFolder.Delete(Paths.Root);
#endif
    }

    // A new time zone or time format shows at once.
    public void RegionChanged()
    {
        Updates.ConditionsChanged();
        HistoryView.Changed();
        SettingsView.RegionChanged();
    }

    // A click found silent mode's task gone.
    private void SilentModeTaskMissing() => SettingsView.SilentModeTaskMissing();

    private void Prompted(bool open) => Prompting?.Invoke(this, open);

    // Checks wait while offline or in Energy saver; the rows and footer follow (spec §6.1, §6.2).
    private void ApplyConditions()
    {
        Scheduler.SetConditions(Conditions.Online, Conditions.BatterySaver);
        Updates.ConditionsChanged();
    }

    // A download stops; an installer that already started finishes on its own.
    // The last saves land over two UI turns; done runs on the second.
    public void Quit(Action done)
    {
        Conditions.Dispose();
        Announcer.Dispose();
        _inbox.Dispose();
        SelfCheck?.Dispose();
        SelfUpdate?.Dispose();
        Queue.Dispose();
        Runner.Dispose();
        Scheduler.Dispose();
        Updates.Dispose();
        SettingsView.Dispose();
        _http.Dispose();
        _drain.Drain(Task.WhenAll(Queue.Stopped, SelfUpdate?.Stopped ?? Task.CompletedTask, SelfCheck?.Stopped ?? Task.CompletedTask,
            Demo ? Runner.Stopped : Task.CompletedTask), () =>
        {
#if DEBUG
            // In the demo, a late save would bring the folder back.
            if (Demo)
            {
                Log.Close();
                DemoFolder.Delete(Paths.Root);
            }
#endif
            done();
        });
    }
}
