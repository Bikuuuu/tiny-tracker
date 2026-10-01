using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using TinyTracker.WinGet.Tests.SelfUpdate;

namespace TinyTracker.WinGet.Tests.Integration;

// The stand-in as this runner's GitHub (spec §12): on port 443, its certificate trusted by the machine and its hosts on loopback,
// until it's disposed. Whatever else on the runner asks for those hosts meanwhile reaches the stand-in too. Only on GitHub runners,
// which are thrown away after the job.
internal sealed class RunnerGitHub : IAsyncDisposable
{
    private static readonly string HostsFile = Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts");
    // Windows' resolver reads the hosts file again a moment after it changes.
    private static readonly TimeSpan ResolveWait = TimeSpan.FromSeconds(30);
    private readonly X509Certificate2 _trusted;
    private byte[]? _hosts;

    private RunnerGitHub(StandInGitHub github)
    {
        GitHub = github;
        _trusted = X509CertificateLoader.LoadCertificate(github.Certificate.RawData);
    }

    public StandInGitHub GitHub { get; }

    public static async Task<RunnerGitHub> StartAsync()
    {
        StandInGitHub github;
        try
        {
            github = StandInGitHub.Start(443);
        }
        catch (SocketException e)
        {
            throw new InvalidOperationException($"Port 443 of 127.0.0.1 or [::1] is taken: {e.Message}", e);
        }
        var runner = new RunnerGitHub(github);
        try
        {
            using (var root = new X509Store(StoreName.Root, StoreLocation.LocalMachine))
            {
                root.Open(OpenFlags.ReadWrite);
                root.Add(runner._trusted);
            }
            runner._hosts = File.ReadAllBytes(HostsFile);
            // One name a line.
            File.AppendAllLines(HostsFile, ["", .. StandInGitHub.Hosts.SelectMany(h => new[] { $"127.0.0.1 {h}", $"::1 {h}" })]);
            var until = DateTime.UtcNow + ResolveWait;
            while (true)
            {
                FlushDns();
                if (StandInGitHub.Hosts.All(h => Resolve(h) is { Length: > 0 } addresses && addresses.All(IPAddress.IsLoopback))) return runner;
                if (DateTime.UtcNow > until) throw new InvalidOperationException($"The stand-in's hosts don't resolve to it: {Resolution()}");
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }
        catch
        {
            await runner.DisposeAsync();
            throw;
        }
    }

    // What each of the stand-in's hosts resolves to now.
    public static string Resolution() => string.Join("; ", StandInGitHub.Hosts.Select(h => $"{h} = {string.Join(", ", Resolve(h).Select(a => a.ToString()))}"));

    // Each step runs even when one before it fails.
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_hosts is { } hosts)
            {
                File.WriteAllBytes(HostsFile, hosts);
                FlushDns();
            }
        }
        finally
        {
            try
            {
                using var root = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
                root.Open(OpenFlags.ReadWrite);
                root.Remove(_trusted);
            }
            finally
            {
                _trusted.Dispose();
                await GitHub.DisposeAsync();
            }
        }
    }

    private static IPAddress[] Resolve(string host)
    {
        try
        {
            return Dns.GetHostAddresses(host);
        }
        catch (SocketException)
        {
            return [];
        }
    }

    private static void FlushDns()
    {
        using var flush = Process.Start(new ProcessStartInfo("ipconfig", "/flushdns") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        flush.StandardOutput.ReadToEnd();
        flush.WaitForExit();
    }
}
