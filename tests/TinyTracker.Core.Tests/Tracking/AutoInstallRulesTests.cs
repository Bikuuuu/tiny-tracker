using TinyTracker.Core.Settings;
using TinyTracker.Core.Tracking;
using Xunit;

namespace TinyTracker.Core.Tests.Tracking;

public class AutoInstallRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 8, 0, 0, TimeSpan.Zero);
    private static readonly SystemState Calm = new(FullScreen: false, Metered: false, BatterySaver: false);
    private static readonly TrackedApp AutoApp = new()
    {
        Id = "Notepad++.Notepad++",
        Source = "winget",
        AutoChoice = true,
        Offer = new Offer { Version = "8.9.8", FirstSeen = Now },
    };
    // Installed for this user, so the update needs no admin rights.
    private static readonly PackageSnapshot UserPackage = new("Notepad++.Notepad++", "winget", "Notepad++", "8.9.7", "8.9.8", Scope: InstallScope.User);
    private static readonly TrackedApp SeenYesterday = AutoApp with { Offer = AutoApp.Offer! with { FirstSeen = Now.AddDays(-1) } };
    private static readonly PackageSnapshot SecurityFix = UserPackage with { ReleaseNotes = "- Fixed a security issue in the updater." };

    // From 22:00 to 06:00; now is 08:00.
    private static readonly AppSettings Night = new() { InstallWindowEnabled = true, InstallWindowFrom = 22, InstallWindowTo = 6 };

    private static AutoBlock Check(TrackedApp? app = null, AppStatus status = AppStatus.Available, AppSettings? settings = null, SystemState? system = null, PackageSnapshot? package = null,
        TimeZoneInfo? zone = null) =>
        AutoInstallRules.Check(new AppCheck(app ?? AutoApp, status, package ?? UserPackage, false), settings ?? new AppSettings(), system ?? Calm, Now, zone ?? TimeZoneInfo.Utc);

    [Fact]
    public void AllClear_Installs() => Assert.Equal(AutoBlock.None, Check());

    [Fact]
    public void AutoOff_IsHeldBack() => Assert.Equal(AutoBlock.AutoOff, Check(AutoApp with { AutoChoice = null }));

    // Update apps automatically (spec §6.2): an app without its own choice follows the switch.
    [Theory]
    [InlineData(false, AutoBlock.AutoOff)]
    [InlineData(true, AutoBlock.None)]
    public void AppWithoutItsOwnChoice_FollowsTheSwitch(bool switchOn, AutoBlock expected) =>
        Assert.Equal(expected, Check(AutoApp with { AutoChoice = null }, settings: new AppSettings { AutoUpdateApps = switchOn }));

    [Fact]
    public void OwnChoiceOff_HoldsTheAppBack_WhileTheSwitchIsOn() =>
        Assert.Equal(AutoBlock.AutoOff, Check(AutoApp with { AutoChoice = false }, settings: new AppSettings { AutoUpdateApps = true }));

    [Fact]
    public void OwnChoiceOn_Installs_WhileTheSwitchIsOff() =>
        Assert.Equal(AutoBlock.None, Check(AutoApp with { AutoChoice = true }, settings: new AppSettings { AutoUpdateApps = false }));

    [Theory]
    [InlineData(AppStatus.UpToDate)]
    [InlineData(AppStatus.Skipped)]
    [InlineData(AppStatus.Phantom)]
    [InlineData(AppStatus.VersionUnknown)]
    [InlineData(AppStatus.NotFound)]
    [InlineData(AppStatus.NotInCatalog)]
    public void OnlyAvailableRows_Install(AppStatus status) => Assert.Equal(AutoBlock.NotAvailable, Check(status: status));

    [Fact]
    public void WithinTheWait_IsTooNew()
    {
        var app = AutoApp with { Offer = AutoApp.Offer! with { FirstSeen = Now.AddDays(-2) } };
        Assert.Equal(AutoBlock.TooNew, Check(app, settings: new AppSettings { AutoInstallWaitDays = 3 }));
    }

    [Fact]
    public void AfterTheWait_Installs()
    {
        var app = AutoApp with { Offer = AutoApp.Offer! with { FirstSeen = Now.AddDays(-3) } };
        Assert.Equal(AutoBlock.None, Check(app, settings: new AppSettings { AutoInstallWaitDays = 3 }));
    }

    [Fact]
    public void ReleaseDate_WinsOverFirstSeen()
    {
        var app = AutoApp with { Offer = AutoApp.Offer! with { ReleaseDate = new DateOnly(2026, 9, 15) } };
        Assert.Equal(AutoBlock.None, Check(app, settings: new AppSettings { AutoInstallWaitDays = 7 }));
    }

    [Fact]
    public void RecentReleaseDate_IsTooNewEvenIfSeenLongAgo()
    {
        var app = AutoApp with { Offer = AutoApp.Offer! with { FirstSeen = Now.AddDays(-30), ReleaseDate = new DateOnly(2026, 9, 24) } };
        Assert.Equal(AutoBlock.TooNew, Check(app, settings: new AppSettings { AutoInstallWaitDays = 3 }));
    }

    [Fact]
    public void NoWait_IgnoresAFutureReleaseDate()
    {
        var app = AutoApp with { Offer = AutoApp.Offer! with { ReleaseDate = new DateOnly(2026, 12, 1) } };
        Assert.Equal(AutoBlock.None, Check(app));
    }

    [Fact]
    public void FullScreen_HoldsBackWhenPausingForGames() =>
        Assert.Equal(AutoBlock.FullScreen, Check(system: Calm with { FullScreen = true }));

    [Fact]
    public void FullScreen_IsIgnoredWhenNotPausingForGames() =>
        Assert.Equal(AutoBlock.None, Check(settings: new AppSettings { PauseDuringGames = false }, system: Calm with { FullScreen = true }));

    [Fact]
    public void FullScreen_IsSaidLast_SoAReasonThatLastsShows()
    {
        Assert.Equal(AutoBlock.Metered, Check(system: Calm with { FullScreen = true, Metered = true }));
        var app = AutoApp with { Offer = AutoApp.Offer! with { LastAutoAttempt = Now.AddHours(-1) } };
        Assert.Equal(AutoBlock.RecentlyAttempted, Check(app, system: Calm with { FullScreen = true }));
    }

    [Fact]
    public void Metered_IsHeldBack() => Assert.Equal(AutoBlock.Metered, Check(system: Calm with { Metered = true }));

    [Fact]
    public void BatterySaver_IsHeldBack() => Assert.Equal(AutoBlock.BatterySaver, Check(system: Calm with { BatterySaver = true }));

    [Fact]
    public void Offline_IsHeldBack_OnceTheWaitIsOver()
    {
        var waiting = AutoApp with { Offer = AutoApp.Offer! with { FirstSeen = Now.AddDays(-2) } };
        Assert.Equal(AutoBlock.Offline, Check(system: Calm with { Offline = true }));
        Assert.Equal(AutoBlock.TooNew, Check(waiting, settings: new AppSettings { AutoInstallWaitDays = 3 }, system: Calm with { Offline = true }));
    }

    [Fact]
    public void AttemptWithin12Hours_IsHeldBack()
    {
        var app = AutoApp with { Offer = AutoApp.Offer! with { LastAutoAttempt = Now.AddHours(-11) } };
        Assert.Equal(AutoBlock.RecentlyAttempted, Check(app));
    }

    [Fact]
    public void AttemptInTheFuture_IsIgnored()
    {
        var app = AutoApp with { Offer = AutoApp.Offer! with { LastAutoAttempt = Now.AddDays(1) } };
        Assert.Equal(AutoBlock.None, Check(app));
    }

    [Fact]
    public void FutureReleaseDate_FallsBackToFirstSeen()
    {
        var app = AutoApp with { Offer = AutoApp.Offer! with { FirstSeen = Now.AddDays(-5), ReleaseDate = new DateOnly(2026, 10, 5) } };
        Assert.Equal(AutoBlock.None, Check(app, settings: new AppSettings { AutoInstallWaitDays = 3 }));
    }

    [Fact]
    public void UpdateThatAsksForAdmin_IsHeldBack() =>
        Assert.Equal(AutoBlock.NeedsAdmin, Check(package: UserPackage with { Scope = InstallScope.Machine }));

    [Fact]
    public void UpdateThatAsksForAdmin_InstallsInSilentMode() =>
        Assert.Equal(AutoBlock.None, Check(settings: new AppSettings { SilentMode = true }, package: UserPackage with { Scope = InstallScope.Machine }));

    [Fact]
    public void NeedingAdmin_IsSaidBeforeTheWait()
    {
        var app = AutoApp with { Offer = AutoApp.Offer! with { FirstSeen = Now.AddDays(-2) } };
        var package = UserPackage with { Elevation = InstallerElevation.ElevatesSelf };
        Assert.Equal(AutoBlock.NeedsAdmin, Check(app, settings: new AppSettings { AutoInstallWaitDays = 3 }, package: package));
    }

    [Theory]
    [InlineData(2.0, 3, 1)]
    [InlineData(0.5, 3, 3)]
    [InlineData(3.0, 3, 0)]
    [InlineData(0.0, 0, 0)]
    public void DaysLeft_CountsWholeDays_RoundedUp(double seenDaysAgo, int wait, int left)
    {
        var offer = AutoApp.Offer! with { FirstSeen = Now.AddDays(-seenDaysAgo) };
        Assert.Equal(left, AutoInstallRules.DaysLeft(offer, new AppSettings { AutoInstallWaitDays = wait }, Now));
    }

    // Tiny Tracker's own update waits from GitHub's release date.
    [Theory]
    [InlineData(2.0, 3, 1)]
    [InlineData(0.5, 3, 3)]
    [InlineData(3.0, 3, 0)]
    [InlineData(-2.0, 3, 3)]
    [InlineData(1.0, 0, 0)]
    public void DaysLeft_FromADate_CountsWholeDays_NeverMoreThanTheWait(double releasedDaysAgo, int wait, int left) =>
        Assert.Equal(left, AutoInstallRules.DaysLeft(Now.AddDays(-releasedDaysAgo), new AppSettings { AutoInstallWaitDays = wait }, Now));

    [Fact]
    public void NoWait_IgnoresAFirstSeenAfterNow()
    {
        var app = AutoApp with { Offer = AutoApp.Offer! with { FirstSeen = Now.AddHours(5) } };
        Assert.Equal(AutoBlock.None, Check(app));
    }

    [Fact]
    public void DaysLeft_NeverExceedsTheWait() =>
        Assert.Equal(3, AutoInstallRules.DaysLeft(AutoApp.Offer! with { FirstSeen = Now.AddDays(2) }, new AppSettings { AutoInstallWaitDays = 3 }, Now));

    [Fact]
    public void Attempt12HoursAgo_TriesAgain()
    {
        var app = AutoApp with { Offer = AutoApp.Offer! with { LastAutoAttempt = Now.AddHours(-12) } };
        Assert.Equal(AutoBlock.None, Check(app));
    }

    // Security fixes first: a security fix skips the wait, and only the wait (spec §6.2).
    [Fact]
    public void SecurityFix_SkipsTheWait() =>
        Assert.Equal(AutoBlock.None, Check(SeenYesterday, settings: new AppSettings { AutoInstallWaitDays = 7 }, package: SecurityFix));

    [Fact]
    public void SecurityFix_WaitsWithTheSwitchOff() =>
        Assert.Equal(AutoBlock.TooNew, Check(SeenYesterday, settings: new AppSettings { AutoInstallWaitDays = 7, SecurityFirst = false }, package: SecurityFix));

    [Fact]
    public void SecurityFix_KeepsTheOtherRules()
    {
        var settings = new AppSettings { AutoInstallWaitDays = 7 };
        Assert.Equal(AutoBlock.Metered, Check(SeenYesterday, settings: settings, system: Calm with { Metered = true }, package: SecurityFix));
        Assert.Equal(AutoBlock.NeedsAdmin, Check(SeenYesterday, settings: settings, package: SecurityFix with { Scope = InstallScope.Machine }));
    }

    [Fact]
    public void OutsideTheWindow_Waits() => Assert.Equal(AutoBlock.OutsideWindow, Check(settings: Night));

    [Fact]
    public void InsideTheWindow_Installs() => Assert.Equal(AutoBlock.None, Check(settings: Night with { InstallWindowFrom = 7, InstallWindowTo = 9 }));

    [Fact]
    public void Window_ReadsTheLocalTime() =>
        Assert.Equal(AutoBlock.None, Check(settings: Night, zone: TimeZoneInfo.CreateCustomTimeZone("Test 14", TimeSpan.FromHours(14), "Test", "Test")));

    [Fact]
    public void Window_IsSaidAfterTheWait_AndBeforeTheNetwork()
    {
        Assert.Equal(AutoBlock.TooNew, Check(SeenYesterday, settings: Night with { AutoInstallWaitDays = 3 }, system: Calm with { Offline = true }));
        Assert.Equal(AutoBlock.OutsideWindow, Check(settings: Night, system: Calm with { Offline = true }));
    }

    [Fact]
    public void SecurityFix_WaitsForTheWindow() =>
        Assert.Equal(AutoBlock.OutsideWindow, Check(SeenYesterday, settings: Night with { AutoInstallWaitDays = 7 }, package: SecurityFix));
}
