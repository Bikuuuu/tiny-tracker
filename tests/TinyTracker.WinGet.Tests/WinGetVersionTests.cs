using Xunit;

namespace TinyTracker.WinGet.Tests;

public class WinGetVersionTests
{
    [Theory]
    [InlineData("1.29.280", true)]
    [InlineData("1.29.380", true)]
    [InlineData("v1.29.380", true)]
    [InlineData("1.30.0", true)]
    [InlineData("1.29.279", false)]
    [InlineData("1.11.510", false)]
    [InlineData("Unknown", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Floor_Is1_29_280(string? version, bool supported) => Assert.Equal(supported, WinGetVersion.IsSupported(version));
}
