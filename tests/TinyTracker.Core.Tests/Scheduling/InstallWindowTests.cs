using TinyTracker.Core.Scheduling;
using TinyTracker.Core.Settings;
using Xunit;

namespace TinyTracker.Core.Tests.Scheduling;

// The hours Auto apps install in (spec §6.2): from the start hour up to the end hour, in local time, across midnight too.
public class InstallWindowTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static DateTimeOffset At(int hour, int minute = 0, int day = 28, int month = 9) => new(2026, month, day, hour, minute, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(22, 6, 22, true)]
    [InlineData(22, 6, 0, true)]
    [InlineData(22, 6, 5, true)]
    [InlineData(22, 6, 6, false)]
    [InlineData(22, 6, 21, false)]
    [InlineData(1, 5, 1, true)]
    [InlineData(1, 5, 4, true)]
    [InlineData(1, 5, 5, false)]
    [InlineData(1, 5, 0, false)]
    public void Contains_FromTheStartHourUpToTheEndHour(int from, int to, int hour, bool inside) =>
        Assert.Equal(inside, new InstallWindow(from, to).Contains(At(hour), Utc));

    // 22:00 to 06:00 ends at 05:59.
    [Fact]
    public void LastMinute_IsInside() => Assert.True(new InstallWindow(22, 6).Contains(At(5, 59), Utc));

    [Fact]
    public void Contains_ReadsTheLocalTime() => Assert.True(new InstallWindow(22, 6).Contains(At(18, 30), Fixed(4)));

    [Fact]
    public void Off_HasNoWindow() => Assert.Null(InstallWindow.Of(new AppSettings()));

    [Fact]
    public void On_TakesTheHours() =>
        Assert.Equal(new InstallWindow(1, 5), InstallWindow.Of(new AppSettings { InstallWindowEnabled = true, InstallWindowFrom = 1, InstallWindowTo = 5 }));

    [Fact]
    public void NextStart_IsLaterToday() => Assert.Equal(At(22), new InstallWindow(22, 6).NextStart(At(8), Utc));

    [Fact]
    public void NextStart_AfterTheWindow_IsTomorrow() => Assert.Equal(At(1, day: 29), new InstallWindow(1, 5).NextStart(At(7), Utc));

    [Fact]
    public void NextStart_ReadsTheLocalTime() => Assert.Equal(At(16, 15), new InstallWindow(22, 6).NextStart(At(10), Fixed(5.75)));

    // Europe's clocks go from 02:00 to 03:00 on 29 March 2026, so a window from 02:00 opens at 03:00, which is 01:00 UTC.
    [Fact]
    public void NextStart_OnTheDayClocksGoForward_IsWhenTheWindowFirstShows() =>
        Assert.Equal(At(1, day: 29, month: 3), new InstallWindow(2, 5).NextStart(At(12, day: 28, month: 3), Europe()));

    // They go back from 03:00 to 02:00 on 25 October 2026, so 02:00 comes twice; the first is 00:00 UTC.
    [Fact]
    public void NextStart_OnTheDayClocksGoBack_IsTheFirstTime() =>
        Assert.Equal(At(0, day: 25, month: 10), new InstallWindow(2, 5).NextStart(At(12, day: 24, month: 10), Europe()));

    private static TimeZoneInfo Fixed(double hours) => TimeZoneInfo.CreateCustomTimeZone($"Test {hours}", TimeSpan.FromHours(hours), "Test", "Test");

    // Central European time: +1, and +2 from the last Sunday of March to the last Sunday of October.
    private static TimeZoneInfo Europe()
    {
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 5, DayOfWeek.Sunday),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 5, DayOfWeek.Sunday));
        return TimeZoneInfo.CreateCustomTimeZone("Test Europe", TimeSpan.FromHours(1), "Test Europe", "Test Europe", "Test Europe Summer", [rule]);
    }
}
