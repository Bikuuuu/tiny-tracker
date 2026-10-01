using TinyTracker.WinGet.Elevation;
using Xunit;

namespace TinyTracker.WinGet.Tests.Elevation;

// The helper's idle wait counts time awake only (spec §5.1): Windows' unbiased interrupt time, in 100 ns ticks.
public sealed class AwakeTimeTests
{
    [Fact]
    public async Task Timestamps_FollowTimeAwake()
    {
        var clock = AwakeTime.Instance;
        Assert.Equal(TimeSpan.TicksPerSecond, clock.TimestampFrequency);
        var start = clock.GetTimestamp();
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.InRange(clock.GetElapsedTime(start), TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(10));
    }
}
