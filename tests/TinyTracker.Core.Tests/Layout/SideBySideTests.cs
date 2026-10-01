using TinyTracker.Core.Layout;
using Xunit;

namespace TinyTracker.Core.Tests.Layout;

public class SideBySideTests
{
    [Fact]
    public void TwoPartsThatFit_StayBesideEachOther() => Assert.True(SideBySide.Fits(318, 120, 150, 8));

    [Fact]
    public void TwoPartsTooWide_GoOneUnderTheOther() => Assert.False(SideBySide.Fits(318, 170, 150, 8));

    [Fact]
    public void TextThatWraps_NeedsOnlyItsShareBesideTheControl() => Assert.True(SideBySide.Fits(318, 600, 150, 12, firstShare: 0.4));

    [Fact]
    public void ControlThatLeavesTheTextLessThanItsShare_GoesUnderIt() => Assert.False(SideBySide.Fits(318, 600, 190, 12, firstShare: 0.4));

    [Fact]
    public void ShortText_NeedsOnlyItsOwnWidth() => Assert.True(SideBySide.Fits(318, 40, 250, 12, firstShare: 0.4));

    // Its share grows with the text, so at twice the size a row's buttons go under its text, as at 100% they didn't.
    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public void LargerText_NeedsALargerShare(double textScale, bool fits) =>
        Assert.Equal(fits, SideBySide.Fits(278, 600, 110, 12, firstShare: 0.35, textScale: textScale));

    [Fact]
    public void ControlWiderThanTheSpace_GoesUnder() => Assert.False(SideBySide.Fits(318, 10, 400, 8));

    // Layout rounds to pixels, so a hair over still fits.
    [Theory]
    [InlineData(142, true)]
    [InlineData(142.4, true)]
    [InlineData(142.6, false)]
    public void ExactFit_AllowsForRounding(double second, bool fits) => Assert.Equal(fits, SideBySide.Fits(300, 150, second, 8));
}
