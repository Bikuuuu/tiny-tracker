namespace TinyTracker.Core.Launch;

// Mirrors TOKEN_ELEVATION_TYPE.
public enum ElevationType { Default = 1, Full = 2, Limited = 3 }

// Switches that do their work with no window and exit, elevated or not (spec §6.7).
public enum MaintenanceVerb { None, Cleanup, StartWithWindows, Uninstall }

public static class LaunchPolicy
{
    public const string StartupFlag = "--startup";
    // Debug builds only: made-up apps instead of winget.
    public const string DemoFlag = "--demo";
    // The installer's "Start Tiny Tracker with Windows".
    public const string StartWithWindowsFlag = "--start-with-windows";
    public const string CleanupFlag = "--cleanup";
    // The uninstaller, as administrator (spec §10).
    public const string UninstallFlag = "--uninstall";
    // With --uninstall or --cleanup: the settings, history and logs go too.
    public const string RemoveDataFlag = "--remove-data";
    // Marks the copy the shell starts unelevated.
    public const string RelaunchedFlag = "--relaunched";

    // Only a split-token (UAC) elevation can be undone through the shell; with UAC off it would loop. So would an elevated shell,
    // so the copy it starts runs as it is.
    public static bool ShouldRelaunchUnelevated(ElevationType type, IReadOnlyList<string> args) =>
        type == ElevationType.Full && !args.Contains(RelaunchedFlag);

    public static IReadOnlyList<string> RelaunchArguments(IReadOnlyList<string> args) => [.. args, RelaunchedFlag];

    // Where the shell can't start it again, the Explorer that owns the taskbar does, without arguments: by its full path, never a
    // new one, which would be an elevated shell.
    public static string? ExplorerFallback(string windowsFolder, Func<string, bool> ownsTheTaskbar) =>
        Path.Combine(windowsFolder, "explorer.exe") is var explorer && ownsTheTaskbar(explorer) ? explorer : null;

    // --cleanup removes what the app registered for this user; the older name still works.
    public static MaintenanceVerb MaintenanceVerbOf(IReadOnlyList<string> args) =>
        args.Any(a => a is CleanupFlag or "--cleanup-notifications") ? MaintenanceVerb.Cleanup
        : args.Contains(StartWithWindowsFlag) ? MaintenanceVerb.StartWithWindows
        : args.Contains(UninstallFlag) ? MaintenanceVerb.Uninstall
        : MaintenanceVerb.None;

    public static bool RemovesData(IReadOnlyList<string> args) => args.Contains(RemoveDataFlag);

    public static bool OpenFlyoutOnLaunch(IReadOnlyList<string> args) => !args.Contains(StartupFlag);

    // A second start hands over its whole command line, program first.
    public static bool OpenFlyoutOnSecondStart(string commandLine) => OpenFlyoutOnLaunch([.. CommandLine.Split(commandLine).Skip(1)]);

    public static bool IsDemo(IReadOnlyList<string> args) => args.Contains(DemoFlag);
}
