using TinyTracker.Core.SelfUpdate;
using Xunit;

namespace TinyTracker.Core.Tests.SelfUpdate;

public class SelfVersionTests
{
    [Theory]
    [InlineData("0.1.0", 0, 1, 0)]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("10.20.300", 10, 20, 300)]
    public void PlainNumbers_Parse(string text, int major, int minor, int patch) =>
        Assert.Equal(new SelfVersion(major, minor, patch), SelfVersion.Parse(text));

    // The helper takes a version from the pipe, so nothing else may get through, and nothing may throw.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("v1.2.3")]
    [InlineData("01.2.3")]
    [InlineData("1.02.3")]
    [InlineData("1.2.3-beta")]
    [InlineData("1.2.3+build")]
    [InlineData(" 1.2.3")]
    [InlineData("1.2.3 ")]
    [InlineData("1.2.3\n")]
    [InlineData("1..3")]
    [InlineData("1.2.x")]
    [InlineData("１.2.3")]
    [InlineData("99999999999.0.0")]
    public void AnythingElse_IsNoVersion(string? text) => Assert.Null(SelfVersion.Parse(text));

    [Fact]
    public void Tag_IsTheVersionWithAV()
    {
        Assert.Equal(new SelfVersion(0, 2, 0), SelfVersion.OfTag("v0.2.0"));
        Assert.Equal("v0.2.0", new SelfVersion(0, 2, 0).Tag);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("v")]
    [InlineData("0.2.0")]
    [InlineData("V0.2.0")]
    [InlineData("vv0.2.0")]
    [InlineData("v0.2")]
    public void OtherTags_AreNoVersion(string? tag) => Assert.Null(SelfVersion.OfTag(tag));

    [Theory]
    [InlineData("0.1.0", "0.2.0")]
    [InlineData("0.9.9", "1.0.0")]
    [InlineData("1.9.0", "1.10.0")]
    [InlineData("1.0.9", "1.0.10")]
    public void Later_IsNewer(string older, string newer)
    {
        Assert.True(SelfVersion.Parse(newer)!.IsNewerThan(SelfVersion.Parse(older)!));
        Assert.False(SelfVersion.Parse(older)!.IsNewerThan(SelfVersion.Parse(newer)!));
    }

    [Fact]
    public void SameVersion_IsNotNewer() => Assert.False(new SelfVersion(1, 0, 0).IsNewerThan(new SelfVersion(1, 0, 0)));

    [Fact]
    public void Text_IsThePlainNumbers() => Assert.Equal("1.10.0", new SelfVersion(1, 10, 0).ToString());

    [Fact]
    public void WhatsNew_IsTheReleasePage() =>
        Assert.Equal("https://github.com/Bikuuuu/tiny-tracker/releases/tag/v0.2.0", new SelfRelease(new SelfVersion(0, 2, 0), DateTimeOffset.UnixEpoch).NotesUrl);
}
