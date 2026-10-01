using System.Globalization;
using TinyTracker.Core.Settings;
using TinyTracker.Core.Installing;
using TinyTracker.Core.Inventory;
using TinyTracker.Presentation.Text;
using Xunit;

namespace TinyTracker.Presentation.Tests.Text;

public class WordsTests
{
    private const ulong KB = 1024, MB = KB * 1024, GB = MB * 1024;
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 8, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0UL, "0 KB")]
    [InlineData(512UL, "0.5 KB")]
    [InlineData(850 * KB, "850 KB")]
    [InlineData(3 * MB / 2, "1.5 MB")]
    [InlineData(180 * MB, "180 MB")]
    [InlineData(6 * GB / 5, "1.2 GB")]
    public void Size_UsesTheLargestFittingUnit(ulong bytes, string text) => Assert.Equal(Fixtures.Nb(text), Words.Size(bytes));

    [Theory]
    [InlineData(180 * MB, 400 * MB, "180 of 400 MB")]
    [InlineData(GB / 2, 3 * GB / 2, "0.5 of 1.5 GB")]
    [InlineData(180 * MB, 0UL, "180 MB")]
    public void Downloaded_ShowsBothInTheTotalsUnit(ulong done, ulong total, string text) => Assert.Equal(Fixtures.Nb(text), Words.Downloaded(done, total));

    // In the rows' units, where a megabyte is 1024 kilobytes.
    [Theory]
    [InlineData(17500, "≈ 17.1 MB/s")]
    [InlineData(500, "≈ 0.49 MB/s")]
    [InlineData(100, "≈ 0.1 MB/s")]
    [InlineData(2048, "≈ 2 MB/s")]
    [InlineData(1_000_000, "≈ 977 MB/s")]
    public void SpeedLimit_ShowsAsMegabytes(int kbps, string text) => Assert.Equal(Fixtures.Nb(text), Words.MegabytesPerSecond(kbps));

    [Fact]
    public void Speed_IsPerSecond() => Assert.Equal([Fixtures.Nb("20 MB/s"), Fixtures.Nb("850 KB/s"), Fixtures.Nb("0 KB/s")], [Words.Speed(20 * MB), Words.Speed(850 * KB), Words.Speed(-1)]);

    [Theory]
    [InlineData(30, "just now")]
    [InlineData(-300, "just now")]
    [InlineData(120, "2 min ago")]
    [InlineData(59 * 60, "59 min ago")]
    [InlineData(3 * 3600 + 59 * 60, "3 h ago")]
    [InlineData(26 * 3600, "1 day ago")]
    [InlineData(3 * 86400, "3 days ago")]
    public void Ago_RoundsDown(int seconds, string text) => Assert.Equal(text, Words.Ago(Now - TimeSpan.FromSeconds(seconds), Now));

    [Theory]
    [InlineData(0, "Today")]
    [InlineData(-2, "Today")]
    [InlineData(1, "Yesterday")]
    [InlineData(5, "5 days ago")]
    [InlineData(30, "30 days ago")]
    [InlineData(45, "Aug 11, 2026")]
    public void Released_CountsDays(int daysAgo, string text) => Assert.Equal(text, Words.Released(DateOnly.FromDateTime(Now.UtcDateTime).AddDays(-daysAgo), Now));

    [Theory]
    [InlineData(-60, "less than a minute")]
    [InlineData(59, "less than a minute")]
    [InlineData(60, "1 min")]
    [InlineData(42 * 60 - 30, "42 min")]
    [InlineData(5 * 3600 + 57 * 60 + 30, "5 h 58 min")]
    [InlineData(6 * 3600, "6 h")]
    public void Until_RoundsUpToTheMinute(int seconds, string text) => Assert.Equal(text, Words.Until(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Reason_WordsAKnownName_AndFallsBackForOthers() =>
        Assert.Equal(["Not enough disk space", "The update failed", ""], [Words.Reason("DiskFull"), Words.Reason("SomethingNew"), Words.Reason(null)]);

    // History keeps these names, so each needs words.
    public static TheoryData<string> ReasonNames()
    {
        var names = new TheoryData<string>("Phantom");
        foreach (var failure in Enum.GetValues<UpgradeFailure>().Where(f => f != UpgradeFailure.None)) names.Add(failure.ToString());
        foreach (var result in Enum.GetValues<UpgradeResult>().Where(r => r is not (UpgradeResult.Updated or UpgradeResult.Cancelled or UpgradeResult.Failed))) names.Add(result.ToString());
        return names;
    }

    [Theory]
    [MemberData(nameof(ReasonNames))]
    public void EveryReason_HasItsOwnWords(string name) => Assert.NotNull(Strings.ResourceManager.GetString("Reason_" + name, Strings.Culture));

    [Theory]
    [InlineData(UpdatedBy.Steam, "Updated by Steam")]
    [InlineData(UpdatedBy.MicrosoftStore, "Updated by Microsoft Store")]
    [InlineData(UpdatedBy.WindowsUpdate, "Updated by Windows Update")]
    [InlineData(UpdatedBy.DriverTool, "Updated by its driver tool")]
    [InlineData(UpdatedBy.ItSelf, "Updates itself")]
    [InlineData(UpdatedBy.NoExactMatch, "No exact match in winget")]
    [InlineData(UpdatedBy.Unknown, "Not in winget")]
    public void Elsewhere_NamesWhatUpdatesTheApp(UpdatedBy by, string text) => Assert.Equal(text, Words.Elsewhere(by));

    [Fact]
    public void Counts_UseOneAndMany() =>
        Assert.Equal(
            ["1 update ready", "3 updates ready", "1 app is up to date", "9 apps are up to date", "1 app tracked", "4 apps tracked", "No apps tracked"],
            [Words.UpdatesReady(1), Words.UpdatesReady(3), Words.UpToDate(1), Words.UpToDate(9), Words.AppsTracked(1), Words.AppsTracked(4), Words.AppsTracked(0)]);

    // A restore's notice (spec §4.5): up to five names, then how many more.
    [Fact]
    public void RestoreWords_CountAndName()
    {
        Assert.Equal(["No apps were added.", "Added 1 app.", "Added 12 apps."], [Words.AppsAdded(0), Words.AppsAdded(1), Words.AppsAdded(12)]);
        Assert.Equal("Not installed here: A, B.", Words.NotInstalledHere(["A", "B"]));
        Assert.Equal("Not installed here: A, B, C, D, E and 2 more.", Words.NotInstalledHere(["A", "B", "C", "D", "E", "F", "G"]));
    }

    [Fact]
    public void Hours_AndWaitDays_NameEachChoice() =>
        Assert.Equal(
            ["1 hour", "3 hours", "24 hours", "Off", "1 day", "7 days"],
            [Words.Hours(1), Words.Hours(3), Words.Hours(24), Words.WaitDays(0), Words.WaitDays(1), Words.WaitDays(7)]);

    [Theory]
    [InlineData("2026-09-25", "2026-09-25", "Today")]
    [InlineData("2026-09-26", "2026-09-25", "Today")]
    [InlineData("2026-09-24", "2026-09-25", "Yesterday")]
    [InlineData("2026-09-22", "2026-09-25", "Sep 22")]
    [InlineData("2026-12-31", "2027-01-01", "Yesterday")]
    [InlineData("2026-12-31", "2027-01-02", "Dec 31, 2026")]
    public void Day_NamesTodayYesterdayOrTheDate(string day, string today, string text) =>
        Assert.Equal(text, Words.Day(DateOnly.Parse(day, CultureInfo.InvariantCulture), DateOnly.Parse(today, CultureInfo.InvariantCulture)));

    [Fact]
    public void Names_ListUpToThree_ThenCountTheRest() =>
        Assert.Equal(
            ["", "A", "A and B", "A, B and C", "A, B, C and 1 more"],
            [Words.Names([]), Words.Names(["A"]), Words.Names(["A", "B"]), Words.Names(["A", "B", "C"]), Words.Names(["A", "B", "C", "D"])]);

    [Fact]
    public void Shortcut_NamesTheModifiersInOrder_ThenTheKey() =>
        Assert.Equal(
            ["Win + Shift + U", "Ctrl + Alt + U", "Win + Ctrl + Alt + Shift + F12"],
            [Words.Shortcut(Shortcut.Default, "U"), Words.Shortcut(new Shortcut(ShortcutModifiers.Control | ShortcutModifiers.Alt, 0x55), "U"),
                Words.Shortcut(new Shortcut(ShortcutModifiers.Windows | ShortcutModifiers.Shift | ShortcutModifiers.Alt | ShortcutModifiers.Control, 0x7B), "F12")]);

    [Fact]
    public void TimeOfDay_FollowsTheCultureGiven()
    {
        var at = new DateTimeOffset(2026, 9, 25, 14, 32, 0, TimeSpan.FromHours(4));
        var twelveHour = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        twelveHour.DateTimeFormat.ShortTimePattern = "h:mm tt";
        twelveHour.DateTimeFormat.PMDesignator = "PM";
        Assert.Equal(["14:32", "2:32 PM"], [Words.TimeOfDay(at, CultureInfo.InvariantCulture), Words.TimeOfDay(at, twelveHour)]);
    }

    // The install window's hours, each kept on one line.
    [Fact]
    public void Hour_FollowsTheCultureGiven()
    {
        var twelveHour = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        twelveHour.DateTimeFormat.ShortTimePattern = "h:mm tt";
        twelveHour.DateTimeFormat.PMDesignator = "PM";
        Assert.Equal(["22:00", "10:00 PM".Replace(' ', (char)0xA0)], [Words.Hour(22, CultureInfo.InvariantCulture), Words.Hour(22, twelveHour)]);
    }
}
