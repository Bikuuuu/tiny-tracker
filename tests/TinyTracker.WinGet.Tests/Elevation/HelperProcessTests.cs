using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Principal;
using TinyTracker.Core.Elevation;
using TinyTracker.Core.Installing;
using TinyTracker.Core.SelfUpdate;
using TinyTracker.Core.Tracking;
using TinyTracker.WinGet.Elevation;
using Xunit;

namespace TinyTracker.WinGet.Tests.Elevation;

// The real helper exe, started without elevation: it serves one app, refuses other command lines, and goes (spec §5.1).
public class HelperProcessTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);
    private static readonly string HelperPath = typeof(HelperProcessTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "HelperPath").Value!;
    private static readonly string Me = WindowsIdentity.GetCurrent().User!.Value;
    private static readonly PackageKey Nobody = new("Nobody.NoSuchPackage.Anywhere", "winget");
    // The helper's own version, as its file says.
    private static readonly SelfVersion Own = SelfVersion.Parse(string.Join('.', FileVersionInfo.GetVersionInfo(HelperPath).FileVersion!.Split('.').Take(3)))!;

#if DEBUG
    private static readonly bool DebugBuild = true;
#else
    private static readonly bool DebugBuild = false;
#endif

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Process Start(params string[] args)
    {
        var info = new ProcessStartInfo(HelperPath) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return Process.Start(info)!;
    }

    private static async Task<int> ExitCodeOf(Process helper)
    {
        await helper.WaitForExitAsync(Ct).WaitAsync(Wait, Ct);
        return helper.ExitCode;
    }

    [Fact]
    public async Task RealHelper_ServesTheApp_ThenLeaves()
    {
        var pipe = HelperRules.NewPipeName();
        using var helper = Start("--pipe", pipe, "--user", Me);
        using (var client = await HelperClient.ConnectAsync(pipe, HelperPath, elevated: false, Wait, Ct))
        {
            // Read-only: a package that isn't there, and winget missing on some runners.
            var outcome = await client.UpgradeAsync(Nobody, "1.0", null, Ct);
            Assert.True(outcome.Result is UpgradeResult.NotInstalled || outcome.Failure == UpgradeFailure.WinGetUnavailable, outcome.ToString());
        }
        Assert.Equal(0, await ExitCodeOf(helper));
    }

    public static TheoryData<string[]> NotItsCommandLine() => new()
    {
        { [] },
        { ["--pipe", "Example.Pipe", "--user", Me] },
        { ["--pipe", HelperRules.NewPipeName(), "--user", "S-1-1-0"] },
        { ["--pipe", HelperRules.NewPipeName(), "--user", Me, "--verbose"] },
    };

    [Theory]
    [MemberData(nameof(NotItsCommandLine))]
    public async Task OtherCommandLines_ExitWithTwo(string[] args)
    {
        using var helper = Start(args);
        Assert.Equal(2, await ExitCodeOf(helper));
    }

    [Fact]
    public async Task NameAnotherProgramTookFirst_ExitsWithThree()
    {
        var pipe = HelperRules.NewPipeName();
        using var squatter = new NamedPipeServerStream(pipe, PipeDirection.InOut, 4);
        using var helper = Start("--pipe", pipe, "--user", Me);
        Assert.Equal(3, await ExitCodeOf(helper));
    }

    [Fact]
    public async Task DemoHelper_FakesTheUpgrade_InDebugBuildsOnly()
    {
        var pipe = HelperRules.NewPipeName();
        using var helper = Start("--pipe", pipe, "--user", Me, "--demo");
        if (!DebugBuild)
        {
            Assert.Equal(2, await ExitCodeOf(helper));
            return;
        }
        using var client = await HelperClient.ConnectAsync(pipe, HelperPath, elevated: false, Wait, Ct);
        var stages = new List<UpgradeStage>();
        var outcome = await client.UpgradeAsync(new PackageKey("Proseware.Maps", "winget"), "2025.2", new Reported<UpgradeProgress>(p =>
        {
            lock (stages) stages.Add(p.Stage);
        }), Ct).WaitAsync(Wait, Ct);
        Assert.Equal(UpgradeResult.Updated, outcome.Result);
        // Queued may give way to the download's first progress while it waits unread (spec §8).
        lock (stages) Assert.Equal([UpgradeStage.Downloading, UpgradeStage.Installing], stages.Distinct().SkipWhile(s => s == UpgradeStage.Queued));
    }

    // Read-only: the helper checks the request before it asks GitHub anything.
    [Fact]
    public async Task RealHelper_RefusesASelfUpdateThatIsntNewer()
    {
        var pipe = HelperRules.NewPipeName();
        using var helper = Start("--pipe", pipe, "--user", Me);
        using (var client = await HelperClient.ConnectAsync(pipe, HelperPath, elevated: false, Wait, Ct))
        {
            Assert.Equal(new UpgradeOutcome(UpgradeResult.Failed, UpgradeFailure.Other, "refused"), await client.SelfUpdateAsync(Own, null, Ct).WaitAsync(Wait, Ct));
        }
        Assert.Equal(0, await ExitCodeOf(helper));
    }

    // Only Tiny Tracker's own folder in Program Files is one only administrators can write, and that Setup replaces.
    [Fact]
    public async Task RealHelperOutsideItsFolder_DoesntSelfUpdate()
    {
        var pipe = HelperRules.NewPipeName();
        using var helper = Start("--pipe", pipe, "--user", Me);
        using (var client = await HelperClient.ConnectAsync(pipe, HelperPath, elevated: false, Wait, Ct))
        {
            var outcome = await client.SelfUpdateAsync(Own with { Major = Own.Major + 1 }, null, Ct).WaitAsync(Wait, Ct);
            Assert.Equal(new UpgradeOutcome(UpgradeResult.Failed, UpgradeFailure.Other, "not in Tiny Tracker's folder"), outcome);
            Assert.False(helper.HasExited);
        }
        Assert.Equal(0, await ExitCodeOf(helper));
    }

    // The account running the tests has no silent-mode task, so the launcher can't start the helper without a prompt. Where an
    // installed copy's silent mode made one, the launcher would start that copy's helper.
    [Fact]
    public async Task SilentModeWithoutItsTask_SaysSo_AndNeverPromptsForAnUpdateThatStartedByItself()
    {
        Assert.SkipWhen(SilentTask.Registered(WindowsIdentity.GetCurrent().User!), "This account's silent mode is on here, so its task is there.");
        var missing = 0;
        var start = await new HelperLauncher(HelperPath, silentMode: () => true, owner: () => 0, demo: false, taskMissing: () => missing++).StartAsync(mayPrompt: false, Ct);
        Assert.Equal((new HelperStart(HelperStartResult.NeedsPrompt), 1), (start, missing));
    }

    [Fact]
    public void Launcher_ShowsAPromptOnlyOutsideSilentMode()
    {
        Assert.True(new HelperLauncher(HelperPath, silentMode: () => false, owner: () => 0, demo: false).Prompts);
        Assert.False(new HelperLauncher(HelperPath, silentMode: () => true, owner: () => 0, demo: false).Prompts);
    }

    // The demo's silent mode never prompts, so this can't show a prompt even when the check is missing.
    [Fact]
    public async Task CancelledStart_StartsNothing()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var start = await new HelperLauncher(HelperPath, silentMode: () => true, owner: () => 0, demo: true).StartAsync(mayPrompt: false, cancelled.Token);
        Assert.Equal(new HelperStart(HelperStartResult.Failed, Code: "0x800704C7"), start);
    }

    [Fact]
    public async Task UpdateThatStartedByItself_NeverGetsAPrompt()
    {
        var start = await new HelperLauncher(HelperPath, silentMode: () => false, owner: () => 0, demo: false).StartAsync(mayPrompt: false, Ct);
        Assert.Equal(new HelperStart(HelperStartResult.NeedsPrompt), start);
    }

#if DEBUG
    [Fact]
    public async Task DemoHelper_FakesTurningTheProxyOptionOn()
    {
        var pipe = HelperRules.NewPipeName();
        using var helper = Start("--pipe", pipe, "--user", Me, "--demo");
        using var client = await HelperClient.ConnectAsync(pipe, HelperPath, elevated: false, Wait, Ct);
        Assert.Null(await client.EnableProxyOptionAsync(Ct).WaitAsync(Wait, Ct));
    }

    // The limit reaches the helper exe over its pipe, and the demo's helper downloads at it.
    [Fact]
    public async Task DemoHelper_DownloadsAtTheLimit()
    {
        var pipe = HelperRules.NewPipeName();
        using var helper = Start("--pipe", pipe, "--user", Me, "--demo");
        using var client = await HelperClient.ConnectAsync(pipe, HelperPath, elevated: false, Wait, Ct, new SpeedLimit(40_960));
        var seen = new List<(TimeSpan, UpgradeProgress)>();
        var clock = Stopwatch.StartNew();
        var outcome = await client.UpgradeAsync(new PackageKey("Proseware.Maps", "winget"), "2025.2", new Reported<UpgradeProgress>(p =>
        {
            lock (seen) seen.Add((clock.Elapsed, p));
        }), Ct).WaitAsync(Wait, Ct);
        Assert.Equal(UpgradeResult.Updated, outcome.Result);
        lock (seen) Assert.InRange(Downloads.SpeedOf(seen), 25_000, 45_000);
    }

    // The demo's helper downloads nothing and starts nothing, but it leaves as after a real Setup started.
    [Fact]
    public async Task DemoHelper_PretendsASelfUpdate_ThenGoes()
    {
        var pipe = HelperRules.NewPipeName();
        using var helper = Start("--pipe", pipe, "--user", Me, "--demo");
        using var client = await HelperClient.ConnectAsync(pipe, HelperPath, elevated: false, Wait, Ct);
        var stages = new List<UpgradeStage>();
        var outcome = await client.SelfUpdateAsync(new SelfVersion(9, 9, 9), new Reported<UpgradeProgress>(p =>
        {
            lock (stages) stages.Add(p.Stage);
        }), Ct).WaitAsync(Wait, Ct);
        Assert.Equal(new UpgradeOutcome(UpgradeResult.Updated), outcome);
        lock (stages) Assert.Equal([UpgradeStage.Downloading], stages.Distinct());
        Assert.Equal(0, await ExitCodeOf(helper));
    }

    [Fact]
    public async Task DemoSilentMode_StartsTheHelperWithoutAPrompt()
    {
        var start = await new HelperLauncher(HelperPath, silentMode: () => true, owner: () => 0, demo: true).StartAsync(mayPrompt: false, Ct);
        Assert.Equal(HelperStartResult.Started, start.Result);
        using var session = start.Session!;
        Assert.True(session.WinGetAvailable);
        Assert.Equal(UpgradeResult.Updated, (await session.UpgradeAsync(new PackageKey("Proseware.Maps", "winget"), "2025.2", null, Ct).WaitAsync(Wait, Ct)).Result);
    }
#endif
}
