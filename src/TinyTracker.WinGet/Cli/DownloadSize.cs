using System.Net.Http.Headers;

namespace TinyTracker.WinGet.Cli;

// How big an installer is, from a HEAD request on its address (spec §6.4). 0 when that can't tell, as when the server answers
// with a web page.
public static class DownloadSize
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // One for the process; servers are told who asks.
    public static HttpClient Shared { get; } = new() { DefaultRequestHeaders = { UserAgent = { new ProductInfoHeaderValue("TinyTracker", typeof(DownloadSize).Assembly.GetName().Version?.ToString(3)) } } };

    public static async Task<ulong> OfAsync(HttpClient http, Uri url, CancellationToken ct)
    {
        if (url.Scheme is not ("https" or "http")) return 0;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType is "text/html") return 0;
            return response.Content.Headers.ContentLength is > 0 and var length ? (ulong)length : 0;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or InvalidOperationException)
        {
            return 0;
        }
    }
}
