using Microsoft.Extensions.Time.Testing;
using TinyTracker.Core.Layout;
using Xunit;

namespace TinyTracker.Core.Tests.Layout;

public class FlyoutToggleTests
{
    [Fact]
    public void ClickWhileClosed_Opens() => Assert.Equal(ToggleAction.Open, new FlyoutToggle(new FakeTimeProvider()).OnTrayClick());

    [Fact]
    public void ClickWhileOpen_Closes()
    {
        var toggle = new FlyoutToggle(new FakeTimeProvider());
        toggle.Opened();
        Assert.Equal(ToggleAction.Close, toggle.OnTrayClick());
    }

    [Fact]
    public void ClickRightAfterFocusLossClosedIt_IsIgnored()
    {
        var time = new FakeTimeProvider();
        var toggle = new FlyoutToggle(time);
        toggle.Opened();
        toggle.Closed();
        time.Advance(TimeSpan.FromMilliseconds(120));
        Assert.Equal(ToggleAction.Ignore, toggle.OnTrayClick());
    }

    [Fact]
    public void ClickAfterTheGuard_OpensAgain()
    {
        var time = new FakeTimeProvider();
        var toggle = new FlyoutToggle(time);
        toggle.Opened();
        toggle.Closed();
        time.Advance(TimeSpan.FromMilliseconds(400));
        Assert.Equal(ToggleAction.Open, toggle.OnTrayClick());
    }

    [Fact]
    public void OpenedAndClosed_UpdateIsOpen()
    {
        var toggle = new FlyoutToggle(new FakeTimeProvider());
        toggle.Opened();
        Assert.True(toggle.IsOpen);
        toggle.Closed();
        Assert.False(toggle.IsOpen);
    }
}
