using TinyTracker.Core.Versions;
using Xunit;

namespace TinyTracker.Core.Tests.Versions;

public class PackageVersionTests
{
    [Theory]
    [InlineData("1.2.3", "1.2.10")]
    [InlineData("1.9", "1.10")]
    [InlineData("1", "1.0.1")]
    [InlineData("8.9.7", "8.9.8.1")]
    [InlineData("1.2b", "1.2")]
    [InlineData("1.2-beta", "1.2-rc")]
    [InlineData("Unknown", "0.1")]
    public void Orders_LikeWinget(string lower, string higher)
    {
        Assert.True(PackageVersion.Parse(lower).CompareTo(PackageVersion.Parse(higher)) < 0);
        Assert.True(PackageVersion.Parse(higher).CompareTo(PackageVersion.Parse(lower)) > 0);
    }

    [Theory]
    [InlineData("1.0", "1.0.0")]
    [InlineData("1", "1.0")]
    [InlineData(" 1.2 ", "1.2")]
    [InlineData("1.0-Beta", "1.0-beta")]
    [InlineData("Unknown", "")]
    [InlineData(null, "unknown")]
    public void Same_IgnoresTrailingZerosCaseAndSpaces(string? a, string b)
    {
        Assert.True(PackageVersion.Same(a, b));
        Assert.Equal(PackageVersion.Parse(a).GetHashCode(), PackageVersion.Parse(b).GetHashCode());
    }

    [Theory]
    [InlineData("1.2", "1.2.1")]
    [InlineData("0", "Unknown")]
    [InlineData("2.0", "20")]
    public void Same_IsFalseForDifferentVersions(string a, string b) => Assert.False(PackageVersion.Same(a, b));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Unknown")]
    [InlineData(" unknown ")]
    public void MissingOrUnknown_IsUnknown(string? text) => Assert.True(PackageVersion.Parse(text).IsUnknown);

    [Fact]
    public void RealVersion_IsKnown() => Assert.False(PackageVersion.Parse("2025.1.3").IsUnknown);

    [Fact]
    public void HugeNumber_DoesNotThrow()
    {
        var huge = PackageVersion.Parse("99999999999999999999999.1");
        Assert.True(PackageVersion.Same(huge.Text, "99999999999999999999999.1"));
        Assert.False(PackageVersion.Same(huge.Text, "1.1"));
    }

    [Fact]
    public void Text_IsTrimmedOriginal() => Assert.Equal("8.9.7", PackageVersion.Parse(" 8.9.7 ").Text);

    // An upgrade must go up, from a version that's known.
    [Theory]
    [InlineData("131.0", "130.0.1", true)]
    [InlineData("3.0.21", "3.0.20.0", true)]
    [InlineData("3.0.20", "3.0.20.0", false)]
    [InlineData("130.0", "131.0", false)]
    [InlineData("1.2b", "1.2", false)]
    [InlineData("1.0", "Unknown", false)]
    [InlineData("1.0", "", false)]
    [InlineData("Unknown", "1.0", false)]
    public void IsNewer_OnlyWhenBothAreKnown(string candidate, string than, bool newer) => Assert.Equal(newer, PackageVersion.IsNewer(candidate, than));
}
