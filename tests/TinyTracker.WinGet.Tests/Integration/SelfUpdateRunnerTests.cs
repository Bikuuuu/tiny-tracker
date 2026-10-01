using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using TinyTracker.Core.History;
using TinyTracker.Core.SelfUpdate;
using TinyTracker.Core.Storage;
using TinyTracker.WinGet.Closing;
using TinyTracker.WinGet.Elevation;
using TinyTracker.WinGet.SelfUpdate;
using TinyTracker.WinGet.Tests.SelfUpdate;
using Xunit;

namespace TinyTracker.WinGet.Tests.Integration;

// The installed, unchanged app updating itself from the stand-in GitHub with silent mode and Auto on, on GitHub runners only
// (spec §12). test-install.ps1 installs the version it starts from, fresh, and names this build's Setup and version.
public sealed class SelfUpdateRunnerTests
{
    private static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tiny Tracker");
    private static readonly string App = Path.Combine(Folder, "TinyTracker.exe");
    private static readonly string UpdateFolder = Path.Combine(Folder, "update");
    private static readonly SecurityIdentifier Me = WindowsIdentity.GetCurrent().User!;
    private static readonly DataPaths Data = DataPaths.ForCurrentUser();
    // The check a minute after the start, the download, Setup and the restart.
    private static readonly TimeSpan UpdateWait = TimeSpan.FromMinutes(8);
    private static readonly TimeSpan AppWait = TimeSpan.FromMinutes(2);
    private const uint WmClose = 0x0010;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly Stopwatch _clock = Stopwatch.StartNew();

    // How long each part took, in the test's output.
    private void Note(string what) => TestContext.Current.TestOutputHelper?.WriteLine($"{_clock.Elapsed.TotalSeconds,6:F1} s: {what}");

    private static (string Setup, SelfVersion Version) RunnerOnly()
    {
        var setup = Environment.GetEnvironmentVariable("TINYTRACKER_SELF_UPDATE_SETUP");
        Assert.SkipUnless(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" && setup is { Length: > 0 },
            "The self-update test runs only on GitHub runners, from test-install.ps1.");
        var version = SelfVersion.Parse(Environment.GetEnvironmentVariable("TINYTRACKER_SELF_UPDATE_VERSION"));
        Assert.NotNull(version);
        return (setup!, version);
    }

    [Fact]
    public async Task InstalledApp_UpdatesItselfToThisBuild()
    {
        var (setup, version) = RunnerOnly();
        var before = Installed();
        Assert.True(version.IsNewerThan(before), $"{version} isn't newer than the installed {before}.");
        await using var runner = await RunnerGitHub.StartAsync();
        runner.GitHub.Serve(version, setup);
        try
        {
            TurnOnSilentModeAndAuto();
            StartApp();
            Note("started the app");
            var entry = await WaitForAsync(runner, () => History().FirstOrDefault(e => e.ToVersion == version.ToString()));
            Note("History has it");
            // The restarted app writes History while Setup may still be finishing.
            await WaitForAsync(runner, () => SetupMutex.Held() ? null : "ended");
            Note("Setup ended");

            Assert.True(entry is { Result: HistoryResult.Updated, Id: "Bikuuuu.TinyTracker", Source: "github", Name: "Tiny Tracker" } && entry.FromVersion == before.ToString(), Told(runner, entry));
            Assert.Equal((version, version), (Installed(), FileVersion()));
            Assert.Contains("--startup", (await OneCopyAsync()).CommandLine);
            Note("one copy runs");
            // Setup's own copy goes at the next restart: its entry, then an empty one for no new name.
            var own = Path.Combine(UpdateFolder, $"TinyTracker-Setup-{version}-x64.exe");
            Assert.Equal([own], Directory.GetFileSystemEntries(UpdateFolder));
            var renames = PendingRenames();
            var at = Array.FindIndex(renames, r => r.EndsWith(@"\??\" + own, StringComparison.OrdinalIgnoreCase));
            Assert.True(at >= 0 && at + 1 < renames.Length && renames[at + 1] == "", $"Windows' pending renames: {string.Join(" | ", renames)}");
            // The app's look, then the helper's, its download and the redirect's.
            Assert.Equal(StandInGitHub.Hosts, runner.GitHub.Requests.Select(r => r.Host).Distinct());
            Assert.True(await StopAppAsync(), "The app quits with 0.");
        }
        finally
        {
            await StopAppAsync();
            SilentTask.Remove(Me);
        }
    }

    [Fact]
    public async Task WrongDigest_InstallsNothing()
    {
        var (setup, _) = RunnerOnly();
        var before = Installed();
        var next = before with { Patch = before.Patch + 1 };
        await using var runner = await RunnerGitHub.StartAsync();
        runner.GitHub.Serve(next, setup, digestMatches: false);
        try
        {
            TurnOnSilentModeAndAuto();
            StartApp();
            Note("started the app");
            var running = await OneCopyAsync();
            Note("it runs");
            var entry = await WaitForAsync(runner, () => History().FirstOrDefault(e => e.ToVersion == next.ToString()));
            Note("History has it");

            Assert.True(entry is { Result: HistoryResult.Failed, Reason: "DigestMismatch" }, Told(runner, entry));
            Assert.Contains(runner.GitHub.Requests, r => r.Host == "release-assets.githubusercontent.com");
            Assert.Equal(before, Installed());
            Assert.Empty(Directory.GetFileSystemEntries(UpdateFolder));
            Assert.Equal(running.Id, (await OneCopyAsync()).Id);
            Assert.True(await StopAppAsync(), "The app quits with 0.");
        }
        finally
        {
            await StopAppAsync();
            SilentTask.Remove(Me);
        }
    }

    private static SelfVersion Installed() =>
        SelfVersion.Parse(Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\TinyTracker_is1", "DisplayVersion", null) as string)
        ?? throw new InvalidOperationException("Tiny Tracker isn't installed.");

    private static SelfVersion FileVersion()
    {
        var info = FileVersionInfo.GetVersionInfo(App);
        return new SelfVersion(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart);
    }

    // Silent mode's task as the helper registers it, and the settings a user would pick. Whatever the runner's desktop says about
    // games, it isn't what this is about.
    private static void TurnOnSilentModeAndAuto()
    {
        Assert.Null(SilentTask.Register(Path.Combine(Folder, "TinyTracker.Helper.exe"), Me));
        var settings = new SettingsStore(Data.Settings);
        settings.Load();
        settings.Update(f => f with { Settings = f.Settings with { SilentMode = true, AutoSelfUpdate = true, AutoInstallWaitDays = 0, PauseDuringGames = false } });
    }

    private static void StartApp() => Process.Start(new ProcessStartInfo(App, "--startup") { UseShellExecute = true })?.Dispose();

    private static IReadOnlyList<HistoryEntry> History()
    {
        var history = new HistoryStore(Data.History, TimeProvider.System);
        history.Load();
        return history.Entries;
    }

    // What Windows moves or deletes at the next restart, each followed by where it goes.
    private static string[] PendingRenames() =>
        Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager", "PendingFileRenameOperations", null) as string[] ?? [];

    private static string Told(RunnerGitHub runner, HistoryEntry entry) =>
        $"History says {entry.Result} {entry.FromVersion} -> {entry.ToVersion}, {entry.Reason} {entry.Code}. {State(runner)}";

    // What the stand-in was asked, what its hosts resolve to, what settings.json keeps of the self-update, and how the app's log ends.
    private static string State(RunnerGitHub runner)
    {
        var settings = new SettingsStore(Data.Settings);
        settings.Load();
        return $"The stand-in was asked for: {string.Join(", ", runner.GitHub.Requests.Select(r => r.Host + r.Path))}. Its hosts: {RunnerGitHub.Resolution()}. "
            + $"The settings keep: {settings.Current.SelfUpdate}. The app's log ends:\n{LogEnd()}";
    }

    // The probe's find within the update's time, or a failure that says why.
    private static async Task<T> WaitForAsync<T>(RunnerGitHub runner, Func<T?> probe) where T : class
    {
        var until = DateTime.UtcNow + UpdateWait;
        while (true)
        {
            if (probe() is { } found) return found;
            if (DateTime.UtcNow > until)
                Assert.Fail($"Nothing came within {UpdateWait}. {State(runner)}");
            await Task.Delay(TimeSpan.FromSeconds(2), Ct);
        }
    }

    private static string LogEnd()
    {
        try
        {
            using var file = new FileStream(Data.Log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return string.Join('\n', new StreamReader(file).ReadToEnd().Split('\n').TakeLast(40));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return e.Message;
        }
    }

    // The one copy, once it has its tray window.
    private static async Task<RunningApp> OneCopyAsync()
    {
        var until = DateTime.UtcNow + AppWait;
        while (true)
        {
            var copies = RunningApps.Of(App);
            if (copies.Count == 1 && FindWindowW("TinyTracker.Tray", null) != 0) return copies[0];
            if (DateTime.UtcNow > until) Assert.Fail($"{copies.Count} copies run, not one with its tray window: {string.Join(" | ", copies.Select(Described))}. The app's log ends:\n{LogEnd()}");
            await Task.Delay(500, Ct);
        }
    }

    // Asked again until it's gone, since a copy that just started may have no tray window yet. True when every copy quit with 0.
    private static async Task<bool> StopAppAsync()
    {
        var copies = RunningApps.Of(App).Select(c => Opened(c.Id)).OfType<Process>().ToList();
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        try
        {
            while (copies.Any(c => !c.HasExited))
            {
                if (DateTime.UtcNow > until)
                {
                    foreach (var copy in copies) End(copy);
                    return false;
                }
                if (FindWindowW("TinyTracker.Tray", null) is var window and not 0) PostMessageW(window, WmClose, 0, 0);
                await Task.Delay(500, CancellationToken.None);
            }
            return copies.All(c => c.ExitCode == 0) && RunningApps.Of(App).Count == 0;
        }
        finally
        {
            copies.ForEach(c => c.Dispose());
        }
    }

    private static string Described(RunningApp copy)
    {
        using var process = Opened(copy.Id);
        return $"{copy.Id} {copy.CommandLine}, responding: {(process is null ? "gone" : process.Responding)}";
    }

    // Null once it's gone.
    private static Process? Opened(int id)
    {
        try
        {
            var process = Process.GetProcessById(id);
            _ = process.Handle;
            return process;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }

    private static void End(Process copy)
    {
        try
        {
            copy.Kill();
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception)
        {
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowW(string className, string? title);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(nint window, uint message, nint w, nint l);
}
