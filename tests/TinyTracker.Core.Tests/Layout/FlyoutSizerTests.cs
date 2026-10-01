using Microsoft.Extensions.Time.Testing;
using TinyTracker.Core.Layout;
using Xunit;

namespace TinyTracker.Core.Tests.Layout;

public sealed class FlyoutSizerTests : IDisposable
{
    private static readonly int WaitMs = (int)FlyoutSizer.ShrinkWait.TotalMilliseconds;

    private readonly FakeTimeProvider _time = new();
    private readonly List<int> _resized = [];
    private readonly Queue<Action> _posted = new();
    private readonly FlyoutSizer _sizer;

    public FlyoutSizerTests()
    {
        _sizer = new FlyoutSizer(_time, _posted.Enqueue, _resized.Add);
        _sizer.Reset(500);
    }

    public void Dispose() => _sizer.Dispose();

    // Moves the clock on, then runs what the timer posted, as the UI thread would.
    private void Wait(int ms)
    {
        _time.Advance(TimeSpan.FromMilliseconds(ms));
        while (_posted.TryDequeue(out var action)) action();
    }

    [Fact]
    public void Growing_ResizesAtOnce()
    {
        _sizer.Request(700, atOnce: false);
        Assert.Equal([700], _resized);
    }

    [Fact]
    public void AtOnce_ShrinksWithoutWaiting()
    {
        _sizer.Request(300, atOnce: true);
        Assert.Equal([300], _resized);
    }

    [Fact]
    public void Shrinking_WaitsUntilWhatLeavesIsGone()
    {
        _sizer.Request(300, atOnce: false);
        Wait(WaitMs - 1);
        Assert.Empty(_resized);
        Wait(1);
        Assert.Equal([300], _resized);
    }

    [Fact]
    public void AnotherShrink_StartsTheWaitAgain_WithTheNewHeight()
    {
        _sizer.Request(400, atOnce: false);
        Wait(WaitMs / 2);
        _sizer.Request(300, atOnce: false);
        Wait(WaitMs - 1);
        Assert.Empty(_resized);
        Wait(1);
        Assert.Equal([300], _resized);
    }

    [Fact]
    public void GrowingWhileAShrinkWaits_DropsTheShrink()
    {
        _sizer.Request(300, atOnce: false);
        Wait(WaitMs / 2);
        _sizer.Request(600, atOnce: false);
        Wait(WaitMs * 3);
        Assert.Equal([600], _resized);
    }

    [Fact]
    public void BackToTheSameHeightWhileAShrinkWaits_DropsTheShrink()
    {
        _sizer.Request(300, atOnce: false);
        _sizer.Request(500, atOnce: false);
        Wait(WaitMs * 3);
        Assert.Empty(_resized);
    }

    [Fact]
    public void AtOnceWhileAShrinkWaits_ResizesOnce_AtOnce()
    {
        _sizer.Request(400, atOnce: false);
        _sizer.Request(300, atOnce: true);
        Wait(WaitMs * 3);
        Assert.Equal([300], _resized);
    }

    [Fact]
    public void SameHeight_DoesNothing()
    {
        _sizer.Request(500, atOnce: false);
        _sizer.Request(500, atOnce: true);
        Assert.Empty(_resized);
    }

    [Fact]
    public void Reset_DropsAWaitingShrink()
    {
        _sizer.Request(300, atOnce: false);
        _sizer.Reset(700);
        Wait(WaitMs * 3);
        Assert.Empty(_resized);
    }

    // The timer posts to the UI thread, so a grow can get there first.
    [Fact]
    public void WaitThatEndsJustAsTheFlyoutGrows_IsDropped()
    {
        _sizer.Request(300, atOnce: false);
        _time.Advance(FlyoutSizer.ShrinkWait);
        _sizer.Request(600, atOnce: false);
        Wait(0);
        Assert.Equal([600], _resized);
    }

    // Another shrink can get there first too, and its wait starts again.
    [Fact]
    public void ShrinkThatComesAsAWaitEnds_WaitsItsWholeTime()
    {
        _sizer.Request(300, atOnce: false);
        _time.Advance(FlyoutSizer.ShrinkWait);
        _sizer.Request(200, atOnce: false);
        Wait(0);
        Assert.Empty(_resized);
        Wait(WaitMs - 1);
        Assert.Empty(_resized);
        Wait(1);
        Assert.Equal([200], _resized);
    }

    [Fact]
    public void AfterAResize_TheNextRequestStartsFromThere()
    {
        _sizer.Request(700, atOnce: false);
        _sizer.Request(600, atOnce: false);
        Assert.Equal([700], _resized);
        Wait(WaitMs);
        Assert.Equal([700, 600], _resized);
    }
}
