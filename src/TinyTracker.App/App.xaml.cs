using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using TinyTracker.App.Interop;
using TinyTracker.App.Pages;
using TinyTracker.App.Tray;
using TinyTracker.Core;
using TinyTracker.Core.Launch;
using TinyTracker.Presentation;
using TinyTracker.Presentation.Settings;
using TinyTracker.Presentation.Shell;
using TinyTracker.Presentation.Updates;

namespace TinyTracker.App;

public partial class App : Application
{
    private const int MenuOpen = 1;
    private const int MenuCheckNow = 2;
    private const int MenuUpdateAll = 3;
    private const int MenuSettings = 4;
    private const int MenuQuit = 9;
    private AppServices? _services;
    private TrayIcon? _tray;
    private HotKey? _hotKey;
    private FlyoutWindow? _flyout;
    private bool _idle;
    private bool _quitting;

    public App()
    {
        InitializeComponent();
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
    }

    // Demo mode tries the UI on made-up apps. It exists only in Debug builds.
    public static bool IsDemo(IReadOnlyList<string> args) =>
#if DEBUG
        LaunchPolicy.IsDemo(args);
#else
        false;
#endif

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var commandLine = Environment.GetCommandLineArgs().Skip(1).ToArray();
        // Windows offers to start it again after a crash, and an installer that closed it starts it again (spec §6.7).
        // First, so an installer never finds it unregistered.
        var restart = IsDemo(commandLine) ? 0 : NativeMethods.RegisterApplicationRestart(LaunchPolicy.StartupFlag, 0);
        var flyout = new FlyoutWindow();
        _flyout = flyout;
        var ui = flyout.DispatcherQueue;
        // The tray's hidden window also carries the shortcut.
        var tray = new TrayIcon(Asset("tray.ico"), AppInfo.Name) { Menu = Menu };
        _tray = tray;
        var hotKey = new HotKey(tray.Handle);
        _hotKey = hotKey;
        var services = new AppServices(action => ui.TryEnqueue(() => action()), OpenLink, () => flyout.IsOpen, () => flyout.Handle, hotKey, IsDemo(commandLine));
        _services = services;
        if (hotKey.TryUse(services.Settings.Current.Settings.OpenShortcut) is var problem and not ShortcutProblem.None)
            services.Log.Warn(problem == ShortcutProblem.InUse ? "The shortcut is in use by another app" : $"Shortcut not set: error {hotKey.Error}");
        flyout.Start(services);
        flyout.OpenChanged += (_, _) => UpdateIdle();
        services.Prompting += (_, open) => flyout.Prompting(open);
        flyout.Prewarm();

        if (restart != 0) services.Log.Warn($"Restart not registered: 0x{restart:X8}");

        // A crashed copy's toasts can't be clicked any more.
        if (services.Toasts.Register() is string toastError) services.Log.Error($"Toast registration failed: {toastError}");
        services.Toasts.ClearHistory();
        services.Toasts.Activated += (_, action) => ui.TryEnqueue(() => OnToast(action));

        tray.Activated += (_, _) => flyout.OnTrayClick();
        tray.MenuCommand += (_, id) => OnMenu(id);
        tray.CloseRequested += (_, _) => Quit();
        tray.Message += (_, message) => OnWindowMessage(message);
        tray.Show();
        services.Updates.PropertyChanged += OnUpdatesChanged;
        ShowTray(services.Updates.Tray);

        // A second start hands over its command line: a second --startup, as at sign-in, changes nothing (spec §6.7).
        AppInstance.GetCurrent().Activated += (_, e) =>
        {
            if (e.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch && !LaunchPolicy.OpenFlyoutOnSecondStart(launch.Arguments)) return;
            ui.TryEnqueue(() => flyout.Show());
        };

        if (SelfCheck.RequestedPath() is string path)
        {
            ui.TryEnqueue(DispatcherQueuePriority.Low, async () =>
            {
                var pages = await flyout.LoadEveryPageAsync();
                File.WriteAllText(path, $"{{\"trayAdded\":{(tray.Added ? "true" : "false")},\"elevated\":{(Environment.IsPrivilegedProcess ? "true" : "false")},"
                    + $"\"pages\":[{string.Join(',', pages.Select(p => $"\"{p}\""))}]}}");
                Quit();
            });
            return;
        }

        if (LaunchPolicy.OpenFlyoutOnLaunch(commandLine))
            ui.TryEnqueue(DispatcherQueuePriority.Low, () => flyout.Show(services.FirstRun ? typeof(ChooseAppsPage) : null));
    }

    public void Quit()
    {
        if (_quitting) return;
        _quitting = true;
        if (_services is { } services) services.Quit(Finish);
        else Finish();
    }

    private void Finish()
    {
        _hotKey?.Dispose();
        _tray?.Dispose();
        _services?.Toasts.ClearHistory();
        _services?.Toasts.RemoveDemo();
        _flyout?.Close();
        Exit();
    }

    private void OnWindowMessage(WindowMessage message)
    {
        if (_quitting || _services is not { } services) return;
        switch (message.Id)
        {
            case NativeMethods.WM_HOTKEY:
                _flyout?.Toggle();
                break;
            case NativeMethods.WM_POWERBROADCAST when message.WParam == NativeMethods.PBT_APMRESUMEAUTOMATIC:
                services.Resumed();
                break;
            // Restart Manager closing it for an installer (spec §6.7).
            case NativeMethods.WM_ENDSESSION when message.WParam != 0 && (message.LParam & NativeMethods.ENDSESSION_CLOSEAPP) != 0:
                Quit();
                break;
            case NativeMethods.WM_ENDSESSION when message.WParam != 0:
                services.EndSession();
                break;
            case NativeMethods.WM_TIMECHANGE:
                TimeZoneInfo.ClearCachedData();
                services.Resumed();
                services.RegionChanged();
                break;
            case NativeMethods.WM_SETTINGCHANGE when message.LParam != 0 && Marshal.PtrToStringUni(message.LParam) == "intl":
                TimeZoneInfo.ClearCachedData();
                CultureInfo.CurrentCulture.ClearCachedData();
                services.RegionChanged();
                break;
        }
    }

    // Update all, Install and Tiny Tracker's own Update act without opening the flyout. Close & update opens it, so a Force close
    // can be answered.
    private void OnToast(ToastAction action)
    {
        if (_quitting || _services is not { } services || _flyout is not { } flyout) return;
        switch (action)
        {
            case ToastAction.UpdateAll:
                if (services.Updates.CanUpdateAll) services.Updates.UpdateAllCommand.Execute(null);
                break;
            case ToastAction.Install:
                services.Updates.InstallNeedingPermission();
                break;
            case ToastAction.CloseAndUpdate:
                flyout.Show(typeof(UpdatesPage));
                services.Updates.CloseAndUpdateInUse();
                break;
            case ToastAction.SelfUpdate:
                services.Updates.SelfRow.PrimaryCommand.Execute(null);
                break;
            case ToastAction.WhatsNew:
                if (services.SelfUpdate is { } self) OpenLink(self.Running.ReleasePage);
                break;
            default:
                flyout.Show();
                break;
        }
    }

    private static string Asset(string name) => Path.Combine(AppContext.BaseDirectory, "Assets", name);

    private void OpenLink(string url)
    {
        if (Links.Openable(url) is not { } link)
        {
            _services?.Log.Warn($"Link not opened: {url}");
            return;
        }
        _ = Windows.System.Launcher.LaunchUriAsync(new Uri(link));
    }

    private (int, string, bool)[] Menu() =>
    [
        (MenuOpen, Strings.MenuOpen, true),
        (MenuCheckNow, Strings.MenuCheckNow, true),
        (MenuUpdateAll, Strings.MenuUpdateAll, _services?.Updates.CanUpdateAll == true),
        (MenuSettings, Strings.MenuSettings, true),
        (0, "-", true),
        (MenuQuit, Strings.MenuQuit, true),
    ];

    private void OnMenu(int id)
    {
        if (_services is not { } services || _flyout is not { } flyout) return;
        switch (id)
        {
            case MenuOpen:
                flyout.Show();
                break;
            case MenuCheckNow:
                services.Updates.CheckNowCommand.Execute(null);
                break;
            case MenuUpdateAll:
                services.Updates.UpdateAllCommand.Execute(null);
                break;
            case MenuSettings:
                flyout.Show(typeof(SettingsPage));
                break;
            case MenuQuit:
                Quit();
                break;
        }
    }

    private void OnUpdatesChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_services is not { } services) return;
        if (e.PropertyName == nameof(UpdatesViewModel.Tray)) ShowTray(services.Updates.Tray);
        else if (e.PropertyName == nameof(UpdatesViewModel.IsWorking)) UpdateIdle();
    }

    private void ShowTray(TrayState state)
    {
        string[] frames = state.Icon switch
        {
            TrayIconKind.Working => [.. Enumerable.Range(0, 8).Select(frame => Asset($"tray-work-{frame}.ico"))],
            TrayIconKind.Badge => [Asset("tray-badge.ico")],
            _ => [Asset("tray.ico")],
        };
        _tray?.Update(frames, state.Tooltip);
    }

    // Efficiency mode while nothing is open or running (spec §9).
    private void UpdateIdle()
    {
        var busy = _flyout?.IsOpen == true || _services?.Updates.IsWorking == true;
        if (busy == !_idle) return;
        _idle = !busy;
        if (_idle) Efficiency.EnterIdle();
        else Efficiency.ExitIdle();
    }
}
