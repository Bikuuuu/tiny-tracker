using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using TinyTracker.WinGet.Throttling;
using Xunit;

namespace TinyTracker.WinGet.Tests.Throttling;

public class ThrottlingRelayTests
{
    // The relay serves only the process it was started for; here that's this test.
    private static bool FromThisTest(int process) => process == Environment.ProcessId;

    // Windows' TCP table gives a connection that just closed to process 0, and winget's id comes only once it has started.
    [Fact]
    public void OnlyOneProcess_IsServed_AndNoneBeforeItsIdIsKnown()
    {
        var served = 0;
        var only = ThrottlingRelay.Only(() => served);
        Assert.False(only(0));
        served = 4242;
        Assert.True(only(4242));
        Assert.False(only(0));
        Assert.False(only(4243));
    }

    [Fact]
    public async Task Connect_TunnelsBytesAtTheLimit()
    {
        var ct = TestContext.Current.CancellationToken;
        var (server, port) = StartServer(200_000, ct);
        await using var relay = new ThrottlingRelay(TimeProvider.System, FromThisTest, new HashSet<int> { port }) { LimitBytesPerSecond = 100_000 };
        relay.Start();

        var stopwatch = Stopwatch.StartNew();
        var (status, received) = await ConnectAndReadAsync(relay.Port, port, ct);
        stopwatch.Stop();

        Assert.StartsWith("HTTP/1.1 200", status);
        Assert.Equal(200_000, received);
        Assert.Equal(200_000, relay.BytesDownloaded);
        Assert.InRange(stopwatch.Elapsed.TotalSeconds, 1.6, 3.5);
        await server;
    }

    [Fact]
    public async Task Unlimited_IsFast()
    {
        var ct = TestContext.Current.CancellationToken;
        var (server, port) = StartServer(2_000_000, ct);
        await using var relay = new ThrottlingRelay(TimeProvider.System, FromThisTest, new HashSet<int> { port });
        relay.Start();

        var stopwatch = Stopwatch.StartNew();
        var (_, received) = await ConnectAndReadAsync(relay.Port, port, ct);

        Assert.Equal(2_000_000, received);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
        await server;
    }

    [Fact]
    public async Task LiftingTheLimitMidTransfer_TakesEffectQuickly()
    {
        var ct = TestContext.Current.CancellationToken;
        var (server, port) = StartServer(300_000, ct);
        await using var relay = new ThrottlingRelay(TimeProvider.System, FromThisTest, new HashSet<int> { port }) { LimitBytesPerSecond = 2_000 };
        relay.Start();

        var stopwatch = Stopwatch.StartNew();
        var read = ConnectAndReadAsync(relay.Port, port, ct);
        await Task.Delay(1000, ct);
        relay.LimitBytesPerSecond = 0;
        var (_, received) = await read;

        Assert.Equal(300_000, received);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"took {stopwatch.Elapsed}");
        await server;
    }

    // As when its upgrade is cancelled.
    [Fact]
    public async Task Refusing_EndsWhatItTunnels_AndTakesNoMore()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, port) = StartServer(300_000, ct);
        await using var relay = new ThrottlingRelay(TimeProvider.System, FromThisTest, new HashSet<int> { port }) { LimitBytesPerSecond = 2_000 };
        relay.Start();
        var read = ConnectAndReadAsync(relay.Port, port, ct);
        await Task.Delay(500, ct);
        relay.Refuse();
        var (status, received) = await read.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.StartsWith("HTTP/1.1 200", status);
        Assert.True(received < 300_000, $"{received} bytes came through");
        using var client = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(() => client.ConnectAsync(IPAddress.Loopback, relay.Port, ct).AsTask());
    }

    // Each upgrade stops its relay at the end, even as it goes back to taking connections.
    [Fact]
    public async Task StoppingRightAfterAnAnswer_NeverThrows()
    {
        var ct = TestContext.Current.CancellationToken;
        for (var i = 0; i < 300; i++)
        {
            var relay = new ThrottlingRelay(TimeProvider.System, FromThisTest);
            relay.Start();
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, relay.Port, ct);
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes("POST http://example.com/ HTTP/1.1\r\nHost: example.com\r\n\r\n"), ct);
            _ = await stream.ReadAsync(new byte[256], ct);
            await relay.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisallowedPort_IsForbidden()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var relay = new ThrottlingRelay(TimeProvider.System, FromThisTest);
        relay.Start();
        var (status, _) = await ConnectAndReadAsync(relay.Port, 8080, ct);
        Assert.StartsWith("HTTP/1.1 403", status);
    }

    [Fact]
    public async Task ConnectionFromAProcessItDoesntServe_GetsNoAnswer()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var relay = new ThrottlingRelay(TimeProvider.System, process => process == Environment.ProcessId + 1);
        relay.Start();
        Assert.Equal(("", 0L), await ConnectAndReadAsync(relay.Port, 443, ct));
    }

    // Only a tunnel, or a plain download as a proxy is asked for one.
    [Theory]
    [InlineData("POST http://example.com/ HTTP/1.1")]
    [InlineData("GET / HTTP/1.1")]
    [InlineData("GET https://example.com/ HTTP/1.1")]
    public async Task OtherRequests_AreRejected(string requestLine)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var relay = new ThrottlingRelay(TimeProvider.System, FromThisTest);
        relay.Start();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, relay.Port, ct);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"{requestLine}\r\nHost: example.com\r\n\r\n"), ct);
        var buffer = new byte[256];
        var read = await stream.ReadAsync(buffer, ct);
        Assert.StartsWith("HTTP/1.1 405", Encoding.ASCII.GetString(buffer, 0, read));
    }

    // Mirrors can send a download to a plain http address (spec §6.4); winget checks the installer's hash either way.
    [Fact]
    public async Task PlainDownload_IsForwardedAtTheLimit()
    {
        var ct = TestContext.Current.CancellationToken;
        var (server, port) = StartHttpServer(200_000, ct);
        await using var relay = new ThrottlingRelay(TimeProvider.System, FromThisTest, plainPorts: new HashSet<int> { port }) { LimitBytesPerSecond = 100_000 };
        relay.Start();

        var stopwatch = Stopwatch.StartNew();
        var (status, received) = await GetAndReadAsync(relay.Port, $"http://127.0.0.1:{port}/vlc.msi?direct", ct);
        stopwatch.Stop();

        Assert.StartsWith("HTTP/1.1 200", status);
        Assert.Equal(200_000, received);
        Assert.InRange(relay.BytesDownloaded, 200_000, 201_000);
        Assert.InRange(stopwatch.Elapsed.TotalSeconds, 1.6, 3.5);
        // The server gets what it would from winget itself, and closes after its answer.
        var request = (await server).Split("\r\n");
        Assert.Equal("GET /vlc.msi?direct HTTP/1.1", request[0]);
        Assert.Contains($"Host: 127.0.0.1:{port}", request);
        Assert.Contains("User-Agent: winget-cli", request);
        Assert.Contains("Connection: close", request);
        Assert.DoesNotContain(request, line => line.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase) || line == "Connection: Keep-Alive");
    }

    [Fact]
    public async Task PlainDownloadFromAnotherPort_IsForbidden()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var relay = new ThrottlingRelay(TimeProvider.System, FromThisTest);
        relay.Start();
        var (status, _) = await GetAndReadAsync(relay.Port, "http://127.0.0.1:8080/vlc.msi", ct);
        Assert.StartsWith("HTTP/1.1 403", status);
    }

    private static (Task Serve, int Port) StartServer(int bytes, CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serve = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            await client.GetStream().WriteAsync(new byte[bytes], ct);
            listener.Stop();
        }, ct);
        return (serve, port);
    }

    // An http server that answers one request with that many bytes; it returns the request's head.
    private static (Task<string> Request, int Port) StartHttpServer(int bytes, CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serve = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            var stream = client.GetStream();
            var head = await ReadHeadAsync(stream, ct);
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {bytes}\r\n\r\n"), ct);
            await stream.WriteAsync(new byte[bytes], ct);
            listener.Stop();
            return head;
        }, ct);
        return (serve, port);
    }

    private static Task<(string Status, long Received)> ConnectAndReadAsync(int relayPort, int targetPort, CancellationToken ct) =>
        RequestAndReadAsync(relayPort, $"CONNECT 127.0.0.1:{targetPort} HTTP/1.1\r\nHost: 127.0.0.1:{targetPort}\r\n\r\n", ct);

    // As winget asks a proxy for a plain http download.
    private static Task<(string Status, long Received)> GetAndReadAsync(int relayPort, string url, CancellationToken ct) =>
        RequestAndReadAsync(relayPort, $"GET {url} HTTP/1.1\r\nHost: {new Uri(url).Authority}\r\nUser-Agent: winget-cli\r\nProxy-Connection: Keep-Alive\r\nConnection: Keep-Alive\r\n\r\n", ct);

    private static async Task<string> ReadHeadAsync(Stream stream, CancellationToken ct)
    {
        var head = new StringBuilder();
        var one = new byte[1];
        while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) && await stream.ReadAsync(one, ct) == 1)
            head.Append((char)one[0]);
        return head.ToString();
    }

    // The answer's head, then the bytes after it.
    private static async Task<(string Status, long Received)> RequestAndReadAsync(int relayPort, string request, CancellationToken ct)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, relayPort, ct);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), ct);
        var head = new StringBuilder();
        long total = 0;
        try
        {
            var one = new byte[1];
            while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) && await stream.ReadAsync(one, ct) == 1)
                head.Append((char)one[0]);
            var buffer = new byte[65536];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0) total += read;
        }
        catch (IOException)
        {
            // The relay hung up at once.
        }
        return (head.ToString(), total);
    }
}
