using System.Runtime.InteropServices;
using TinyTracker.Core.Checking;
using TinyTracker.Core.Settings;
using TinyTracker.Presentation.Settings;
using Xunit;

namespace TinyTracker.Presentation.Tests.Settings;

public class DiagnosticFactsTests
{
    private static readonly DiagnosticFacts Facts = new("0.1.0", new Version(10, 0, 22631, 0), Architecture.X64, new Version(10, 0, 1), "1.29.380", 3,
        new AppSettings(), true, new DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.FromHours(2)), CheckProblem.WinGetUnreachable,
        new DateTimeOffset(2026, 9, 25, 2, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Text_HoldsVersionsCountsAndSettings()
    {
        var expected = """
            Tiny Tracker 0.1.0
            Windows 10.0.22631.0 X64
            .NET 10.0.1
            winget 1.29.380
            Tracked apps: 3
            Last check: 2026-09-25T08:00:00.0000000+00:00 WinGetUnreachable
            Last good check: 2026-09-25T02:00:00.0000000+00:00
            Check every: 6 h
            Update apps automatically: off
            Silent mode: off
            Wait before auto-installing: 0 days
            Security fixes first: on
            Install window: off (22 to 6)
            Pause during games: on
            Speed limit: off (17500 KB/s)
            Notifications: All
            Start with Windows: on
            Shortcut: Shift, Windows + 0x55
            Auto self-update: on
            What's new links: on
            """;
        Assert.Equal(expected.ReplaceLineEndings(), Facts.Text());
    }

    [Theory]
    [InlineData("v1.29.380", "winget 1.29.380")]
    [InlineData(@"D:\Example\AppData", "winget unknown")]
    [InlineData("1.29.380 (Someone's PC)", "winget unknown")]
    [InlineData(null, "winget unavailable")]
    public void WinGet_GetsThroughOnlyAsAVersionNumber(string? reported, string line) =>
        Assert.Contains(line, (Facts with { WinGet = reported }).Text().Split(Environment.NewLine));

    [Fact]
    public void NoCheckYet_AndNoShortcut_SayNone()
    {
        var text = (Facts with { LastCheck = null, LastGoodCheck = null, Settings = new AppSettings { OpenShortcut = null } }).Text();
        Assert.Contains("Last check: none", text);
        Assert.Contains("Last good check: none", text);
        Assert.Contains("Shortcut: none", text);
    }

    [Fact]
    public void ChecksThatNeverSucceeded_StillNameTheirProblem()
    {
        var text = (Facts with { LastGoodCheck = null, LastProblem = CheckProblem.WinGetTooOld }).Text();
        Assert.Contains("Last check: 2026-09-25T08:00:00.0000000+00:00 WinGetTooOld", text);
        Assert.Contains("Last good check: none", text);
    }

    [Fact]
    public void Gather_ReadsThisPcsVersions()
    {
        var facts = DiagnosticFacts.Gather("0.1.0", 0, new AppSettings(), false, null, CheckProblem.None, null);
        Assert.Equal((Environment.OSVersion.Version, Environment.Version, (string?)null), (facts.Windows, facts.DotNet, facts.WinGet));
    }
}
