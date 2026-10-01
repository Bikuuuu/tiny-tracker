using TinyTracker.Core.Installing;
using TinyTracker.Core.Settings;
using Xunit;

namespace TinyTracker.Core.Tests.Installing;

public sealed class SpeedLimitTests
{
    [Fact]
    public void NoLimit_ByDefault() => Assert.Equal((0, 0L), (new SpeedLimit().KBps, new SpeedLimit().BytesPerSecond));

    [Fact]
    public void Limit_CountsKilobytesOf1024Bytes() => Assert.Equal(17500 * 1024L, new SpeedLimit(17500).BytesPerSecond);

    [Theory]
    [InlineData(true, 17500, 17500)]
    [InlineData(false, 17500, 0)]
    [InlineData(true, 250, 250)]
    public void Of_TakesTheLimitOnlyWhileItsOn(bool enabled, int kbps, int expected) =>
        Assert.Equal(expected, SpeedLimit.Of(new AppSettings { SpeedLimitEnabled = enabled, SpeedLimitKBps = kbps }));

    [Fact]
    public void Set_SaysWhenTheLimitChanges()
    {
        var limit = new SpeedLimit(17500);
        var changes = new List<int>();
        limit.Changed += (_, _) => changes.Add(limit.KBps);
        limit.Set(17500);
        limit.Set(2000);
        limit.Set(0);
        limit.Set(0);
        Assert.Equal([2000, 0], changes);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(100, true)]
    [InlineData(1_000_000, true)]
    [InlineData(99, false)]
    [InlineData(1_000_001, false)]
    [InlineData(-1, false)]
    public void Allowed_IsNoneOrWhatTheSettingsBoxTakes(int kbps, bool allowed) => Assert.Equal(allowed, SpeedLimit.IsAllowed(kbps));

    [Fact]
    public void LimitOutsideTheRange_IsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpeedLimit(50));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpeedLimit().Set(2_000_000));
    }
}
