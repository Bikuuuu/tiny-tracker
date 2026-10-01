using Xunit;

namespace TinyTracker.Core.Tests;

public class AppInfoTests
{
    [Fact]
    public void Name_IsTheProductName() => Assert.Equal("Tiny Tracker", AppInfo.Name);

    [Fact]
    public void InstanceKey_HasNoSpaces() => Assert.Equal("TinyTracker", AppInfo.InstanceKey);

    [Fact]
    public void RepositoryUrl_IsTheProjectsPage() => Assert.Equal("https://github.com/Bikuuuu/tiny-tracker", AppInfo.RepositoryUrl);

    [Fact]
    public void TipUrl_IsTheKofiPage() => Assert.Equal("https://ko-fi.com/Bikuuuu", AppInfo.TipUrl);
}
