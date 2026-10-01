using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TinyTracker.WinGet.Tests.Cli;

// A tiny web server on 127.0.0.1 for the download tests: /editor.exe answers a HEAD with its headers and a GET with its bytes,
// /moved sends there, and any other path is missing.
internal sealed class FakeServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly long _bytes;
    private readonly string _type;
    private int _heads;
    private int _gets;

    public FakeServer(long bytes, string type = "application/octet-stream")
    {
        _bytes = bytes;
        _type = type;
        _listener.Start();
        _ = ServeAsync(_stop.Token);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public Uri Url(string path = "/editor.exe") => new($"http://127.0.0.1:{Port}{path}");

    public int Heads => Volatile.Read(ref _heads);

    public int Gets => Volatile.Read(ref _gets);

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
    }

    private async Task ServeAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct);
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }
            _ = AnswerAsync(client, ct);
        }
    }

    private async Task AnswerAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var head = new StringBuilder();
                var one = new byte[1];
                while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) && await stream.ReadAsync(one, ct) == 1) head.Append((char)one[0]);
                var request = head.ToString().Split(' ');
                if (request.Length < 2) return;
                var (method, path) = (request[0], request[1]);
                if (path == "/moved")
                {
                    await Send(stream, "HTTP/1.1 302 Found\r\nLocation: /editor.exe\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", ct);
                    return;
                }
                if (path != "/editor.exe")
                {
                    await Send(stream, "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", ct);
                    return;
                }
                if (method == "HEAD") Interlocked.Increment(ref _heads);
                else Interlocked.Increment(ref _gets);
                await Send(stream, $"HTTP/1.1 200 OK\r\nContent-Length: {_bytes}\r\nContent-Type: {_type}\r\nConnection: close\r\n\r\n", ct);
                if (method != "GET") return;
                var chunk = new byte[16 * 1024];
                for (var sent = 0L; sent < _bytes; sent += chunk.Length)
                    await stream.WriteAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, _bytes - sent)), ct);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // The client hung up, or the test ended.
            }
        }
    }

    private static Task Send(Stream stream, string text, CancellationToken ct) => stream.WriteAsync(Encoding.ASCII.GetBytes(text), ct).AsTask();
}
