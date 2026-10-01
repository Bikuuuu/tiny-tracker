using Microsoft.Extensions.Time.Testing;
using TinyTracker.Core.Scheduling;
using Xunit;

namespace TinyTracker.Core.Tests.Scheduling;

public sealed class CheckSchedulerTests : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 25, 8, 0, 0, TimeSpan.Zero));
    private readonly List<CheckTicket> _tickets = [];
    private readonly List<CheckTrigger> _checks = [];
    private readonly CheckScheduler _scheduler;

    public CheckSchedulerTests()
    {
        _scheduler = new CheckScheduler(_time, Interval);
        _scheduler.CheckDue += (_, ticket) =>
        {
            _tickets.Add(ticket);
            _checks.Add(ticket.Trigger);
        };
    }

    public void Dispose() => _scheduler.Dispose();

    private void Advance(double minutes) => _time.Advance(TimeSpan.FromMinutes(minutes));

    private void Finish(bool succeeded) => _scheduler.Finished(_tickets[^1], succeeded);

    private void CompleteStartupCheck(bool succeeded = true)
    {
        Advance(1);
        Finish(succeeded);
    }

    [Fact]
    public void FirstCheck_RunsOneMinuteAfterStart()
    {
        _time.Advance(TimeSpan.FromSeconds(59));
        Assert.Empty(_checks);
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal([CheckTrigger.Startup], _checks);
    }

    [Fact]
    public void AfterACheck_TheNextWaitsTheInterval()
    {
        CompleteStartupCheck();
        Assert.Equal(_time.GetUtcNow() + Interval, _scheduler.NextCheck);
        _time.Advance(Interval - TimeSpan.FromSeconds(1));
        Assert.Equal([CheckTrigger.Startup], _checks);
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal([CheckTrigger.Startup, CheckTrigger.Scheduled], _checks);
    }

    [Fact]
    public void NextCheck_IsNullWhileAChecksRuns()
    {
        Advance(1);
        Assert.Null(_scheduler.NextCheck);
        Finish(true);
        Assert.NotNull(_scheduler.NextCheck);
    }

    [Fact]
    public void FailedChecks_RetryAfter1_5And15Minutes_ThenWaitTheInterval()
    {
        CompleteStartupCheck(succeeded: false);
        foreach (var minutes in new[] { 1, 5, 15 })
        {
            Assert.Equal(_time.GetUtcNow().AddMinutes(minutes), _scheduler.NextCheck);
            Advance(minutes);
            Finish(false);
        }
        Assert.Equal(_time.GetUtcNow() + Interval, _scheduler.NextCheck);
        Assert.Equal([CheckTrigger.Startup, CheckTrigger.Retry, CheckTrigger.Retry, CheckTrigger.Retry], _checks);
    }

    [Fact]
    public void Success_ResetsTheRetries()
    {
        CompleteStartupCheck(succeeded: false);
        Advance(1);
        Finish(true);
        _time.Advance(Interval);
        Finish(false);
        Assert.Equal(_time.GetUtcNow().AddMinutes(1), _scheduler.NextCheck);
    }

    [Fact]
    public void CheckNow_RunsAtOnceAndNotTwiceWhileRunning()
    {
        _scheduler.CheckNow();
        _scheduler.CheckNow();
        Advance(1);
        Assert.Equal([CheckTrigger.Manual], _checks);
    }

    [Fact]
    public void CheckNow_ReplacesTheStartupCheck()
    {
        _scheduler.CheckNow();
        Finish(true);
        Advance(1);
        Assert.Equal([CheckTrigger.Manual], _checks);
        Assert.Equal(_time.GetUtcNow().AddMinutes(-1) + Interval, _scheduler.NextCheck);
    }

    [Fact]
    public void CheckNow_IgnoresOfflineAndBatterySaver()
    {
        _scheduler.SetConditions(online: false, batterySaver: true);
        _scheduler.CheckNow();
        Assert.Equal([CheckTrigger.Manual], _checks);
    }

    [Fact]
    public void OpeningTheFlyout_BeforeAnyCheck_Checks()
    {
        _scheduler.FlyoutOpened();
        Assert.Equal([CheckTrigger.FlyoutOpened], _checks);
    }

    [Fact]
    public void OpeningTheFlyout_Within15Minutes_ShowsCachedData()
    {
        CompleteStartupCheck();
        Advance(15);
        _scheduler.FlyoutOpened();
        Assert.Equal([CheckTrigger.Startup], _checks);
    }

    [Fact]
    public void OpeningTheFlyout_After15Minutes_Refreshes()
    {
        CompleteStartupCheck();
        _time.Advance(TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(1));
        _scheduler.FlyoutOpened();
        Assert.Equal([CheckTrigger.Startup, CheckTrigger.FlyoutOpened], _checks);
    }

    [Fact]
    public void OpeningTheFlyout_Offline_SkipsQuietly()
    {
        _scheduler.SetConditions(online: false, batterySaver: false);
        _scheduler.FlyoutOpened();
        Assert.Empty(_checks);
    }

    [Fact]
    public void OpeningTheFlyout_InBatterySaver_StillRefreshes()
    {
        _scheduler.SetConditions(online: true, batterySaver: true);
        _scheduler.FlyoutOpened();
        Assert.Equal([CheckTrigger.FlyoutOpened], _checks);
    }

    [Fact]
    public void Offline_HoldsTheCheckUntilTheNetworkReturns()
    {
        _scheduler.SetConditions(online: false, batterySaver: false);
        Advance(10);
        Assert.Empty(_checks);
        _scheduler.SetConditions(online: true, batterySaver: false);
        Assert.Equal([CheckTrigger.Startup], _checks);
    }

    [Fact]
    public void BatterySaver_HoldsTheCheckUntilItTurnsOff()
    {
        _scheduler.SetConditions(online: true, batterySaver: true);
        Advance(10);
        Assert.Empty(_checks);
        _scheduler.SetConditions(online: true, batterySaver: false);
        Assert.Equal([CheckTrigger.Startup], _checks);
    }

    [Fact]
    public void Hold_SaysWhyTimedChecksWait()
    {
        Assert.Equal(CheckHold.None, _scheduler.Hold);
        _scheduler.SetConditions(online: true, batterySaver: true);
        Assert.Equal(CheckHold.BatterySaver, _scheduler.Hold);
        _scheduler.SetConditions(online: false, batterySaver: true);
        Assert.Equal(CheckHold.Offline, _scheduler.Hold);
        _scheduler.SetConditions(online: true, batterySaver: false);
        Assert.Equal(CheckHold.None, _scheduler.Hold);
    }

    [Fact]
    public void ConditionsClearingEarly_KeepTheSchedule()
    {
        _scheduler.SetConditions(online: false, batterySaver: false);
        Advance(0.5);
        _scheduler.SetConditions(online: true, batterySaver: false);
        Assert.Empty(_checks);
        Advance(0.5);
        Assert.Equal([CheckTrigger.Startup], _checks);
    }

    [Fact]
    public void CheckDueDuringSleep_RunsAMinuteAfterResume()
    {
        CompleteStartupCheck();
        _time.AdjustTime(_time.GetUtcNow() + TimeSpan.FromHours(8));
        _scheduler.Resumed();
        _time.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal([CheckTrigger.Startup], _checks);
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal([CheckTrigger.Startup, CheckTrigger.Resumed], _checks);
    }

    [Fact]
    public void ResumeBeforeTheDueTime_KeepsTheWallClockSchedule()
    {
        CompleteStartupCheck();
        var due = _scheduler.NextCheck;
        _time.AdjustTime(_time.GetUtcNow() + TimeSpan.FromHours(2));
        _scheduler.Resumed();
        Assert.Equal(due, _scheduler.NextCheck);
        _time.Advance(TimeSpan.FromHours(4));
        Assert.Equal([CheckTrigger.Startup, CheckTrigger.Scheduled], _checks);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(60)]
    public void ClockSetBack_KeepsTheSchedule(int days)
    {
        CompleteStartupCheck();
        _time.AdjustTime(_time.GetUtcNow() - TimeSpan.FromDays(days));
        _scheduler.Resumed();
        Assert.Equal(_time.GetUtcNow() + Interval, _scheduler.NextCheck);
        _time.Advance(Interval);
        Assert.Equal([CheckTrigger.Startup, CheckTrigger.Scheduled], _checks);
    }

    [Fact]
    public void OpeningTheFlyout_AfterClockSetBack_Refreshes()
    {
        CompleteStartupCheck();
        _time.AdjustTime(_time.GetUtcNow() - TimeSpan.FromDays(1));
        _scheduler.FlyoutOpened();
        Assert.Equal([CheckTrigger.Startup, CheckTrigger.FlyoutOpened], _checks);
    }

    [Fact]
    public void ShorterIntervalAlreadyPassed_ChecksNow()
    {
        CompleteStartupCheck();
        Advance(120);
        _scheduler.SetInterval(TimeSpan.FromHours(1));
        Assert.Equal([CheckTrigger.Startup, CheckTrigger.Scheduled], _checks);
    }

    [Fact]
    public void LongerInterval_CountsFromTheLastCheck()
    {
        CompleteStartupCheck();
        var last = _time.GetUtcNow();
        Advance(30);
        _scheduler.SetInterval(TimeSpan.FromHours(12));
        Assert.Equal(last + TimeSpan.FromHours(12), _scheduler.NextCheck);
    }

    [Fact]
    public void IntervalChange_LeavesARetryAlone()
    {
        CompleteStartupCheck(succeeded: false);
        var retry = _scheduler.NextCheck;
        _scheduler.SetInterval(TimeSpan.FromHours(1));
        Assert.Equal(retry, _scheduler.NextCheck);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(31 * 24)]
    public void IntervalOutOfRange_IsRejected(int hours)
    {
        var interval = TimeSpan.FromHours(hours);
        Assert.Throws<ArgumentOutOfRangeException>(() => new CheckScheduler(_time, interval));
        Assert.Throws<ArgumentOutOfRangeException>(() => _scheduler.SetInterval(interval));
    }

    [Fact]
    public void NextCheck_IsNullWhileHeldBack()
    {
        _scheduler.SetConditions(online: false, batterySaver: false);
        Assert.Null(_scheduler.NextCheck);
        _scheduler.SetConditions(online: true, batterySaver: true);
        Assert.Null(_scheduler.NextCheck);
        _scheduler.SetConditions(online: true, batterySaver: false);
        Assert.NotNull(_scheduler.NextCheck);
    }

    [Fact]
    public void HungCheck_TimesOutAndRetries()
    {
        Advance(1);
        Advance(10);
        Assert.NotNull(_scheduler.NextCheck);
        Advance(1);
        Assert.Equal([CheckTrigger.Startup, CheckTrigger.Retry], _checks);
    }

    [Fact]
    public void LateFinishFromATimedOutCheck_IsIgnored()
    {
        Advance(1);
        var hung = _tickets[^1];
        Advance(10);
        Advance(1);
        _scheduler.Finished(hung, true);
        Assert.Null(_scheduler.NextCheck);
        Finish(true);
        Assert.Equal(_time.GetUtcNow() + Interval, _scheduler.NextCheck);
    }

    [Fact]
    public void StaleTimerCallback_DoesNotFailAJustStartedCheck()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 25, 8, 0, 0, TimeSpan.Zero));
        using var scheduler = new CheckScheduler(time, Interval);
        var tickets = new List<CheckTicket>();
        scheduler.CheckDue += (_, ticket) => tickets.Add(ticket);
        scheduler.CheckNow();
        time.FireTimer();
        Assert.Null(scheduler.NextCheck);
        scheduler.Finished(tickets[^1], true);
        Assert.Equal(time.GetUtcNow() + Interval, scheduler.NextCheck);
    }

    [Fact]
    public void StrayFinished_IsIgnored()
    {
        _scheduler.Finished(new CheckTicket(42, CheckTrigger.Manual), false);
        Advance(1);
        Assert.Equal([CheckTrigger.Startup], _checks);
    }

    [Fact]
    public void Dispose_StopsEverything()
    {
        _scheduler.Dispose();
        Advance(10);
        _scheduler.CheckNow();
        _scheduler.SetConditions(online: true, batterySaver: false);
        Assert.Empty(_checks);
    }

    // Runs the timer callback by hand, like one already queued to the thread pool.
    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private TimerCallback? _callback;

        public override DateTimeOffset GetUtcNow() => now;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => now.UtcTicks;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _callback = callback;
            return new InertTimer();
        }

        public void FireTimer() => _callback!(null);

        private sealed class InertTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
