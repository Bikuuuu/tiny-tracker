using TinyTracker.WinGet.Cli;
using TinyTracker.WinGet.Tests.Integration;
using Xunit;
using static TinyTracker.WinGet.Tests.Cli.FakeWinGet;

namespace TinyTracker.WinGet.Tests.Cli;

// winget's proxy option, as `winget settings export` tells it, policies included (spec §6.4).
public sealed class WinGetSettingsTests
{
    private const string On = """{"$schema":"https://aka.ms/winget-settings-export.schema.json","adminSettings":{"LocalManifestFiles":false,"ProxyCommandLineOptions":true},"userSettingsFile":"settings.json"}""";
    private const string Off = """{"adminSettings":{"ProxyCommandLineOptions":false}}""";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Export_SaysWhetherTheOptionIsOn()
    {
        Assert.True(WinGetSettings.ProxyOptionOf([On]));
        Assert.False(WinGetSettings.ProxyOptionOf([Off]));
        // What winget prints before its JSON doesn't count, and neither does a JSON spread over lines.
        Assert.True(WinGetSettings.ProxyOptionOf(["Failed to load settings.", "{", "\"adminSettings\": { \"ProxyCommandLineOptions\": true }", "}"]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"adminSettings":{}}""")]
    [InlineData("""{"adminSettings":{"ProxyCommandLineOptions":"yes"}}""")]
    [InlineData("""{"userSettingsFile":"settings.json"}""")]
    public void ExportThatDoesntSay_IsUnknown(string line) => Assert.Null(WinGetSettings.ProxyOptionOf([line]));

    [Fact]
    public async Task Read_RunsTheExport()
    {
        using var fake = new FakeWinGet(Scenario(0, Say(On)));
        Assert.True(await WinGetSettings.ProxyOptionAsync(fake.Cli, Ct));
        Assert.Equal(["settings", "export"], fake.Arguments);
    }

    [Fact]
    public async Task ExportThatFails_IsUnknown()
    {
        using var fake = new FakeWinGet(Scenario(unchecked((int)0x8A15003A), Say(On)));
        Assert.Null(await WinGetSettings.ProxyOptionAsync(fake.Cli, Ct));
    }

    [Fact]
    public async Task Enable_IsDoneOnceTheExportReadsOn()
    {
        using var fake = new FakeWinGet(Scenario(0, Say(On)));
        Assert.Null(await WinGetSettings.EnableProxyOptionAsync(fake.Cli, Ct));
    }

    // As when the administrator who approved isn't the user, or a policy keeps it off.
    [Fact]
    public async Task Enable_ThatLeavesItOff_SaysSo()
    {
        using var fake = new FakeWinGet(Scenario(0, Say(Off)));
        Assert.Equal("still off", await WinGetSettings.EnableProxyOptionAsync(fake.Cli, Ct));
    }

    // The uninstaller's, for the account it runs as (spec §10).
    [Fact]
    public async Task Disable_IsDoneOnceTheExportReadsOff()
    {
        using var fake = new FakeWinGet(Scenario(0, Say(Off)));
        Assert.Null(await WinGetSettings.DisableProxyOptionAsync(fake.Cli, Ct));
    }

    // As when a policy keeps it on.
    [Fact]
    public async Task Disable_ThatLeavesItOn_SaysSo()
    {
        using var fake = new FakeWinGet(Scenario(0, Say(On)));
        Assert.Equal("still on", await WinGetSettings.DisableProxyOptionAsync(fake.Cli, Ct));
    }

    [Fact]
    public async Task Disable_ThatWinGetRefuses_SaysItsCode()
    {
        using var fake = new FakeWinGet(Scenario(unchecked((int)0x8A15003A), Say(On)));
        Assert.Equal("0x8A15003A", await WinGetSettings.DisableProxyOptionAsync(fake.Cli, Ct));
    }

    // Details shows winget's own code.
    [Fact]
    public async Task Enable_ThatWinGetRefuses_SaysItsCode()
    {
        using var fake = new FakeWinGet(Scenario(unchecked((int)0x8A15003A), Say(Off)));
        Assert.Equal("0x8A15003A", await WinGetSettings.EnableProxyOptionAsync(fake.Cli, Ct));
    }

    [Fact]
    public async Task Enable_WithoutWinGet_SaysSo() =>
        Assert.Equal("winget didn't run", await WinGetSettings.EnableProxyOptionAsync(new WinGetCli(Path.Combine(Path.GetTempPath(), "tinytracker-tests-" + Guid.NewGuid().ToString("N"), "winget.exe"), _ => true), Ct));

    // Read-only.
    [Fact]
    public async Task RealWinGet_SaysWhetherItsProxyOptionIsOn()
    {
        await RealWinGet.RequireCliAsync();
        Assert.NotNull(await WinGetSettings.ProxyOptionAsync(WinGetCli.Real, Ct));
    }
}
