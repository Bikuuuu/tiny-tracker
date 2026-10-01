using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using static TinyTracker.WinGet.Interop.NativeMethods;

namespace TinyTracker.WinGet.Throttling;

// A relay on 127.0.0.1 that holds downloads to a limit: it tunnels https, never decrypted, and forwards plain http
// (spec §6.4, §8). fromProcess says whether a process id may use it; a connection from any other gets no answer.
public sealed class ThrottlingRelay(TimeProvider time, Func<int, bool> fromProcess, IReadOnlySet<int>? allowedPorts = null,
    IReadOnlySet<int>? plainPorts = null) : IAsyncDisposable
{
    private const int MaxHeadBytes = 8192;
    private const int InterNetwork = 2;
    private const int TcpTableOwnerPidConnections = 4;
    private const int InsufficientBuffer = 122;
    // 127.0.0.1 as Windows' TCP table has it, in network order.
    private const int Loopback = 0x0100007F;
    private static readonly TimeSpan MaxSleep = TimeSpan.FromMilliseconds(100);

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly TokenBucket _bucket = new(time);
    private readonly IReadOnlySet<int> _allowedPorts = allowedPorts ?? new HashSet<int> { 443 };
    private readonly IReadOnlySet<int> _plainPorts = plainPorts ?? new HashSet<int> { 80 };
    private readonly CancellationTokenSource _stop = new();
    private Task? _acceptLoop;
    private long _bytesDownloaded;
    private long _firstByteAt;
    private long _lastByteAt;

    // Serves only that process, and none before its id is known: the TCP table gives a connection that just closed to process 0.
    public static Func<int, bool> Only(Func<int> process) => owner => owner > 0 && owner == process();

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public Uri ProxyUri => new($"http://127.0.0.1:{Port}");
    public long BytesDownloaded => Interlocked.Read(ref _bytesDownloaded);

    public TimeSpan TransferDuration
    {
        get
        {
            var first = Interlocked.Read(ref _firstByteAt);
            return first == 0 ? TimeSpan.Zero : time.GetElapsedTime(first, Interlocked.Read(ref _lastByteAt));
        }
    }

    public long LimitBytesPerSecond
    {
        get => _bucket.BytesPerSecond;
        set => _bucket.BytesPerSecond = value;
    }

    public void Start()
    {
        _listener.Start();
        _acceptLoop = AcceptLoopAsync(_stop.Token);
    }

    // From now on it tunnels nothing: what it tunnels ends, and new connections are refused.
    public void Refuse()
    {
        _stop.Cancel();
        _listener.Stop();
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            // Stopping ends the wait, or finds the listener already gone.
            try { client = await _listener.AcceptTcpClientAsync(ct); }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException or InvalidOperationException) { break; }
            _ = HandleAsync(client, ct);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                if (OwnerOf((IPEndPoint)client.Client.RemoteEndPoint!) is not { } owner || !fromProcess(owner)) return;
                var stream = client.GetStream();
                var head = await ReadHeadAsync(stream, ct);
                var lines = head?.Split("\r\n");
                var parts = lines?[0].Split(' ');
                if (parts is { Length: 3 } && parts[0] == "CONNECT")
                    await TunnelAsync(stream, parts[1], ct);
                // Mirrors can send a download to a plain http address; winget checks the installer's hash either way.
                else if (parts is { Length: 3 } && parts[0] is "GET" or "HEAD" && Uri.TryCreate(parts[1], UriKind.Absolute, out var url)
                    && url.Scheme == Uri.UriSchemeHttp)
                    await ForwardAsync(stream, parts, lines!, url, ct);
                else
                    await WriteStatusAsync(stream, "405 Method Not Allowed", ct);
            }
            catch (IOException) { }
            catch (OperationCanceledException) { }
        }
    }

    private async Task TunnelAsync(Stream stream, string target, CancellationToken ct)
    {
        if (!TryParseTarget(target, out var host, out var port) || !_allowedPorts.Contains(port))
        {
            await WriteStatusAsync(stream, "403 Forbidden", ct);
            return;
        }
        using var upstream = await ConnectAsync(host, port, ct);
        if (upstream is null)
        {
            await WriteStatusAsync(stream, "502 Bad Gateway", ct);
            return;
        }
        await WriteStatusAsync(stream, "200 Connection Established", ct);
        await RelayAsync(stream, upstream.GetStream(), ct);
    }

    // As the server would get it from winget itself: the path alone, without the proxy's headers, and closed after its answer.
    private async Task ForwardAsync(Stream stream, string[] parts, string[] lines, Uri url, CancellationToken ct)
    {
        if (!_plainPorts.Contains(url.Port))
        {
            await WriteStatusAsync(stream, "403 Forbidden", ct);
            return;
        }
        using var upstream = await ConnectAsync(url.DnsSafeHost, url.Port, ct);
        if (upstream is null)
        {
            await WriteStatusAsync(stream, "502 Bad Gateway", ct);
            return;
        }
        var request = new StringBuilder($"{parts[0]} {url.PathAndQuery} {parts[2]}\r\n");
        foreach (var line in lines.Skip(1))
        {
            if (line.Length == 0 || line.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Connection:", StringComparison.OrdinalIgnoreCase) || line.StartsWith("Keep-Alive:", StringComparison.OrdinalIgnoreCase))
                continue;
            request.Append(line).Append("\r\n");
        }
        request.Append("Connection: close\r\n\r\n");
        var remote = upstream.GetStream();
        await remote.WriteAsync(Encoding.ASCII.GetBytes(request.ToString()), ct);
        await RelayAsync(stream, remote, ct);
    }

    private static async Task<TcpClient?> ConnectAsync(string host, int port, CancellationToken ct)
    {
        var upstream = new TcpClient();
        try
        {
            await upstream.ConnectAsync(host, port, ct);
            return upstream;
        }
        catch (Exception e)
        {
            upstream.Dispose();
            if (e is SocketException) return null;
            throw;
        }
    }

    // Until either side ends; only the download is held to the limit.
    private Task RelayAsync(Stream client, Stream remote, CancellationToken ct) =>
        Task.WhenAny(client.CopyToAsync(remote, ct), CopyThrottledAsync(remote, client, ct));

    private async Task CopyThrottledAsync(Stream from, Stream to, CancellationToken ct)
    {
        var buffer = new byte[TokenBucket.ChunkSize(0)];
        int read;
        while ((read = await from.ReadAsync(buffer.AsMemory(0, TokenBucket.ChunkSize(_bucket.BytesPerSecond)), ct)) > 0)
        {
            // Sleep in short slices so limit changes apply within ~100 ms.
            for (var wait = _bucket.Take(read); wait > TimeSpan.Zero; wait = _bucket.PendingDelay())
                await Task.Delay(wait < MaxSleep ? wait : MaxSleep, time, ct);
            await to.WriteAsync(buffer.AsMemory(0, read), ct);
            var now = time.GetTimestamp();
            Interlocked.CompareExchange(ref _firstByteAt, now, 0);
            Interlocked.Exchange(ref _lastByteAt, now);
            Interlocked.Add(ref _bytesDownloaded, read);
        }
    }

    private static async Task<string?> ReadHeadAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxHeadBytes];
        var length = 0;
        while (length < buffer.Length)
        {
            if (await stream.ReadAsync(buffer.AsMemory(length, 1), ct) == 0) return null;
            length++;
            if (length >= 4 && buffer[length - 4] == '\r' && buffer[length - 3] == '\n' && buffer[length - 2] == '\r' && buffer[length - 1] == '\n')
                return Encoding.ASCII.GetString(buffer, 0, length);
        }
        return null;
    }

    // The process that made this connection to the relay, from Windows' table of TCP connections.
    private int? OwnerOf(IPEndPoint client)
    {
        var size = 0;
        GetExtendedTcpTable(0, ref size, false, InterNetwork, TcpTableOwnerPidConnections, 0);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var table = Marshal.AllocHGlobal(size);
            try
            {
                var error = GetExtendedTcpTable(table, ref size, false, InterNetwork, TcpTableOwnerPidConnections, 0);
                if (error == InsufficientBuffer) continue;
                if (error != 0) return null;
                // A count, then rows of state, local and remote address and port, and owner; ports in network order.
                var rows = Marshal.ReadInt32(table);
                for (var i = 0; i < rows; i++)
                {
                    var row = table + 4 + i * 24;
                    if (Marshal.ReadInt32(row, 4) == Loopback && PortOf(Marshal.ReadInt32(row, 8)) == client.Port
                        && Marshal.ReadInt32(row, 12) == Loopback && PortOf(Marshal.ReadInt32(row, 16)) == Port)
                        return Marshal.ReadInt32(row, 20);
                }
                return null;
            }
            finally
            {
                Marshal.FreeHGlobal(table);
            }
        }
        return null;
    }

    private static int PortOf(int value) => (value & 0xFF) << 8 | (value >> 8) & 0xFF;

    private static bool TryParseTarget(string target, out string host, out int port)
    {
        var colon = target.LastIndexOf(':');
        host = colon > 0 ? target[..colon].Trim('[', ']') : "";
        port = 0;
        return colon > 0 && int.TryParse(target[(colon + 1)..], out port) && host.Length > 0;
    }

    private static Task WriteStatusAsync(Stream stream, string status, CancellationToken ct) =>
        stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\n\r\n"), ct).AsTask();

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        if (_acceptLoop is not null) await _acceptLoop;
        _stop.Dispose();
    }
}
