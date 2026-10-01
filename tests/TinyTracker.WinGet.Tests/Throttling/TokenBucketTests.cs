using Microsoft.Extensions.Time.Testing;
using TinyTracker.WinGet.Throttling;
using Xunit;

namespace TinyTracker.WinGet.Tests.Throttling;

public class TokenBucketTests
{
    [Fact]
    public void Unlimited_NeverWaits() =>
        Assert.Equal(TimeSpan.Zero, new TokenBucket(new FakeTimeProvider()).Take(10_000_000));

    [Fact]
    public void StartsEmpty_FirstChunkWaitsForItsShare() =>
        Assert.Equal(TimeSpan.FromMilliseconds(500), new TokenBucket(new FakeTimeProvider()) { BytesPerSecond = 1000 }.Take(500));

    [Fact]
    public void SteadyRate_WaitsProportionally()
    {
        var time = new FakeTimeProvider();
        var bucket = new TokenBucket(time) { BytesPerSecond = 1000 };
        bucket.Take(500);
        time.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Equal(TimeSpan.FromMilliseconds(500), bucket.Take(500));
    }

    [Fact]
    public void LongIdle_AllowsAtMostOneSecondBurst()
    {
        var time = new FakeTimeProvider();
        var bucket = new TokenBucket(time) { BytesPerSecond = 1000 };
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(TimeSpan.Zero, bucket.Take(1000));
        Assert.Equal(TimeSpan.FromMilliseconds(500), bucket.Take(500));
    }

    [Fact]
    public void RaisingTheLimit_ShortensWaits()
    {
        var bucket = new TokenBucket(new FakeTimeProvider()) { BytesPerSecond = 1000 };
        bucket.BytesPerSecond = 4000;
        Assert.Equal(TimeSpan.FromMilliseconds(250), bucket.Take(1000));
    }

    [Fact]
    public void ZeroMeansUnlimited_EvenAfterBeingLimited()
    {
        var bucket = new TokenBucket(new FakeTimeProvider()) { BytesPerSecond = 1000 };
        bucket.Take(5000);
        bucket.BytesPerSecond = 0;
        Assert.Equal(TimeSpan.Zero, bucket.Take(5000));
    }

    [Fact]
    public void ChunkSize_IsAtMostATenthOfASecond()
    {
        Assert.Equal(16 * 1024, TokenBucket.ChunkSize(0));
        Assert.Equal(102, TokenBucket.ChunkSize(1024));
        Assert.Equal(1, TokenBucket.ChunkSize(5));
        Assert.Equal(16 * 1024, TokenBucket.ChunkSize(100_000_000));
    }

    [Fact]
    public void RaisingTheLimit_ShortensPendingDebt()
    {
        var bucket = new TokenBucket(new FakeTimeProvider()) { BytesPerSecond = 1000 };
        bucket.Take(1000);
        bucket.BytesPerSecond = 10_000;
        Assert.Equal(TimeSpan.FromMilliseconds(100), bucket.PendingDelay());
    }

    [Fact]
    public void SwitchingToUnlimited_ClearsPendingDebt()
    {
        var bucket = new TokenBucket(new FakeTimeProvider()) { BytesPerSecond = 1000 };
        bucket.Take(5000);
        bucket.BytesPerSecond = 0;
        Assert.Equal(TimeSpan.Zero, bucket.PendingDelay());
    }
}
