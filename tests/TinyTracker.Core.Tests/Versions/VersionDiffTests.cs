using TinyTracker.Core.Versions;
using Xunit;

namespace TinyTracker.Core.Tests.Versions;

public class VersionDiffTests
{
    [Theory]
    [InlineData("2025.1.2", "2025.1.3", "2025.1.", "3")]
    [InlineData("8.9.7", "8.9.8.1", "8.9.", "8.1")]
    [InlineData("1.2", "1.2.1", "1.2.", "1")]
    [InlineData("1.0", "2.0", "", "2.0")]
    [InlineData("Unknown", "1.2", "", "1.2")]
    [InlineData(null, "1.2", "", "1.2")]
    [InlineData("1.2.3", "1.2.3", "1.2.3", "")]
    public void Between_HighlightsFromTheFirstChangedPart(string? oldVersion, string newVersion, string unchanged, string changed) =>
        Assert.Equal(new VersionDiff(unchanged, changed), VersionDiff.Between(oldVersion, newVersion));

    [Fact]
    public void MissingNewVersion_IsEmpty() => Assert.Equal(new VersionDiff("", ""), VersionDiff.Between("1.0", null));

    [Theory]
    [InlineData("1.2.3", "1.3.0-beta")]
    [InlineData("24.08", "24.9")]
    [InlineData("", "")]
    public void Parts_JoinBackToTheNewVersion(string oldVersion, string newVersion)
    {
        var diff = VersionDiff.Between(oldVersion, newVersion);
        Assert.Equal(newVersion, diff.Unchanged + diff.Changed);
    }
}
