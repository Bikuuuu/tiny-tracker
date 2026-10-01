using TinyTracker.WinGet.Cli;
using TinyTracker.WinGet.Tests.Integration;
using Xunit;
using static TinyTracker.WinGet.Tests.Cli.FakeWinGet;

namespace TinyTracker.WinGet.Tests.Cli;

// winget's command line runs only once it's checked to be App Installer's own (spec §6.4, §8).
public sealed class WinGetCliTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);
    private static readonly string WindowsApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
    private static readonly string Folder = Path.Combine(WindowsApps, "Microsoft.DesktopAppInstaller_1.29.379.0_x64__8wekyb3d8bbwe");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<ProgramIdentity, bool> Identities() => new()
    {
        { new ProgramIdentity(Path.Combine(Folder, "winget.exe"), WinGetCli.AppInstallerFamily, Folder), true },
        { new ProgramIdentity(Path.Combine(Folder.ToUpperInvariant(), "WINGET.EXE"), WinGetCli.AppInstallerFamily.ToLowerInvariant(), Folder + @"\"), true },
        // Not running as App Installer's package.
        { new ProgramIdentity(Path.Combine(Folder, "winget.exe"), null, null), false },
        { new ProgramIdentity(Path.Combine(Folder, "winget.exe"), "Contoso.Tools_8wekyb3d8bbwe", Folder), false },
        // Another program of the package, or one outside its folder.
        { new ProgramIdentity(Path.Combine(Folder, "AppInstallerCLI.exe"), WinGetCli.AppInstallerFamily, Folder), false },
        { new ProgramIdentity(Path.Combine(Path.GetDirectoryName(WinGetCli.AliasPath)!, "winget.exe"), WinGetCli.AppInstallerFamily, Folder), false },
        // A package folder anywhere but directly inside WindowsApps, as a developer's registered folder.
        { new ProgramIdentity(@"C:\Dev\Microsoft.DesktopAppInstaller\winget.exe", WinGetCli.AppInstallerFamily, @"C:\Dev\Microsoft.DesktopAppInstaller"), false },
        { new ProgramIdentity(Path.Combine(WindowsApps, "Deleted", "App", "winget.exe"), WinGetCli.AppInstallerFamily, Path.Combine(WindowsApps, "Deleted", "App")), false },
        { new ProgramIdentity(null, WinGetCli.AppInstallerFamily, Folder), false },
    };

    [Theory]
    [MemberData(nameof(Identities))]
    public void OnlyAppInstallersOwnWinGet_IsTrusted(ProgramIdentity program, bool trusted) => Assert.Equal(trusted, WinGetCli.IsAppInstallers(program));

    [Fact]
    public async Task TrustedProgram_GetsItsArgumentsAsGiven_AndItsOutputComesBack()
    {
        using var fake = new FakeWinGet(Scenario(0x2A, Say("Found Example Editor [Example.Editor] Version 2.5.0"), Say("Ünïcode ✓")));
        string[] arguments = ["upgrade", "--version", "1.0 RC", "a\"b", @"C:\Example Folder\", "", "tail"];
        var run = await fake.Cli.RunAsync(arguments, Ct);
        Assert.Equal(0x2A, run!.Value.ExitCode);
        Assert.Equal(["Found Example Editor [Example.Editor] Version 2.5.0", "Ünïcode ✓"], run.Value.Lines);
        Assert.Equal(arguments, fake.Arguments);
    }

    [Fact]
    public async Task UntrustedProgram_IsEndedBeforeItRunsAStep()
    {
        using var fake = new FakeWinGet(Scenario(0, Say("hello")));
        Assert.Null(new WinGetCli(fake.Exe, _ => false).Start(["--version"]));
        Assert.Null(await new WinGetCli(fake.Exe, _ => false).RunAsync(["--version"], Ct));
        await Task.Delay(500, Ct);
        Assert.False(fake.Ran);
    }

    // What a program the user runs could put in place of winget's alias.
    [Fact]
    public async Task StandInNamedWinGet_IsNotAppInstallers_AndNeverRuns()
    {
        using var fake = new FakeWinGet(Scenario(0, Say("hello")), exe: "winget.exe");
        Assert.Null(new WinGetCli(fake.Exe, WinGetCli.IsAppInstallers).Start(["--version"]));
        await Task.Delay(500, Ct);
        Assert.False(fake.Ran);
    }

    [Fact]
    public void MissingProgram_StartsNothing() =>
        Assert.Null(new WinGetCli(Path.Combine(Path.GetTempPath(), "tinytracker-tests-" + Guid.NewGuid().ToString("N"), "winget.exe"), _ => true).Start(["--version"]));

    [Fact]
    public async Task End_StopsItAtOnce()
    {
        using var fake = new FakeWinGet(Scenario(0, Say("working"), Sleep(60_000)));
        using var process = fake.Cli.Start(["upgrade"])!;
        Assert.Equal("working", await process.Lines.ReadAsync(Ct).AsTask().WaitAsync(Wait, Ct));
        process.End();
        Assert.Equal(1, await process.Exited.WaitAsync(Wait, Ct));
    }

    // Its own console host doesn't count.
    [Fact]
    public async Task ProgramItStarts_IsSeen()
    {
        using var fake = new FakeWinGet(Scenario(0, Say("ready"), Sleep(1000), Say("installing"), Installer(3000)));
        using var process = fake.Cli.Start(["upgrade"])!;
        Assert.Equal("ready", await process.Lines.ReadAsync(Ct).AsTask().WaitAsync(Wait, Ct));
        Assert.False(process.HasChildren());
        Assert.Equal("installing", await process.Lines.ReadAsync(Ct).AsTask().WaitAsync(Wait, Ct));
        var deadline = DateTime.UtcNow + Wait;
        while (!process.HasChildren())
        {
            Assert.True(DateTime.UtcNow < deadline, "The installer wasn't seen.");
            await Task.Delay(50, Ct);
        }
        Assert.Equal(0, await process.Exited.WaitAsync(Wait, Ct));
    }

    // Read-only: winget's version.
    [Fact]
    public async Task RealWinGet_IsAppInstallers_AndAnswers()
    {
        await RealWinGet.RequireCliAsync();
        var run = await WinGetCli.Real.RunAsync(["--version"], Ct);
        Assert.NotNull(run);
        Assert.Equal(0, run.Value.ExitCode);
        Assert.StartsWith("v1.", Assert.Single(run.Value.Lines));
    }
}
