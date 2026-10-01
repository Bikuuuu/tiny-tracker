using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace TinyTracker.FakeWinGet;

// Acts like winget's command line for the speed-limit tests (spec §12). It writes ran.txt as it starts and the arguments it got
// to args.json, then plays scenario.json from its own folder: lines to print, downloads through the --proxy relay, reads of
// its sources through it, pauses, files it makes to say how far it got, and an installer, which is another copy of itself
// started with --installer <ms>. Then it exits with the scenario's code. As winget, a download that fails ends it, after
// failed.txt, and a read of its sources that fails only warns.
internal static class Program
{
    private static int Main(string[] args)
    {
        var folder = AppContext.BaseDirectory;
        if (args is ["--installer", var ms])
        {
            Thread.Sleep(int.Parse(ms));
            return 0;
        }
        File.WriteAllText(Path.Combine(folder, "ran.txt"), "");
        File.WriteAllText(Path.Combine(folder, "args.json"), JsonSerializer.Serialize(args));
        var scenario = JsonSerializer.Deserialize<Scenario>(File.ReadAllText(Path.Combine(folder, "scenario.json")), Json)!;
        // winget writes UTF-8 when its output is piped.
        using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        var proxy = Array.IndexOf(args, "--proxy") is var at and >= 0 && at + 1 < args.Length ? new Uri(args[at + 1]) : null;
        foreach (var step in scenario.Steps)
        {
            if (step.Say is { } line) output.WriteLine(line);
            if (step.Sleep > 0) Thread.Sleep(step.Sleep);
            if (step.Touch is { } file) File.WriteAllText(Path.Combine(folder, file), "");
            if (step.Fetch is { } source && !Download(new Uri(source), proxy!, step.Pace))
                output.WriteLine("Failed when searching source; results will not be included: winget");
            if (step.Download is { } url)
            {
                output.WriteLine($"Downloading {url}");
                // As a real download, whose first bytes take a round trip or two.
                Thread.Sleep(200);
                if (!Download(new Uri(url), proxy!, step.Pace))
                {
                    File.WriteAllText(Path.Combine(folder, "failed.txt"), "");
                    output.WriteLine("An unexpected error occurred while executing the command:");
                    // One that hangs on after saying so, as a winget that must be ended.
                    if (step.Stubborn) Thread.Sleep(Timeout.Infinite);
                    // WinINet's "the connection ended", which winget exits with.
                    return unchecked((int)0x80072EFE);
                }
            }
            if (step.Installer > 0)
            {
                using var installer = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, ["--installer", step.Installer.ToString()]) { UseShellExecute = false })!;
                installer.WaitForExit();
            }
        }
        return scenario.Exit;
    }

    // Through the relay, as winget's downloader does: CONNECT, then the request inside the tunnel. Pace sleeps between reads.
    // False when the relay refused it or it ended short.
    private static bool Download(Uri url, Uri proxy, int pace)
    {
        try
        {
            using var client = new TcpClient(proxy.Host, proxy.Port);
            var stream = client.GetStream();
            var target = $"{url.Host}:{url.Port}";
            stream.Write(Encoding.ASCII.GetBytes($"CONNECT {target} HTTP/1.1\r\nHost: {target}\r\n\r\n"));
            if (!HeadOf(stream).StartsWith("HTTP/1.1 200", StringComparison.Ordinal)) return false;
            stream.Write(Encoding.ASCII.GetBytes($"GET {url.PathAndQuery} HTTP/1.1\r\nHost: {target}\r\nConnection: close\r\n\r\n"));
            var length = LengthOf(HeadOf(stream));
            var buffer = new byte[16 * 1024];
            var received = 0L;
            int read;
            while ((read = stream.Read(buffer)) > 0)
            {
                received += read;
                if (pace > 0) Thread.Sleep(pace);
            }
            return received == length;
        }
        catch (Exception e) when (e is IOException or SocketException)
        {
            return false;
        }
    }

    private static string HeadOf(Stream stream)
    {
        var head = new StringBuilder();
        var one = new byte[1];
        while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) && stream.Read(one) == 1) head.Append((char)one[0]);
        return head.ToString();
    }

    private static long LengthOf(string head) =>
        head.Split("\r\n").FirstOrDefault(h => h.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) is { } header ? long.Parse(header[15..].Trim()) : -1;

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private sealed record Scenario
    {
        public List<Step> Steps { get; init; } = [];
        public int Exit { get; init; }
    }

    private sealed record Step
    {
        public string? Say { get; init; }
        public int Sleep { get; init; }
        public string? Download { get; init; }
        public bool Stubborn { get; init; }
        public string? Fetch { get; init; }
        public int Pace { get; init; }
        public int Installer { get; init; }
        public string? Touch { get; init; }
    }
}
