using Microsoft.Extensions.Time.Testing;
using TinyTracker.Core.Installing;
using Xunit;

namespace TinyTracker.Core.Tests.Installing;

public class SpeedMeterTests
{
    private const ulong MB = 1024 * 1024;

    private readonly FakeTimeProvider _time = new();
    private readonly SpeedMeter _meter;

    public SpeedMeterTests() => _meter = new SpeedMeter(_time);

    private void Every(TimeSpan step, params ulong[] bytes)
    {
        foreach (var value in bytes)
        {
            _time.Advance(step);
            _meter.Add(value);
        }
    }

    [Fact]
    public void NoSamples_IsZero() => Assert.Equal(0, _meter.BytesPerSecond);

    [Fact]
    public void OneSample_IsZero()
    {
        _meter.Add(5 * MB);
        Assert.Equal(0, _meter.BytesPerSecond);
    }

    [Fact]
    public void FirstSeconds_UseTheTimeSoFar()
    {
        _meter.Add(0);
        Every(TimeSpan.FromMilliseconds(500), 5 * MB);
        Assert.Equal(10 * MB, _meter.BytesPerSecond, 3);
    }

    [Fact]
    public void SteadyDownload_AveragesTheLastThreeSeconds()
    {
        _meter.Add(0);
        Every(TimeSpan.FromSeconds(1), 10 * MB, 20 * MB, 30 * MB);
        Assert.Equal(10 * MB, _meter.BytesPerSecond, 3);
        Every(TimeSpan.FromSeconds(1), 50 * MB, 70 * MB, 90 * MB);
        Assert.Equal(20 * MB, _meter.BytesPerSecond, 3);
    }

    [Fact]
    public void StoppedDownload_FallsToZero()
    {
        _meter.Add(0);
        Every(TimeSpan.FromSeconds(1), 10 * MB, 20 * MB, 30 * MB);
        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(10 * MB / 3.0, _meter.BytesPerSecond, 3);
        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(0, _meter.BytesPerSecond);
    }

    [Fact]
    public void FewerBytes_StartsOver()
    {
        _meter.Add(0);
        Every(TimeSpan.FromSeconds(1), 50 * MB, 2 * MB);
        Assert.Equal(0, _meter.BytesPerSecond);
        Every(TimeSpan.FromSeconds(1), 6 * MB);
        Assert.Equal(4 * MB, _meter.BytesPerSecond, 3);
    }
}
