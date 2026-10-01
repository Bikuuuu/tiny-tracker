using TinyTracker.Core.Launch;
using Xunit;

namespace TinyTracker.Core.Tests.Launch;

// The uninstaller waits for the --cleanup the shell started, which it can only see in the process list (spec §10).
public class CleanupWaitTests
{
    private TimeSpan _now;

    private CleanupWait.Outcome Wait(Func<TimeSpan, bool> runningAt) =>
        CleanupWait.For(() => runningAt(_now), () => _now, () => _now += CleanupWait.Every);

    private static TimeSpan Seconds(double s) => TimeSpan.FromSeconds(s);

    [Fact]
    public void CleanupThatRunsAndEnds_IsWaitedFor()
    {
        Assert.Equal(CleanupWait.Outcome.Ended, Wait(t => t >= Seconds(0.5) && t < Seconds(2)));
        Assert.InRange(_now, Seconds(2), Seconds(2) + CleanupWait.Every);
    }

    // A quick one may show in only one look.
    [Fact]
    public void CleanupSeenOnce_CountsAsEnded()
    {
        Assert.Equal(CleanupWait.Outcome.Ended, Wait(t => t == CleanupWait.Every * 4));
        Assert.Equal(CleanupWait.Every * 5, _now);
    }

    [Fact]
    public void CleanupThatNeverShows_IsGivenItsTenSeconds()
    {
        Assert.Equal(CleanupWait.Outcome.NeverSeen, Wait(_ => false));
        Assert.InRange(_now, CleanupWait.Appears, CleanupWait.Appears + CleanupWait.Every);
    }

    // The uninstall goes on after a minute; files still held go at the next restart.
    [Fact]
    public void CleanupStillRunningAfterItsMinute_IsLeftToFinish()
    {
        Assert.Equal(CleanupWait.Outcome.StillRunning, Wait(t => t >= Seconds(1)));
        Assert.InRange(_now, CleanupWait.Appears + CleanupWait.Runs, CleanupWait.Appears + CleanupWait.Runs + CleanupWait.Every);
    }
}
