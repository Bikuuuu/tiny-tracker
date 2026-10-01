using System.Collections.Concurrent;
using System.Net;
using TinyTracker.Core.Checking;

namespace TinyTracker.WinGet.ReleaseDates;

// Release dates from the public winget-pkgs manifests on GitHub. Each version is asked once per session.
public sealed class GitHubReleaseDates(HttpClient http, TimeProvider time) : IReleaseDates
{
    public static readonly Uri Manifests = new("https://raw.githubusercontent.com/microsoft/winget-pkgs/master/manifests/");
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    // After a failure, stop asking for a while instead of timing out on every app.
    public static readonly TimeSpan Backoff = TimeSpan.FromMinutes(10);
    private const int MaxChars = 64 * 1024;

    private readonly ConcurrentDictionary<string, DateOnly?> _known = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<DateOnly?>> _asking = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private DateTimeOffset _quietUntil = DateTimeOffset.MinValue;

    // Callers asking for the same manifest at once share one request.
    public async Task<DateOnly?> GetAsync(string id, string version, CancellationToken ct)
    {
        if (Manifest.InstallerPath(id, version) is not { } path) return null;
        ct.ThrowIfCancellationRequested();
        if (_known.TryGetValue(path, out var known)) return known;
        Task<DateOnly?> asking;
        lock (_gate)
        {
            if (_known.TryGetValue(path, out known)) return known;
            if (time.GetUtcNow() < _quietUntil) return null;
            if (!_asking.TryGetValue(path, out asking!))
            {
                asking = Task.Run(() => AskAsync(path), CancellationToken.None);
                _asking[path] = asking;
            }
        }
        return await asking.WaitAsync(ct);
    }

    // Has its own timeout, so a caller that gives up doesn't fail the others.
    private async Task<DateOnly?> AskAsync(string path)
    {
        try
        {
            using var timeout = new CancellationTokenSource(RequestTimeout, time);
            using var response = await http.GetAsync(new Uri(Manifests, path), HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotFound) return Remember(path, null);
            if (!response.IsSuccessStatusCode)
            {
                Pause();
                return null;
            }
            return Remember(path, Manifest.ReleaseDate(await ReadStartAsync(response.Content, timeout.Token)));
        }
        // Nobody may be waiting for this task, so nothing escapes it: a failure means no date for now.
        catch (Exception)
        {
            Pause();
            return null;
        }
        finally
        {
            lock (_gate) _asking.Remove(path);
        }
    }

    private DateOnly? Remember(string path, DateOnly? date)
    {
        _known[path] = date;
        return date;
    }

    private void Pause()
    {
        lock (_gate) _quietUntil = time.GetUtcNow() + Backoff;
    }

    private static async Task<string> ReadStartAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        var buffer = new char[MaxChars];
        var read = await reader.ReadBlockAsync(buffer, ct);
        return new string(buffer, 0, read);
    }
}
