using Xunit;

namespace TinyTracker.Presentation.Tests;

public class LinksTests
{
    [Theory]
    [InlineData("https://github.com/Bikuuuu/tiny-tracker", "https://github.com/Bikuuuu/tiny-tracker")]
    [InlineData(" http://example.com/notes ", "http://example.com/notes")]
    [InlineData(Notice.AppInstallerStoreLink, Notice.AppInstallerStoreLink)]
    public void WebLinksAndTheStorePage_Open(string url, string opened) => Assert.Equal(opened, Links.Openable(url));

    [Theory]
    [InlineData(null)]
    [InlineData("file:///C:/Windows/notepad.exe")]
    [InlineData("ms-settings:windowsupdate")]
    [InlineData("ms-windows-store://pdp/?productid=9WZDNCRFHVN5")]
    [InlineData("search-ms:query=x")]
    public void AnythingElse_DoesNot(string? url) => Assert.Null(Links.Openable(url));
}
