using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using TinyTracker.Core.SelfUpdate;

namespace TinyTracker.WinGet.Tests.SelfUpdate;

// GitHub as Tiny Tracker's self-update meets it, over TLS on loopback (spec §12): the API with the latest release and its one Setup,
// github.com's download address, which redirects to the asset host, and the file there. Anything else is a 404. Its certificate
// names those hosts; nothing trusts it unless a test makes it so.
public sealed class StandInGitHub : IAsyncDisposable
{
    public static readonly IReadOnlyList<string> Hosts = ["api.github.com", "github.com", "release-assets.githubusercontent.com"];
    private const string Releases = "/repos/Bikuuuu/tiny-tracker/releases/";
    private const string Downloads = "/Bikuuuu/tiny-tracker/releases/download/";
    private const int MaxHead = 16 * 1024;

    private readonly TcpListener[] _listeners;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _running = [];
    private readonly ConcurrentQueue<(string Host, string Path)> _requests = new();
    private volatile Release? _release;

    private StandInGitHub(TcpListener[] listeners, X509Certificate2 certificate)
    {
        _listeners = listeners;
        Certificate = certificate;
        lock (_running)
            foreach (var listener in listeners)
                _running.Add(Task.Run(() => AcceptAsync(listener)));
    }

    public X509Certificate2 Certificate { get; }

    public int Port => ((IPEndPoint)_listeners[0].LocalEndpoint).Port;

    // In the order they came.
    public IReadOnlyList<(string Host, string Path)> Requests => [.. _requests];

    // On a free port of 127.0.0.1, or on that port of both loopbacks.
    public static StandInGitHub Start(int port = 0)
    {
        var certificate = NewCertificate();
        TcpListener[] listeners = port == 0 ? [new(IPAddress.Loopback, 0)] : [new(IPAddress.Loopback, port), new(IPAddress.IPv6Loopback, port)];
        try
        {
            foreach (var listener in listeners) listener.Start();
        }
        catch
        {
            foreach (var listener in listeners) listener.Stop();
            certificate.Dispose();
            throw;
        }
        return new StandInGitHub(listeners, certificate);
    }

    // From now on the latest release is that version, published an hour ago, with that file as its Setup. Its digest is the file's,
    // or another file's.
    public void Serve(SelfVersion version, string setup, bool digestMatches = true)
    {
        byte[] hash;
        using (var file = File.OpenRead(setup)) hash = digestMatches ? SHA256.HashData(file) : SHA256.HashData("another file"u8);
        _release = new Release(version, setup, new FileInfo(setup).Length, Convert.ToHexStringLower(hash), DateTimeOffset.UtcNow.AddHours(-1),
            $"/github-production-release-asset/1/{Guid.NewGuid()}?sp=r&sig=stand-in");
    }

    // A client like the app's that reaches these hosts here and takes only this certificate, which must still name the host.
    // It follows no redirects, as the helper's doesn't.
    public HttpClient Client() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectCallback = async (context, ct) =>
        {
            if (!Hosts.Contains(context.DnsEndPoint.Host) || context.DnsEndPoint.Port != 443) throw new HttpRequestException($"{context.DnsEndPoint} isn't the stand-in's");
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(IPAddress.Loopback, Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
        SslOptions =
        {
            RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == SslPolicyErrors.None && certificate is not null
                && certificate.GetRawCertData().AsSpan().SequenceEqual(Certificate.RawData),
        },
    })
    {
        DefaultRequestHeaders = { UserAgent = { new ProductInfoHeaderValue("TinyTracker", "0.1.0") } },
    };

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        foreach (var listener in _listeners) listener.Stop();
        Task[] running;
        lock (_running) running = [.. _running];
        await Task.WhenAll(running);
        Certificate.Dispose();
        _stop.Dispose();
    }

    private static X509Certificate2 NewCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Tiny Tracker test GitHub", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        foreach (var host in Hosts) names.AddDnsName(host);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: false));
        var now = DateTimeOffset.UtcNow;
        using var made = request.CreateSelfSigned(now.AddMinutes(-5), now.AddDays(1));
        // Windows' TLS serves only a key it can find, so it's loaded again from PKCS #12; disposing that deletes the key.
        return X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pkcs12), null);
    }

    private async Task AcceptAsync(TcpListener listener)
    {
        while (true)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }
            lock (_running)
            {
                _running.RemoveAll(t => t.IsCompleted);
                _running.Add(Task.Run(() => AnswerAsync(client)));
            }
        }
    }

    // One request for each connection, as GitHub may close it after any answer.
    private async Task AnswerAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                await using var tls = new SslStream(client.GetStream());
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = Certificate }, _stop.Token);
                if (await ReadHeadAsync(tls, _stop.Token) is not { } head) return;
                _requests.Enqueue((head.Host, head.Path));
                await RespondAsync(tls, head, _release, _stop.Token);
            }
            catch (Exception e) when (e is IOException or AuthenticationException or OperationCanceledException or SocketException or ObjectDisposedException)
            {
            }
        }
    }

    private static async Task RespondAsync(Stream stream, Head head, Release? release, CancellationToken ct)
    {
        if (head.Method != "GET") await SendAsync(stream, "405 Method Not Allowed", Message("Method Not Allowed"), ct);
        // GitHub's API refuses a request with no User-Agent.
        else if (head.Host == "api.github.com" && head.Agent is null) await SendAsync(stream, "403 Forbidden", Message("Please make sure your request has a User-Agent header"), ct);
        else if (release is null) await SendAsync(stream, "404 Not Found", Message("Not Found"), ct);
        else if (head.Host == "api.github.com" && (head.Path == Releases + "latest" || head.Path == Releases + "tags/" + release.Tag))
            await SendAsync(stream, "200 OK", Encoding.UTF8.GetBytes(release.Json()), ct);
        else if (head.Host == "github.com" && head.Path == $"{Downloads}{release.Tag}/{release.Name}")
            await SendAsync(stream, "302 Found", [], ct, "Location: https://release-assets.githubusercontent.com" + release.Asset);
        else if (head.Host == "release-assets.githubusercontent.com" && head.Path == release.Asset)
        {
            await using var file = File.OpenRead(release.Setup);
            await SendHeadAsync(stream, "200 OK", "application/octet-stream", file.Length, null, ct);
            await file.CopyToAsync(stream, ct);
            await stream.FlushAsync(ct);
        }
        else await SendAsync(stream, "404 Not Found", Message("Not Found"), ct);
    }

    private static byte[] Message(string text) => Encoding.UTF8.GetBytes(new JsonObject { ["message"] = text }.ToJsonString());

    private static async Task SendAsync(Stream stream, string status, byte[] body, CancellationToken ct, string? header = null)
    {
        await SendHeadAsync(stream, status, "application/json; charset=utf-8", body.Length, header, ct);
        await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);
    }

    private static Task SendHeadAsync(Stream stream, string status, string type, long length, string? header, CancellationToken ct)
    {
        var head = $"HTTP/1.1 {status}\r\nContent-Type: {type}\r\nContent-Length: {length}\r\nConnection: close\r\n{(header is null ? "" : header + "\r\n")}\r\n";
        return stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct).AsTask();
    }

    // The request line and the headers that matter here; null for anything that isn't a request.
    private static async Task<Head?> ReadHeadAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxHead];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), ct);
            if (read == 0) return null;
            length += read;
            var end = buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8);
            if (end >= 0) return Head.Parse(Encoding.ASCII.GetString(buffer, 0, end));
        }
        return null;
    }

    private sealed record Head(string Method, string Path, string Host, string? Agent)
    {
        public static Head? Parse(string text)
        {
            var lines = text.Split("\r\n");
            var start = lines[0].Split(' ');
            if (start.Length != 3) return null;
            string? host = null, agent = null;
            foreach (var line in lines.Skip(1))
            {
                var colon = line.IndexOf(':');
                if (colon <= 0) continue;
                var (name, value) = (line[..colon].Trim(), line[(colon + 1)..].Trim());
                if (name.Equals("Host", StringComparison.OrdinalIgnoreCase)) host = value.Split(':')[0].ToLowerInvariant();
                else if (name.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)) agent = value;
            }
            return host is null ? null : new Head(start[0], start[1], host, agent);
        }
    }

    private sealed record Release(SelfVersion Version, string Setup, long Size, string Digest, DateTimeOffset Published, string Asset)
    {
        public string Tag => "v" + Version;

        public string Name => $"TinyTracker-Setup-{Version}-x64.exe";

        // The fields GitHub's API gives, as it words them.
        public string Json() => new JsonObject
        {
            ["url"] = $"https://api.github.com{Releases}1",
            ["html_url"] = $"https://github.com/Bikuuuu/tiny-tracker/releases/tag/{Tag}",
            ["id"] = 1,
            ["tag_name"] = Tag,
            ["name"] = $"Tiny Tracker {Version}",
            ["draft"] = false,
            ["immutable"] = true,
            ["prerelease"] = false,
            ["created_at"] = Time(Published.AddMinutes(-5)),
            ["published_at"] = Time(Published),
            ["assets"] = new JsonArray(new JsonObject
            {
                ["url"] = $"https://api.github.com{Releases}assets/2",
                ["id"] = 2,
                ["name"] = Name,
                ["content_type"] = "application/x-msdownload",
                ["state"] = "uploaded",
                ["size"] = Size,
                ["digest"] = "sha256:" + Digest,
                ["browser_download_url"] = $"https://github.com{Downloads}{Tag}/{Name}",
            }),
        }.ToJsonString();

        private static string Time(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }
}
