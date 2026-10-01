using System.Diagnostics;
using System.Security.Principal;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using TinyTracker.App.Interop;
using TinyTracker.Core;
using TinyTracker.Core.Launch;
using TinyTracker.Core.Storage;
using TinyTracker.WinGet.Storage;

namespace TinyTracker.App;

public static class Program
{
    // How long a second start waits for the running copy to take its command line.
    private static readonly TimeSpan RedirectWait = TimeSpan.FromSeconds(10);

    [STAThread]
    private static int Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();

        // Maintenance verbs run as-is, even elevated: the installer and the uninstaller run them.
        switch (LaunchPolicy.MaintenanceVerbOf(args))
        {
            case MaintenanceVerb.Cleanup:
                return Cleanup(LaunchPolicy.RemovesData(args));
            case MaintenanceVerb.StartWithWindows:
                return StartWithWindows();
            case MaintenanceVerb.Uninstall:
                return Uninstall.Run(new WindowsUninstall(Environment.ProcessPath!), DataPaths.ForCurrentUser().Settings, LaunchPolicy.RemovesData(args)).ExitCode;
        }

        // The shell starts it again as the signed-in user, with its arguments; the running Explorer, the fallback, drops them.
        if (LaunchPolicy.ShouldRelaunchUnelevated(ProcessInfo.GetElevationType(), args))
        {
            if (!ShellLaunch.Start(Environment.ProcessPath!, CommandLine.Arguments(LaunchPolicy.RelaunchArguments(args)), Environment.CurrentDirectory)
                && LaunchPolicy.ExplorerFallback(Environment.GetFolderPath(Environment.SpecialFolder.Windows), OwnsTheTaskbar) is { } explorer)
                Process.Start(new ProcessStartInfo(explorer, $"\"{Environment.ProcessPath}\"") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(explorer)! });
            return 0;
        }

        // A demo runs next to the real app, not instead of it.
        var instance = AppInstance.FindOrRegisterForKey(App.IsDemo(args) ? AppInfo.InstanceKey + ".Demo" : AppInfo.InstanceKey);
        if (!instance.IsCurrent)
        {
            NativeMethods.AllowSetForegroundWindow(instance.ProcessId);
            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            // Never left waiting on a copy that doesn't take it.
            Task.Run(() => instance.RedirectActivationToAsync(activation).AsTask()).Wait(RedirectWait);
            return 0;
        }

        Application.Start(p =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            new App();
        });
        return 0;
    }

    // Another shell can host the tray too, so the taskbar's process must be that program.
    private static bool OwnsTheTaskbar(string program)
    {
        var taskbar = NativeMethods.FindWindowW("Shell_TrayWnd", null);
        if (taskbar == 0 || NativeMethods.GetWindowThreadProcessId(taskbar, out var owner) == 0) return false;
        try
        {
            using var process = Process.GetProcessById((int)owner);
            return string.Equals(process.MainModule?.FileName, program, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    // The installer's box, for the user it runs the app as. A failure exits with 1.
    // As SYSTEM, as a managed install's Setup runs it, nothing: SYSTEM's entry would start the app for no one (spec §6.7).
    private static int StartWithWindows()
    {
        try
        {
            using var me = WindowsIdentity.GetCurrent();
            if (me.IsSystem) return 0;
            new StartupEntry(new RegistryStartupValues(), Environment.ProcessPath!).Set(true);
            return 0;
        }
        catch (Exception)
        {
            return 1;
        }
    }

    // Removes what the app registered for this user, and with removeData its settings, history and logs. Every step runs even
    // when another fails; a failure exits with 1.
    internal static int Cleanup(bool removeData)
    {
        var failed = false;
        void Step(Action step)
        {
            try
            {
                step();
            }
            catch (Exception)
            {
                failed = true;
            }
        }
        Step(() => new StartupEntry(new RegistryStartupValues(), Environment.ProcessPath!).Remove());
        Step(Notifications.ToastService.RemoveRegistration);
        Step(() => TrayEntries.Remove(Environment.ProcessPath!));
        if (removeData) Step(() => FolderTree.Delete(DataPaths.ForCurrentUser().Root));
        return failed ? 1 : 0;
    }
}
