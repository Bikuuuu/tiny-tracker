using TinyTracker.Core.SelfUpdate;
using TinyTracker.Core.Settings;
using TinyTracker.Core.Tracking;
using Xunit;

namespace TinyTracker.Core.Tests.SelfUpdate;

// Tiny Tracker installs its own update by itself only as an Auto app would, and only where that needs no prompt (spec §6.5).
public class SelfUpdateRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 8, 0, 0, TimeSpan.Zero);
    private static readonly SelfVersion Running = new(0, 1, 0);
    private static readonly SelfRelease Release = new(new SelfVersion(0, 2, 0), Now.AddDays(-10));
    private static readonly SystemState Calm = new(FullScreen: false, Metered: false, BatterySaver: false);
    private static readonly AppSettings Silent = new() { SilentMode = true };

    private static AutoBlock Check(SelfRelease? release, SelfUpdateBook? book = null, AppSettings? settings = null, SystemState? system = null) =>
        SelfUpdateRules.Check(release, Running, book ?? new SelfUpdateBook(), settings ?? Silent, system ?? Calm, Now, TimeZoneInfo.Utc);

    private static SelfUpdateBook Attempted(string version, TimeSpan ago) => new() { AttemptedVersion = version, AttemptedAt = Now - ago };

    [Fact]
    public void AllClear_InstallsByItself() => Assert.Equal(AutoBlock.None, Check(Release));

    [Fact]
    public void SwitchOff_WaitsForTheClick() => Assert.Equal(AutoBlock.AutoOff, Check(Release, settings: Silent with { AutoSelfUpdate = false }));

    [Fact]
    public void NoRelease_IsNothingToInstall() => Assert.Equal(AutoBlock.NotAvailable, Check(null));

    [Theory]
    [InlineData(0, 1, 0)]
    [InlineData(0, 0, 9)]
    public void ReleaseThatIsntNewer_IsNothingToInstall(int major, int minor, int patch) =>
        Assert.Equal(AutoBlock.NotAvailable, Check(Release with { Version = new SelfVersion(major, minor, patch) }));

    // Installing into Program Files always needs admin rights, so without silent mode it would prompt.
    [Fact]
    public void WithoutSilentMode_WaitsForTheClick() => Assert.Equal(AutoBlock.NeedsAdmin, Check(Release, settings: new AppSettings()));

    [Fact]
    public void WithinTheWait_IsTooNew() =>
        Assert.Equal(AutoBlock.TooNew, Check(Release with { PublishedAt = Now.AddDays(-2) }, settings: Silent with { AutoInstallWaitDays = 3 }));

    [Fact]
    public void AfterTheWait_Installs() =>
        Assert.Equal(AutoBlock.None, Check(Release with { PublishedAt = Now.AddDays(-3) }, settings: Silent with { AutoInstallWaitDays = 3 }));

    // Now is 08:00.
    [Fact]
    public void OutsideTheInstallWindow_IsHeldBack() =>
        Assert.Equal(AutoBlock.OutsideWindow, Check(Release, settings: Silent with { InstallWindowEnabled = true, InstallWindowFrom = 22, InstallWindowTo = 6 }));

    [Fact]
    public void InsideTheInstallWindow_Installs() =>
        Assert.Equal(AutoBlock.None, Check(Release, settings: Silent with { InstallWindowEnabled = true, InstallWindowFrom = 8, InstallWindowTo = 9 }));

    [Fact]
    public void Offline_IsHeldBack() => Assert.Equal(AutoBlock.Offline, Check(Release, system: Calm with { Offline = true }));

    [Fact]
    public void Metered_IsHeldBack() => Assert.Equal(AutoBlock.Metered, Check(Release, system: Calm with { Metered = true }));

    [Fact]
    public void EnergySaver_IsHeldBack() => Assert.Equal(AutoBlock.BatterySaver, Check(Release, system: Calm with { BatterySaver = true }));

    [Fact]
    public void AttemptOnThisVersionWithin12Hours_IsHeldBack() =>
        Assert.Equal(AutoBlock.RecentlyAttempted, Check(Release, Attempted("0.2.0", TimeSpan.FromHours(11))));

    [Fact]
    public void Attempt12HoursAgo_TriesAgain() => Assert.Equal(AutoBlock.None, Check(Release, Attempted("0.2.0", TimeSpan.FromHours(12))));

    [Fact]
    public void AttemptOnAnotherVersion_DoesntCount() => Assert.Equal(AutoBlock.None, Check(Release, Attempted("0.1.5", TimeSpan.FromHours(1))));

    // An attempt after now means the clock went back.
    [Fact]
    public void AttemptAfterNow_DoesntCount() => Assert.Equal(AutoBlock.None, Check(Release, Attempted("0.2.0", TimeSpan.FromHours(-1))));

    // A failure that waits for the user holds its version back, after a restart too, but never a newer one (spec §6.5).
    [Fact]
    public void FailedVersion_WaitsForTheClick() => Assert.Equal(AutoBlock.AutoOff, Check(Release, new SelfUpdateBook { FailedVersion = "0.2.0" }));

    [Fact]
    public void FailureOnAnotherVersion_DoesntCount() => Assert.Equal(AutoBlock.None, Check(Release, new SelfUpdateBook { FailedVersion = "0.1.5" }));

    [Fact]
    public void FullScreen_HoldsBackWhenPausingForGames() => Assert.Equal(AutoBlock.FullScreen, Check(Release, system: Calm with { FullScreen = true }));

    [Fact]
    public void FullScreen_IsIgnoredWhenNotPausingForGames() =>
        Assert.Equal(AutoBlock.None, Check(Release, settings: Silent with { PauseDuringGames = false }, system: Calm with { FullScreen = true }));

    // The first rule that holds decides, in spec §6.2's order.
    [Fact]
    public void Rules_HoldInTheSpecsOrder()
    {
        var settings = new AppSettings { AutoSelfUpdate = false, AutoInstallWaitDays = 7, InstallWindowEnabled = true, InstallWindowFrom = 22, InstallWindowTo = 6 };
        var release = Release with { PublishedAt = Now.AddDays(-1) };
        var system = new SystemState(FullScreen: true, Metered: true, BatterySaver: true, Offline: true);
        var book = Attempted("0.2.0", TimeSpan.FromHours(1));
        AutoBlock Block() => SelfUpdateRules.Check(release, Running, book, settings, system, Now, TimeZoneInfo.Utc);

        Assert.Equal(AutoBlock.AutoOff, Block());
        settings = settings with { AutoSelfUpdate = true };
        Assert.Equal(AutoBlock.NeedsAdmin, Block());
        settings = settings with { SilentMode = true };
        Assert.Equal(AutoBlock.TooNew, Block());
        settings = settings with { AutoInstallWaitDays = 0 };
        Assert.Equal(AutoBlock.OutsideWindow, Block());
        settings = settings with { InstallWindowEnabled = false };
        Assert.Equal(AutoBlock.Offline, Block());
        system = system with { Offline = false };
        Assert.Equal(AutoBlock.Metered, Block());
        system = system with { Metered = false };
        Assert.Equal(AutoBlock.BatterySaver, Block());
        system = system with { BatterySaver = false };
        Assert.Equal(AutoBlock.RecentlyAttempted, Block());
        book = Attempted("0.2.0", TimeSpan.FromHours(13));
        Assert.Equal(AutoBlock.FullScreen, Block());
        system = system with { FullScreen = false };
        Assert.Equal(AutoBlock.None, Block());
    }
}
