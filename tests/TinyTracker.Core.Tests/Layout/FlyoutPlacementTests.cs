using TinyTracker.Core.Layout;
using Xunit;

namespace TinyTracker.Core.Tests.Layout;

public class FlyoutPlacementTests
{
    private static readonly PixelRect Primary = new(0, 0, 2560, 1392);

    [Fact]
    public void At100Percent_SitsBottomRightWith12pxMargins() =>
        Assert.Equal(new PixelRect(2168, 880, 380, 500), FlyoutPlacement.Compute(Primary, 96, 500));

    [Fact]
    public void At150Percent_ScalesSizeAndMargins() =>
        Assert.Equal(new PixelRect(1972, 624, 570, 750), FlyoutPlacement.Compute(Primary, 144, 500));

    [Fact]
    public void TallContent_IsClampedTo70PercentOfWorkArea() =>
        Assert.Equal(new PixelRect(2168, 406, 380, 974), FlyoutPlacement.Compute(Primary, 96, 2000));

    [Fact]
    public void SecondaryMonitorOnTheRight_UsesItsOwnWorkArea() =>
        Assert.Equal(new PixelRect(4088, 620, 380, 400), FlyoutPlacement.Compute(new PixelRect(2560, 0, 1920, 1032), 96, 400));

    [Fact]
    public void MonitorLeftOfPrimary_HandlesNegativeCoordinatesAt125Percent() =>
        Assert.Equal(new PixelRect(-490, 525, 475, 500), FlyoutPlacement.Compute(new PixelRect(-1920, 0, 1920, 1040), 120, 400));

    [Fact]
    public void Result_StaysInsideTheWorkArea()
    {
        var work = new PixelRect(-1920, 0, 1920, 1040);
        var rect = FlyoutPlacement.Compute(work, 120, 5000);
        Assert.True(rect.X >= work.X && rect.Y >= work.Y && rect.Right <= work.Right && rect.Bottom <= work.Bottom);
    }

    [Fact]
    public void Place_PutsAHeightInPixelsAboveTheTaskbar() =>
        Assert.Equal(new PixelRect(1972, 574, 570, 800), FlyoutPlacement.Place(Primary, 144, 800));

    [Fact]
    public void Place_KeepsTo70PercentOfTheWorkArea() =>
        Assert.Equal(new PixelRect(2168, 406, 380, 974), FlyoutPlacement.Place(Primary, 96, 2000));
}
