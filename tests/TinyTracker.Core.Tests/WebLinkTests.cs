using Xunit;

namespace TinyTracker.Core.Tests;

public class WebLinkTests
{
    [Theory]
    [InlineData("https://example.com/notes", "https://example.com/notes")]
    [InlineData("  http://example.com/release notes  ", "http://example.com/release%20notes")]
    public void WebLinks_AreKept(string url, string clean) => Assert.Equal(clean, WebLink.Clean(url));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("file:///C:/notes.txt")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ms-settings:windowsupdate")]
    [InlineData("not a link")]
    public void AnythingElse_IsNone(string? url) => Assert.Null(WebLink.Clean(url));
}
