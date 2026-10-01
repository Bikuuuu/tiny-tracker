using TinyTracker.WinGet.Matching;
using Xunit;

namespace TinyTracker.WinGet.Tests.Matching;

public class NameKeyTests
{
    [Theory]
    [InlineData("Python 3.12.5 (64-bit)", "Python 3.12")]
    [InlineData("Mozilla Firefox (x64 en-US)", "Mozilla Firefox")]
    [InlineData("7-Zip 24.09 (x64)", "7-Zip")]
    [InlineData("Example Studio™ 2025", "Example Studio")]
    [InlineData("Example Tool v2.1 x64", "EXAMPLE TOOL")]
    [InlineData("Example Runtime", "Example Runtime (LTS)")]
    [InlineData("Example Tool® 2.0 [32-bit]", "Example Tool")]
    [InlineData("Example Tool 2.0 64bit", "Example Tool")]
    [InlineData("Example Tool 2.0 x86-64", "Example Tool")]
    public void SameProduct_HasTheSameKey(string installed, string catalog) => Assert.Equal(NameKey.Of(catalog), NameKey.Of(installed));

    [Theory]
    [InlineData("Mozilla Firefox", "Mozilla Firefox Beta")]
    [InlineData("Mozilla Firefox", "Mozilla Firefox Developer Edition")]
    [InlineData("Visual Studio Code", "Visual Studio Code Insiders")]
    [InlineData("Example Browser", "Example Browser (Beta)")]
    [InlineData("Example Browser", "Example Browser [Preview]")]
    [InlineData("Example Browser", "Example Browser (RC1)")]
    [InlineData("Example Browser", "Example Browser (beta2)")]
    [InlineData("Example Browser", "Example Browser (Pre-release)")]
    [InlineData("Example Browser", "Example Browser (Early Access)")]
    [InlineData("Mozilla Firefox", "Mozilla Firefox (Developer Edition)")]
    public void OtherEdition_HasAnotherKey(string installed, string catalog) => Assert.NotEqual(NameKey.Of(catalog), NameKey.Of(installed));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("(x64) 1.0")]
    public void NothingLeft_IsEmpty(string? name) => Assert.Equal("", NameKey.Of(name));

    [Theory]
    [InlineData("Python 3.12.5 (64-bit)", "Python")]
    [InlineData("Mozilla Firefox (x64 en-US)", "Mozilla Firefox")]
    [InlineData("Example Studio 2025", "Example Studio")]
    public void SearchTerm_StaysReadable(string name, string term) => Assert.Equal(term, NameKey.SearchTerm(name));

    [Theory]
    [InlineData("Python 3.12.5 (64-bit)", "3.12.5")]
    [InlineData("7-Zip 24.09 (x64)", "7|24.9")]
    [InlineData("Example Studio 2025", "2025")]
    [InlineData("Example Tool", "")]
    [InlineData("Example Tool 2 64bit", "2")]
    [InlineData("Example Tool 2 (x86-64)", "2")]
    public void Numbers_AreTheNumbersInAName(string name, string numbers) =>
        Assert.Equal(numbers, string.Join('|', NameKey.Numbers(name)));
}
