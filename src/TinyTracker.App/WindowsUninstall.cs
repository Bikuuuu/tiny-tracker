using System.Diagnostics;
using TinyTracker.App.Interop;
using TinyTracker.App.Tray;
using TinyTracker.Core.Launch;
using TinyTracker.WinGet.Cli;
using TinyTracker.WinGet.Closing;
using TinyTracker.WinGet.Elevation;

namespace TinyTracker.App;

// --uninstall's Windows side (spec §10), run elevated by the uninstaller from Program Files.
internal sealed class WindowsUninstall(string exe) : IUninstallSystem
{
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(10);
    // A helper ends once its app hangs up, though an install it runs may take longer.
    private static readonly TimeSpan HelperGrace = TimeSpan.FromSeconds(30);

    private string Folder => Path.GetDirectoryName(exe)!;

    public void CloseRunningCopies() => RunningCopies.Close(exe, Path.Combine(Folder, "TinyTracker.Helper.exe"), TrayIcon.ClassName, Grace, HelperGrace);

    public void RemoveEveryTask()
    {
        if (SilentTask.RemoveAll() is { } error) throw new IOException(error);
    }

    public bool TurnProxyOptionOff() => WinGetSettings.DisableProxyOptionAsync(WinGetCli.Real, CancellationToken.None).GetAwaiter().GetResult() is null;

    // Once the shell has it, none of it runs elevated too (spec §8), whether or not the cleanup is seen.
    public bool CleanUpAsSignedInUser(bool removeData)
    {
        string[] arguments = removeData ? [LaunchPolicy.CleanupFlag, LaunchPolicy.RemoveDataFlag] : [LaunchPolicy.CleanupFlag];
        if (!ShellLaunch.Start(exe, CommandLine.Arguments(arguments), Folder)) return false;
        var watch = Stopwatch.StartNew();
        CleanupWait.For(CleanupRunning, () => watch.Elapsed, () => Thread.Sleep(CleanupWait.Every));
        return true;
    }

    public bool CleanUpHere(bool removeData) => Program.Cleanup(removeData) == 0;

    // Found by its program and command line; a look by name comes first, since it's quicker.
    private bool CleanupRunning()
    {
        var own = Environment.ProcessId;
        var named = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe));
        var others = named.Any(p => p.Id != own);
        foreach (var process in named) process.Dispose();
        return others && RunningApps.Of(exe).Any(a => CommandLine.Split(a.CommandLine).Skip(1).Contains(LaunchPolicy.CleanupFlag));
    }
}
