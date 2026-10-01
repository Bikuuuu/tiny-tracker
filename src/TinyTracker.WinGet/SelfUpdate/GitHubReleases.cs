using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using TinyTracker.Core;
using TinyTracker.Core.SelfUpdate;

namespace TinyTracker.WinGet.SelfUpdate;

public enum GitHubProblem
{
    // No answer: no network, a timeout, a server error or GitHub's rate limit.
    Unreachable,
    // An answer the self-update doesn't take.
    Refused,
}

public sealed class GitHubException(GitHubProblem problem, string message, Exception? inner = null) : Exception(message, inner)
{
    public GitHubProblem Problem { get; } = problem;
}

// One release's Setup, and its size and SHA-256 as GitHub records them.
public sealed record SelfAsset(Uri Download, long Size, byte[] Sha256);

// Tiny Tracker's releases, from GitHub's public API (spec §6.5).
public sealed partial class GitHubReleases(HttpClient http, TimeProvider time) : ISelfReleases
{
    public static readonly Uri Api = new("https://api.github.com/repos/Bikuuuu/tiny-tracker/releases/");
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    // Setup is about 50 MB.
    public const long MaxSize = 200L * 1024 * 1024;
    private const int MaxAnswer = 1024 * 1024;
    private static readonly ProductInfoHeaderValue Agent = new("TinyTracker", typeof(GitHubReleases).Assembly.GetName().Version?.ToString(3));

    public static string SetupName(SelfVersion version) => $"TinyTracker-Setup-{version}-x64.exe";

    public static Uri DownloadOf(SelfVersion version) => new($"{AppInfo.RepositoryUrl}/releases/download/{version.Tag}/{SetupName(version)}");

    // The latest release, which GitHub picks among those that are neither drafts nor pre-releases; null before the first.
    public async Task<SelfRelease?> LatestAsync(CancellationToken ct)
    {
        using var answer = await GetAsync(new Uri(Api, "latest"), ct);
        if (answer is null) return null;
        var release = answer.RootElement;
        if (SelfVersion.OfTag(Text(release, "tag_name")) is not { } version) throw Refused("a tag that isn't a plain version");
        Published(release);
        if (!release.TryGetProperty("published_at", out var published) || !published.TryGetDateTimeOffset(out var at)) throw Refused("no publish date");
        return new SelfRelease(version, at);
    }

    // That version's Setup, from its published, immutable release.
    public async Task<SelfAsset> AssetAsync(SelfVersion version, CancellationToken ct)
    {
        using var answer = await GetAsync(new Uri(Api, "tags/" + version.Tag), ct) ?? throw Refused("no release " + version.Tag);
        var release = answer.RootElement;
        if (Text(release, "tag_name") != version.Tag) throw Refused("another tag");
        Published(release);
        if (!Flag(release, "immutable")) throw Refused("not immutable");
        var name = SetupName(version);
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) throw Refused("no assets");
        var setups = assets.EnumerateArray().Where(a => Text(a, "name") == name).ToList();
        if (setups.Count != 1) throw Refused("no single " + name);
        var setup = setups[0];
        if (Text(setup, "state") != "uploaded") throw Refused("not uploaded");
        if (Digest().Match(Text(setup, "digest") ?? "") is not { Success: true } digest) throw Refused("no SHA-256 digest");
        if (!setup.TryGetProperty("size", out var size) || !size.TryGetInt64(out var bytes) || bytes is <= 0 or >= MaxSize) throw Refused("a size out of range");
        var download = DownloadOf(version);
        if (Text(setup, "browser_download_url") != download.AbsoluteUri) throw Refused("a download elsewhere");
        return new SelfAsset(download, bytes, Convert.FromHexString(digest.Groups[1].Value));
    }

    private static void Published(JsonElement release)
    {
        if (Flag(release, "draft") || Flag(release, "prerelease")) throw Refused("a draft or pre-release");
    }

    // Null for a 404. The answer must be a JSON object of at most 1 MB.
    private async Task<JsonDocument?> GetAsync(Uri uri, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(RequestTimeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            if (http.DefaultRequestHeaders.UserAgent.Count == 0) request.Headers.UserAgent.Add(Agent);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (response.StatusCode != HttpStatusCode.OK) throw new GitHubException(GitHubProblem.Unreachable, $"GitHub answered {(int)response.StatusCode}");
            var body = await ReadAsync(response.Content, linked.Token);
            var json = JsonDocument.Parse(body);
            if (json.RootElement.ValueKind == JsonValueKind.Object) return json;
            json.Dispose();
            throw Refused("an answer that isn't an object");
        }
        catch (OperationCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new GitHubException(GitHubProblem.Unreachable, "no answer in time", e);
        }
        catch (HttpRequestException e)
        {
            throw new GitHubException(GitHubProblem.Unreachable, e.Message, e);
        }
        catch (JsonException e)
        {
            throw new GitHubException(GitHubProblem.Refused, "malformed JSON", e);
        }
    }

    private static async Task<byte[]> ReadAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var body = new MemoryStream();
        var buffer = new byte[16 * 1024];
        for (int read; (read = await stream.ReadAsync(buffer, ct)) > 0;)
        {
            if (body.Length + read > MaxAnswer) throw Refused("an answer over 1 MB");
            body.Write(buffer, 0, read);
        }
        return body.ToArray();
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Flag(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static GitHubException Refused(string why) => new(GitHubProblem.Refused, why);

    [GeneratedRegex(@"\Asha256:([0-9a-fA-F]{64})\z")]
    private static partial Regex Digest();
}
