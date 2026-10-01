using System.Diagnostics;
using System.Reflection;
using TinyTracker.Core.Logging;
using TinyTracker.WinGet.Closing;
using Xunit;

namespace TinyTracker.WinGet.Tests.Closing;

// Close & update on a dummy app started from a temp folder (spec §6.3). Nothing outside that folder is ever touched.
public sealed class AppCloserTests : IDisposable
{
    private const string LocalId = @"ARP\User\X64\Example App";
    private const string Mark = "TINYTRACKER_TEST_MARK";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);
    private static readonly string DummyFolder = typeof(AppCloserTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "DummyAppFolder").Value!;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinytracker-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _app;
    private readonly List<string> _others = [];
    private readonly FileLog _log;
    private readonly AppCloser _closer;

    public AppCloserTests()
    {
        _app = Copy(Path.Combine(_root, "Example App"));
        _log = new FileLog(Path.Combine(_root, "logs", "app.log"), TimeProvider.System);
        _closer = new AppCloser(id => id == LocalId ? _app : null, _ => _others, _log);
    }

    // Every dummy still running goes, so no hidden window outlives the test. A child may start meanwhile, so it looks again.
    public void Dispose()
    {
        for (var round = 0; round < 25 && RunningApps.In(_root) is { Count: > 0 } left; round++)
        {
            foreach (var app in left)
            {
                try
                {
                    using var process = Process.GetProcessById(app.Id);
                    process.Kill();
                    process.WaitForExit(5000);
                }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
            }
            Thread.Sleep(200);
        }
        _log.Close();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < 20)
            {
                Thread.Sleep(250);
            }
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Copy(string folder)
    {
        Directory.CreateDirectory(folder);
        foreach (var file in Directory.EnumerateFiles(DummyFolder, "TinyTracker.DummyApp.*")) File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
        return folder;
    }

    // Where it ran and what it inherited, once it has said so.
    private static string[]? Report(string file)
    {
        try
        {
            return File.ReadAllText(file).Split('\n') is { Length: 2 } lines ? lines : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    // Started, and idle in its message loop, so Windows can ask it to close.
    private static Process Start(string folder, params string[] args)
    {
        var process = Process.Start(Path.Combine(folder, "TinyTracker.DummyApp.exe"), args);
        if (!args.Contains("--no-window")) process.WaitForInputIdle(10000);
        return process;
    }

    private static async Task Until(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, what);
            await Task.Delay(100, Ct);
        }
    }

    private int Running(string tag) => RunningApps.In(_root).Count(a => a.CommandLine.Contains($"--tag {tag}", StringComparison.Ordinal));

    [Fact]
    public void CanClose_OnlyAnAppWhoseFolderIsKnown()
    {
        Assert.True(_closer.CanClose(LocalId));
        Assert.False(_closer.CanClose(@"ARP\User\X64\Other App"));
    }

    [Theory]
    [InlineData("--hidden")]
    [InlineData("--visible")]
    public async Task AppThatClosesWhenAsked_Closes_AndReopensWithItsCommandLine(string window)
    {
        using var dummy = Start(_app, window, "--tag", "first");
        var closing = _closer.Close(LocalId);
        await closing.Closed.WaitAsync(Wait, Ct);
        Assert.True(dummy.HasExited);
        closing.Reopen();
        await Until(() => Running("first") == 1, "The app didn't reopen.");
    }

    [Theory]
    [InlineData("--stubborn")]
    [InlineData("--no-window")]
    public async Task AppThatDoesntClose_StaysOpen_UntilItsForceClosed(string kind)
    {
        using var dummy = Start(_app, kind);
        var closing = _closer.Close(LocalId);
        await Task.Delay(2000, Ct);
        Assert.False(closing.Closed.IsCompleted);
        Assert.True(closing.ForceClose());
        await closing.Closed.WaitAsync(Wait, Ct);
        Assert.True(dummy.HasExited);
    }

    [Fact]
    public async Task OnlyTheProcessThatStartedTheRest_Reopens()
    {
        using var dummy = Start(_app, "--child", "--tag", "parent");
        await Until(() => Running("child") == 1, "The child didn't start.");
        // Windows asks an app to close through its window, so the child's must be up.
        using (var child = Process.GetProcessById(RunningApps.In(_root).Single(a => a.CommandLine.Contains("--tag child", StringComparison.Ordinal)).Id))
            child.WaitForInputIdle(10000);
        var closing = _closer.Close(LocalId);
        await closing.Closed.WaitAsync(Wait, Ct);
        closing.Reopen();
        await Until(() => Running("parent") == 1 && Running("child") == 1, "The app didn't reopen with its child.");
        await Task.Delay(1000, Ct);
        Assert.Equal((1, 1), (Running("parent"), Running("child")));
    }

    [Fact]
    public async Task AppsInOtherFolders_AreLeftAlone()
    {
        var other = Copy(Path.Combine(_root, "Other App"));
        using var elsewhere = Start(other, "--tag", "elsewhere");
        using var dummy = Start(_app);
        var closing = _closer.Close(LocalId);
        await closing.Closed.WaitAsync(Wait, Ct);
        Assert.False(elsewhere.HasExited);
        Assert.True(closing.ForceClose());
        Assert.False(elsewhere.HasExited);
    }

    [Fact]
    public async Task AppWhoseExeIsGoneAfterTheUpdate_IsNotReopened_AndTheLogSays()
    {
        using (Start(_app, "--tag", "gone"))
        {
            var closing = _closer.Close(LocalId);
            await closing.Closed.WaitAsync(Wait, Ct);
            File.Delete(Path.Combine(_app, "TinyTracker.DummyApp.exe"));
            closing.Reopen();
        }
        await Task.Delay(500, Ct);
        Assert.Equal(0, Running("gone"));
        Assert.Contains("wasn't reopened: it's gone after the update", File.ReadAllText(Path.Combine(_root, "logs", "app.log")));
    }

    [Fact]
    public async Task AppsInstalledInsideTheFolder_AreLeftAlone()
    {
        var game = Copy(Path.Combine(_app, "games", "Example Game"));
        _others.Add(game);
        using var inside = Start(game, "--tag", "game");
        using var dummy = Start(_app);
        var closing = _closer.Close(LocalId);
        await closing.Closed.WaitAsync(Wait, Ct);
        Assert.True(closing.ForceClose());
        Assert.False(inside.HasExited);
        closing.Reopen();
    }

    // Reopened as it was started: in its own working folder, but with the user's environment, not this process's.
    [Fact]
    public async Task ReopenedApp_KeepsItsWorkingFolder_AndGetsTheUsersEnvironment()
    {
        var work = Directory.CreateDirectory(Path.Combine(_root, "work")).FullName;
        var report = Path.Combine(_root, "report.txt");
        Environment.SetEnvironmentVariable(Mark, "inherited");
        try
        {
            var info = new ProcessStartInfo(Path.Combine(_app, "TinyTracker.DummyApp.exe")) { WorkingDirectory = work, UseShellExecute = false };
            foreach (var arg in new[] { "--report", report, "--tag", "work" }) info.ArgumentList.Add(arg);
            using (var dummy = Process.Start(info)!)
            {
                dummy.WaitForInputIdle(10000);
                await Until(() => Report(report) is not null, "The app didn't report.");
                Assert.Equal([work, "inherited"], Report(report)!);
                File.Delete(report);
                var closing = _closer.Close(LocalId);
                await closing.Closed.WaitAsync(Wait, Ct);
                closing.Reopen();
            }
            await Until(() => Report(report) is not null, "The app didn't reopen.");
            Assert.Equal([work, ""], Report(report)!);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Mark, null);
        }
    }

    [Fact]
    public async Task AppStartedAgainMeanwhile_IsNotStartedTwice()
    {
        using (Start(_app, "--tag", "again"))
        {
            var closing = _closer.Close(LocalId);
            await closing.Closed.WaitAsync(Wait, Ct);
            // As when the installer starts it again.
            using var again = Start(_app, "--tag", "again");
            closing.Reopen();
        }
        await Task.Delay(1000, Ct);
        Assert.Equal(1, Running("again"));
    }

    [Fact]
    public void ConsolePrograms_AreToldApart()
    {
        Assert.True(RunningApps.IsConsole(Path.Combine(Environment.SystemDirectory, "cmd.exe")));
        Assert.False(RunningApps.IsConsole(Path.Combine(_app, "TinyTracker.DummyApp.exe")));
        Assert.False(RunningApps.IsConsole(Path.Combine(_root, "missing.exe")));
    }

    [Fact]
    public void NoOtherAppIsInstalledInsideATestFolder() => Assert.Empty(AppCloser.OthersInside(_root));

    // The Store's apps folder holds every Store app: never an app's folder itself, though a package's folder inside it is (spec §6.3).
    // Its folder at the root of another drive it installs to is the same.
    [Fact]
    public void StoreAppsFolder_IsNeverAnAppsFolder_ThoughAPackageInsideIs()
    {
        var windowsApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
        var folders = AppCloser.FoldersOfThisPc(AppContext.BaseDirectory);
        Assert.Null(folders.Choose(windowsApps, null));
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
            Assert.Null(folders.Choose(Path.Combine(drive.Name, "WindowsApps"), null));
        var package = Path.Combine(windowsApps, "Contoso.Paint_1.0.0.0_x64__8wekyb3d8bbwe");
        Assert.Equal(package, folders.Choose(package, null));
    }

    [Fact]
    public void FolderLookup_OfAnAppThatIsntThere_FindsNone()
    {
        var lookup = AppCloser.FolderLookup(AppContext.BaseDirectory);
        Assert.Null(lookup(@"ARP\Machine\X64\Nobody.NoSuchApp"));
        Assert.Null(lookup(@"MSIX\Nobody.NoSuchApp_1.0.0.0_x64__abcdefgh"));
        Assert.Null(lookup("Example.Editor"));
    }
}
