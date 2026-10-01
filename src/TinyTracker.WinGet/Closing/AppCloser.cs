using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using TinyTracker.Core.Installing;
using TinyTracker.Core.Logging;
using TinyTracker.Core.Tracking;
using Windows.Management.Deployment;
using static TinyTracker.WinGet.Interop.NativeMethods;

namespace TinyTracker.WinGet.Closing;

// Closes an app for Close & update (spec §6.3): this user's processes in its folder, not other apps' inside it, asked through
// Restart Manager as installers ask and ended only if the user says so; what the user or Windows started reopens, unelevated.
public sealed class AppCloser(Func<string, string?> folderOf, Func<string, IReadOnlyList<string>> othersInside, FileLog log) : IAppCloser
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    public bool CanClose(string localId) => folderOf(localId) is not null;

    public IClosingApp Close(string localId)
    {
        var folder = folderOf(localId) ?? throw new InvalidOperationException("The app's install folder can't be found.");
        var others = othersInside(folder);
        return new ClosingApp(folder, others, RunningApps.Open(folder, others), FamilyOf(localId), log);
    }

    // Where the app lives: its uninstall entry's location or icon, or its MSIX package's folder. ownFolder is Tiny Tracker's.
    public static Func<string, string?> FolderLookup(string ownFolder)
    {
        var folders = FoldersOfThisPc(ownFolder);
        return localId =>
        {
            try
            {
                return LocalApp.Parse(localId) switch
                {
                    UninstallEntry entry => EntryFolder(entry, folders),
                    MsixPackage package => folders.Choose(PackageFolder(package), null),
                    _ => null,
                };
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or COMException)
            {
                return null;
            }
        };
    }

    // The install locations of other apps inside the folder, such as a launcher's games: Close & update leaves those alone.
    public static IReadOnlyList<string> OthersInside(string folder)
    {
        var found = new List<string>();
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var root = RegistryKey.OpenBaseKey(hive, view);
                    using var entries = root.OpenSubKey(UninstallKey);
                    if (entries is null) continue;
                    foreach (var name in entries.GetSubKeyNames())
                    {
                        using var entry = entries.OpenSubKey(name);
                        if (entry?.GetValue("InstallLocation") is string location && AppFolders.Holds(folder, location) && !AppFolders.Holds(location, folder))
                            found.Add(location);
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                }
            }
        }
        return found;
    }

    // The folders of this PC that are never an app's own (spec §6.3): Windows', temporary and installer caches, and every folder
    // Windows names, as they are, the Store's apps folder too.
    public static AppFolders FoldersOfThisPc(string ownFolder)
    {
        static string Of(Environment.SpecialFolder folder) => Environment.GetFolderPath(folder);
        var profile = Of(Environment.SpecialFolder.UserProfile);
        var local = Of(Environment.SpecialFolder.LocalApplicationData);
        var programData = Of(Environment.SpecialFolder.CommonApplicationData);
        List<string> shared =
        [
            .. Enum.GetValues<Environment.SpecialFolder>().Select(Of),
            Path.Combine(Of(Environment.SpecialFolder.ProgramFiles), "WindowsApps"),
            // The Store's folder on another drive it installs to.
            .. DriveInfo.GetDrives().Where(d => d.DriveType is DriveType.Fixed or DriveType.Removable).Select(d => Path.Combine(d.Name, "WindowsApps")),
            Path.GetDirectoryName(profile) ?? profile,
            Path.Combine(local, "Programs"),
            Path.Combine(local, "Programs", "Common"),
            .. KnownFolders(),
            .. new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" }.Select(Environment.GetEnvironmentVariable).OfType<string>(),
        ];
        return new AppFolders(
            [Of(Environment.SpecialFolder.Windows), Path.GetTempPath(), Path.Combine(programData, "Package Cache"), Path.Combine(programData, "Microsoft")],
            shared, programData, ownFolder);
    }

    // The ones .NET has no name for: Downloads, LocalLow, Saved Games, Public, Public Downloads, Links, Contacts and Searches.
    private static IEnumerable<string> KnownFolders()
    {
        string[] ids =
        [
            "374DE290-123F-4565-9164-39C4925E467B", "A520A1A4-1780-4FF6-BD18-167343C5AF16", "4C5C32FF-BB9D-43B0-B5B4-2D72E54EAAA4",
            "DFDF76A2-C82A-4D63-906A-5644AC457385", "3D644C9B-1FB8-4F30-9B45-F670235F79C0", "BFB9D5E0-C6A9-404C-B2B2-AE6DB6AF4968",
            "56784854-C6CB-462B-8169-88E350ACB882", "7D1D3A04-DEBB-4115-95CF-2F29DA2920DA",
        ];
        foreach (var id in ids)
        {
            string? path = null;
            try
            {
                path = SHGetKnownFolderPath(new Guid(id), 0, 0);
            }
            catch (COMException)
            {
            }
            if (path is not null) yield return path;
        }
    }

    private static string? EntryFolder(UninstallEntry entry, AppFolders folders)
    {
        using var root = RegistryKey.OpenBaseKey(entry.PerUser ? RegistryHive.CurrentUser : RegistryHive.LocalMachine, entry.Wow64 ? RegistryView.Registry32 : RegistryView.Registry64);
        using var key = root.OpenSubKey(entry.KeyPath);
        return key is null ? null : folders.Choose(key.GetValue("InstallLocation") as string, key.GetValue("DisplayIcon") as string);
    }

    private static string? PackageFolder(MsixPackage package) => new PackageManager().FindPackageForUser(string.Empty, package.FullName)?.InstalledLocation.Path;

    // An MSIX app is known by its family, which stays the same when an update changes its full name.
    private static string? FamilyOf(string localId)
    {
        if (LocalApp.Parse(localId) is not MsixPackage package) return null;
        try
        {
            return new PackageManager().FindPackageForUser(string.Empty, package.FullName)?.Id.FamilyName;
        }
        catch (Exception e) when (e is COMException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private sealed class ClosingApp : IClosingApp
    {
        private static readonly TimeSpan WatchEvery = TimeSpan.FromMilliseconds(200);
        private static readonly TimeSpan StopWait = TimeSpan.FromSeconds(5);
        private readonly string _folder;
        private readonly IReadOnlyList<string> _others;
        private readonly List<OpenApp> _apps;
        private readonly string? _family;
        private readonly FileLog _log;
        private readonly CancellationTokenSource _stop = new();
        private readonly RestartManager? _manager;
        private readonly Task _asking;
        private int _ended;

        public ClosingApp(string folder, IReadOnlyList<string> others, List<OpenApp> apps, string? family, FileLog log)
        {
            _folder = folder;
            _others = others;
            _apps = apps;
            _family = family;
            _log = log;
            Closed = WatchAsync();
            if (apps.Count > 0)
            {
                _manager = RestartManager.For([.. apps.Select(a => a.App)], out var error);
                // Nothing is asked to close then, and the row asks about Force close 10 s later.
                if (_manager is null) log.Warn($"Restart Manager didn't start: {error}");
            }
            // Restart Manager waits for the apps it asks, so it runs on a worker.
            _asking = _manager is { } manager ? Task.Run(manager.AskToClose) : Task.CompletedTask;
        }

        public Task Closed { get; }

        // Ends what still runs, through the handles held since it was found, so never another process that got its id.
        public bool ForceClose()
        {
            var ended = true;
            foreach (var app in _apps)
            {
                if (!app.Exited && !app.End()) ended &= app.Exited;
            }
            return ended;
        }

        // Stops the asking first, so nothing closes after this. Then what the user or Windows started opens again: a program
        // another app started is that app's to start, and a console program would run its command again.
        public void Reopen()
        {
            if (Interlocked.Exchange(ref _ended, 1) == 1) return;
            _manager?.Cancel();
            _stop.Cancel();
            try
            {
                if (Task.WhenAll(_asking, Closed).Wait(StopWait)) _manager?.Dispose();
            }
            catch (AggregateException e)
            {
                _log.Warn($"Restart Manager didn't stop: {e.InnerException?.Message}");
            }
            try
            {
                var closed = RunningApps.Roots([.. _apps.Select(a => a.App)]).Where(root => _apps.First(a => a.App == root).Exited).ToList();
                if (closed.Count == 0) return;
                if (_family is not null)
                {
                    _ = ReopenPackageAsync(_family);
                    return;
                }
                // What the installer started again isn't started twice.
                var running = RunningApps.In(_folder, _others).Select(a => a.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var app in closed)
                {
                    var name = Path.GetFileName(app.Path);
                    if (running.Contains(app.Path)) continue;
                    if (app.Console) _log.Info($"{name} wasn't reopened: it's a console program");
                    else if (!StartedByTheUser(app)) _log.Info($"{name} wasn't reopened: another app started it");
                    else if (!File.Exists(app.Path)) _log.Info($"{name} wasn't reopened: it's gone after the update");
                    else if (Start(app) is var error and not 0) _log.Warn($"{name} wasn't reopened: 0x{error:X8}");
                }
            }
            finally
            {
                foreach (var app in _apps) app.Dispose();
            }
        }

        private async Task WatchAsync()
        {
            try
            {
                while (!_apps.All(a => a.Exited)) await Task.Delay(WatchEvery, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        // The shell or Windows, this app's own reopen, or a launcher that has gone since.
        private static bool StartedByTheUser(RunningApp app) =>
            app.ParentPath is null || app.ParentId == Environment.ProcessId || AppFolders.Holds(Environment.GetFolderPath(Environment.SpecialFolder.Windows), app.ParentPath);

        // As the user, unelevated like this app, with the process's own command line and working folder, and with the user's own
        // environment rather than this app's. 0 once started, else the Win32 error.
        private static int Start(RunningApp app)
        {
            var environment = UserEnvironment();
            try
            {
                var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
                var folder = app.Directory is { } directory && Directory.Exists(directory) ? directory : Path.GetDirectoryName(app.Path);
                var flags = environment == 0 ? 0 : CreateUnicodeEnvironment;
                if (!CreateProcess(app.Path, new StringBuilder(app.CommandLine), 0, 0, false, flags, environment, folder, ref startup, out var started))
                    return Marshal.GetLastPInvokeError();
                CloseHandle(started.Process);
                CloseHandle(started.Thread);
                return 0;
            }
            finally
            {
                if (environment != 0) DestroyEnvironmentBlock(environment);
            }
        }

        // The variables Windows gives this user's new programs, as the shell would, without the ones only this app has.
        private static nint UserEnvironment()
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenQuery | TokenDuplicate, out var token)) return 0;
            using (token) return CreateEnvironmentBlock(out var block, token, false) ? block : 0;
        }

        // An MSIX app starts through its Start menu entry.
        private async Task ReopenPackageAsync(string family)
        {
            try
            {
                var package = new PackageManager().FindPackagesForUser(string.Empty, family).FirstOrDefault();
                var entries = package is null ? null : await package.GetAppListEntriesAsync();
                if (entries is { Count: > 0 }) await entries[0].LaunchAsync();
                else _log.Info("An MSIX app wasn't reopened: its package is gone");
            }
            catch (Exception e)
            {
                _log.Warn($"An MSIX app wasn't reopened: 0x{e.HResult:X8}");
            }
        }
    }
}
