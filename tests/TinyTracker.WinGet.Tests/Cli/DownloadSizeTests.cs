using System.Net;
using System.Net.Sockets;
using TinyTracker.WinGet.Cli;
using Xunit;

namespace TinyTracker.WinGet.Tests.Cli;

// A download's size comes from a HEAD request on its address; anything unclear counts as unknown (spec §6.4).
public sealed class DownloadSizeTests : IDisposable
{
    private readonly HttpClient _http = new();

    public void Dispose() => _http.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Head_TellsTheSize()
    {
        using var server = new FakeServer(123_456);
        Assert.Equal(123_456UL, await DownloadSize.OfAsync(_http, server.Url(), Ct));
        Assert.Equal((1, 0), (server.Heads, server.Gets));
    }

    [Fact]
    public async Task Redirect_IsFollowed()
    {
        using var server = new FakeServer(123_456);
        Assert.Equal(123_456UL, await DownloadSize.OfAsync(_http, server.Url("/moved"), Ct));
    }

    [Fact]
    public async Task WebPage_CountsAsUnknown()
    {
        using var server = new FakeServer(5_000, "text/html; charset=utf-8");
        Assert.Equal(0UL, await DownloadSize.OfAsync(_http, server.Url(), Ct));
    }

    [Fact]
    public async Task MissingFile_CountsAsUnknown()
    {
        using var server = new FakeServer(5_000);
        Assert.Equal(0UL, await DownloadSize.OfAsync(_http, server.Url("/missing.exe"), Ct));
    }

    [Fact]
    public async Task NoServer_CountsAsUnknown()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        Assert.Equal(0UL, await DownloadSize.OfAsync(_http, new Uri($"http://127.0.0.1:{port}/editor.exe"), Ct));
    }

    [Fact]
    public async Task OtherAddresses_AreNotAsked() => Assert.Equal(0UL, await DownloadSize.OfAsync(_http, new Uri("ftp://127.0.0.1/editor.exe"), Ct));
}
