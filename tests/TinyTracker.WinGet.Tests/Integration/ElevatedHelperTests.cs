using System.Diagnostics;
using System.Security.Principal;
using TinyTracker.Core.Elevation;
using TinyTracker.Core.Installing;
using TinyTracker.Core.Tracking;
using TinyTracker.Core.Versions;
using TinyTracker.WinGet.Elevation;
using Xunit;

namespace TinyTracker.WinGet.Tests.Integration;

// The real helper, elevated, on GitHub runners only (spec §12): real upgrades through it, and silent mode's task
// registered from a Program Files copy, started with no prompt, and removed.
[Collection<UpgradeCollection>]
public class ElevatedHelperTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);
    private static readonly string HelperPath = HelperExe.Location;
    private static readonly SecurityIdentifier Me = WindowsIdentity.GetCurrent().User!;
    private const string NotepadPlusPlus = "Notepad++.Notepad++";
    private const string Vlc = "VideoLAN.VLC";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void RunnerOnly() =>
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" && Environment.GetEnvironmentVariable("TINYTRACKER_WINGET_UPGRADE_TESTS") == "1",
            "Elevated helper tests run only on GitHub runners.");

    // The helper runs with this test's own rights, elevated on runners.
    private static async Task<HelperClient> StartAsync(string helper)
    {
        var pipe = HelperRules.NewPipeName();
        using var process = Process.Start(helper, ["--pipe", pipe, "--user", Me.Value]);
        return await HelperClient.ConnectAsync(pipe, helper, Environment.IsPrivilegedProcess, Wait, Ct);
    }

    private static async Task<InstalledPackage> InstalledAsync(string id)
    {
        var session = await WinGetSession.OpenAsync(Ct);
        return Assert.Single(await session.FindInstalledByIdAsync([id], details: false, Ct), p => string.Equals(p.CatalogId, id, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task OldPackage_UpgradesThroughTheHelper()
    {
        RunnerOnly();
        var before = await InstalledAsync(NotepadPlusPlus);
        var stages = new List<UpgradeStage>();
        using var client = await StartAsync(HelperPath);
        var outcome = await client.UpgradeAsync(new PackageKey(NotepadPlusPlus, "winget"), before.LatestVersion!, new Reported<UpgradeProgress>(p =>
        {
            lock (stages) stages.Add(p.Stage);
        }), Ct);
        Assert.True(outcome.Result is UpgradeResult.Updated or UpgradeResult.RestartNeeded, outcome.ToString());
        lock (stages) Assert.Contains(UpgradeStage.Installing, stages);
        Assert.True(PackageVersion.Same(before.LatestVersion, (await InstalledAsync(NotepadPlusPlus)).Version));
    }

    [Fact]
    public async Task CancelThroughTheHelper_KeepsTheOldVersion()
    {
        RunnerOnly();
        var before = await InstalledAsync(Vlc);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        // A mirror that sends nothing is cancelled too.
        cancel.CancelAfter(TimeSpan.FromMinutes(5));
        var fired = 0;
        using var client = await StartAsync(HelperPath);
        var outcome = await client.UpgradeAsync(new PackageKey(Vlc, "winget"), before.LatestVersion!, new Reported<UpgradeProgress>(p =>
        {
            if (p.Stage == UpgradeStage.Downloading && p.BytesDownloaded > 0 && Interlocked.Exchange(ref fired, 1) == 0) _ = Task.Run(cancel.Cancel, Ct);
        }), cancel.Token);
        Assert.Equal(UpgradeResult.Cancelled, outcome.Result);
        if (fired == 0) TestContext.Current.TestOutputHelper?.WriteLine("The mirror sent nothing for 5 minutes.");
        Assert.True(PackageVersion.Same(before.Version, (await InstalledAsync(Vlc)).Version));
    }

    // Runners are elevated already, so the prompt starts the helper with nothing to click.
    [Fact]
    public async Task Launcher_StartsTheHelperThroughItsPrompt_AndSaysWhileItIsOpen()
    {
        RunnerOnly();
        var prompts = new List<bool>();
        var launcher = new HelperLauncher(HelperPath, () => false, () => 0, demo: false, prompting: open =>
        {
            lock (prompts) prompts.Add(open);
        });
        var start = await launcher.StartAsync(mayPrompt: true, Ct);
        using var session = start.Session;
        Assert.Equal((HelperStartResult.Started, (string?)null), (start.Result, start.Code));
        Assert.True(session!.WinGetAvailable);
        lock (prompts) Assert.Equal([true, false], prompts);
    }

    // An installed helper runs where it is; this build's gets a Program Files copy.
    [Fact]
    public async Task SilentTask_FromProgramFiles_StartsTheHelperWithNoPrompt_AndGoesAgain()
    {
        RunnerOnly();
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tiny Tracker Tests");
        var helper = HelperExe.Installed ? HelperPath : Path.Combine(folder, "TinyTracker.Helper.exe");
        if (!HelperExe.Installed) Copy(Path.GetDirectoryName(HelperPath)!, folder);
        try
        {
            using (var client = await StartAsync(helper))
            {
                Assert.Null(await client.RegisterTaskAsync(Ct));
            }
            Assert.True(SilentTask.Exists(helper, Me));
            var pipe = HelperRules.NewPipeName();
            Assert.Null(SilentTask.Run(Me, pipe));
            using (var client = await HelperClient.ConnectAsync(pipe, helper, Environment.IsPrivilegedProcess, Wait, Ct))
            {
                Assert.True(client.WinGetAvailable);
                Assert.Null(await client.RemoveTaskAsync(Ct));
            }
            Assert.False(SilentTask.Exists(helper, Me));
        }
        finally
        {
            SilentTask.Remove(Me);
            await DeleteAsync(folder);
        }
    }

    // Turning off goes through the task, or, when the task runs another copy's helper, through a prompt (none shows on runners).
    [Fact]
    public async Task SilentModeOff_RemovesTheTask_ThroughItself_OrThroughAPrompt()
    {
        RunnerOnly();
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tiny Tracker Tests");
        var helper = Path.Combine(folder, "TinyTracker.Helper.exe");
        Copy(Path.GetDirectoryName(HelperPath)!, folder);
        try
        {
            foreach (var off in new[] { helper, HelperPath })
            {
                using (var client = await StartAsync(helper))
                {
                    Assert.Null(await client.RegisterTaskAsync(Ct));
                }
                var viaTask = new HelperLauncher(off, () => true, () => 0, demo: false);
                var viaPrompt = new HelperLauncher(off, () => false, () => 0, demo: false);
                Assert.Equal((SwitchResult.Done, (string?)null), await new SilentModeSwitch(viaTask, viaPrompt, off).TurnOffAsync(Ct));
                Assert.False(SilentTask.Registered(Me));
            }
        }
        finally
        {
            SilentTask.Remove(Me);
            await DeleteAsync(folder);
        }
    }

    // The uninstaller's: every account's task goes, then the folder (spec §10).
    [Fact]
    public void RemovingEveryTask_LeavesNoTaskAndNoFolder()
    {
        RunnerOnly();
        const string other = @"\Tiny Tracker\Tiny Tracker Helper (S-1-5-21-0-0-0-1001)";
        try
        {
            Assert.Null(SilentTask.Register(HelperPath, Me));
            // Another account's task sits next to this one's.
            Assert.Equal(0, Schtasks("/create", "/tn", other, "/tr", "cmd.exe /c exit", "/sc", "once", "/st", "00:00", "/f"));
            Assert.True(SilentTask.Registered(Me));
            Assert.Null(SilentTask.RemoveAll());
            Assert.False(SilentTask.Registered(Me));
            Assert.False(SilentTask.FolderExists());
            Assert.Null(SilentTask.RemoveAll());
        }
        finally
        {
            Schtasks("/delete", "/tn", other, "/f");
            SilentTask.Remove(Me);
        }
    }

    private static int Schtasks(params string[] arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("schtasks.exe", arguments) { UseShellExecute = false, CreateNoWindow = true })!;
        process.WaitForExit();
        return process.ExitCode;
    }

    private static void Copy(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    // A helper that just hung up may still be exiting.
    private static async Task DeleteAsync(string folder)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < 20)
            {
                await Task.Delay(500, Ct);
            }
        }
    }
}
