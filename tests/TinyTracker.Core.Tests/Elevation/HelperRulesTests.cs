using TinyTracker.Core.Elevation;
using TinyTracker.Core.SelfUpdate;
using Xunit;

namespace TinyTracker.Core.Tests.Elevation;

public sealed class HelperRulesTests
{
    [Theory]
    [InlineData("Mozilla.Firefox")]
    [InlineData("Notepad++.Notepad++")]
    [InlineData("VideoLAN.VLC")]
    [InlineData("Example.Editor.Beta")]
    public void WinGetIds_AreAccepted(string id) => Assert.True(HelperRules.IsId(id));

    [Theory]
    [InlineData("")]
    [InlineData("Firefox")]
    [InlineData("Mozilla..Firefox")]
    [InlineData(".Mozilla.Firefox")]
    [InlineData("Mozilla.Firefox.")]
    [InlineData("Mozilla Corp.Firefox")]
    [InlineData(@"Mozilla.Fire\fox")]
    [InlineData("Mozilla.Fire/fox")]
    [InlineData("Mozilla.Fire\"fox")]
    [InlineData("Mozilla.Fire:fox")]
    [InlineData("A.B.C.D.E.F.G.H.I")]
    [InlineData("Mozilla.ThisPartIsLongerThanThirtyTwoCharacters")]
    public void OtherIds_AreRefused(string id) => Assert.False(HelperRules.IsId(id));

    [Fact]
    public void IdsWithControlCharactersOrTooLong_AreRefused()
    {
        Assert.False(HelperRules.IsId("Mozilla.Firefox\n"));
        Assert.False(HelperRules.IsId("Mozilla.Fire\u0000fox"));
        Assert.False(HelperRules.IsId(null));
        // Five parts of 31 characters: each part fits, the whole doesn't.
        Assert.False(HelperRules.IsId(string.Join('.', Enumerable.Repeat(new string('a', 31), 5))));
    }

    [Theory]
    [InlineData("131.0")]
    [InlineData("3.0.20")]
    [InlineData("2025.1.3-beta+1")]
    [InlineData("1.0 RC")]
    public void WinGetVersions_AreAccepted(string version) => Assert.True(HelperRules.IsVersion(version));

    [Theory]
    [InlineData("")]
    [InlineData("1/2")]
    [InlineData(@"1\2")]
    [InlineData("1:2")]
    [InlineData("1*")]
    [InlineData("1?")]
    [InlineData("1<2")]
    [InlineData("1|2")]
    public void OtherVersions_AreRefused(string version) => Assert.False(HelperRules.IsVersion(version));

    [Fact]
    public void VersionsWithControlCharactersOrTooLong_AreRefused()
    {
        Assert.False(HelperRules.IsVersion("1.0\n"));
        Assert.False(HelperRules.IsVersion("1.\u00000"));
        Assert.False(HelperRules.IsVersion(null));
        Assert.False(HelperRules.IsVersion(new string('1', 129)));
        Assert.True(HelperRules.IsVersion(new string('1', 128)));
    }

    [Theory]
    [InlineData("winget", true)]
    [InlineData("WinGet", true)]
    [InlineData("msstore", false)]
    [InlineData("", false)]
    public void Upgrade_NeedsTheWinGetSource(string source, bool valid) =>
        Assert.Equal(valid, HelperRules.IsValid(new UpgradeRequest(1, "Mozilla.Firefox", source, "131.0", 0)));

    [Fact]
    public void Upgrade_NeedsAValidIdAndVersion()
    {
        Assert.False(HelperRules.IsValid(new UpgradeRequest(1, "Firefox", "winget", "131.0", 0)));
        Assert.False(HelperRules.IsValid(new UpgradeRequest(1, "Mozilla.Firefox", "winget", "", 0)));
    }

    // None, or what the Settings box takes (spec §8).
    [Theory]
    [InlineData(0, true)]
    [InlineData(100, true)]
    [InlineData(17500, true)]
    [InlineData(1_000_000, true)]
    [InlineData(99, false)]
    [InlineData(1_000_001, false)]
    [InlineData(-1, false)]
    public void Upgrade_NeedsASpeedLimitSettingsCouldHave(int kbps, bool valid) =>
        Assert.Equal(valid, HelperRules.IsValid(new UpgradeRequest(1, "Mozilla.Firefox", "winget", "131.0", kbps)));

    // Only a newer plain version of Tiny Tracker itself: never a path or an address (spec §8).
    [Theory]
    [InlineData("0.2.0", 0, true)]
    [InlineData("1.0.0", 17500, true)]
    [InlineData("0.1.0", 0, false)]
    [InlineData("0.0.9", 0, false)]
    [InlineData("v0.2.0", 0, false)]
    [InlineData("0.2.0-beta", 0, false)]
    [InlineData("0.2", 0, false)]
    [InlineData(@"C:\Temp\setup.exe", 0, false)]
    [InlineData("https://example.com/setup.exe", 0, false)]
    [InlineData("0.2.0", 99, false)]
    [InlineData("0.2.0", 1_000_001, false)]
    public void SelfUpdate_NeedsANewerPlainVersion_AndASpeedLimitSettingsCouldHave(string version, int kbps, bool valid) =>
        Assert.Equal(valid, HelperRules.IsValid(new SelfUpdateRequest(1, version, kbps), new SelfVersion(0, 1, 0)));

    [Fact]
    public void PipeNames_AreNewEachTime_AndOnlyTheirOwnFormPasses()
    {
        var name = HelperRules.NewPipeName();
        Assert.True(HelperRules.IsPipeName(name));
        Assert.NotEqual(name, HelperRules.NewPipeName());
        Assert.False(HelperRules.IsPipeName("TinyTracker.Helper."));
        Assert.False(HelperRules.IsPipeName(name.ToUpperInvariant()));
        Assert.False(HelperRules.IsPipeName(name + "0"));
        Assert.False(HelperRules.IsPipeName(@"\\.\pipe\" + name));
        Assert.False(HelperRules.IsPipeName(name + "\n"));
        Assert.False(HelperRules.IsPipeName(null));
    }
}
