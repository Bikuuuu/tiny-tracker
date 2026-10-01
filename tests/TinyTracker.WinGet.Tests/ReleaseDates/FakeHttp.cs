using System.Net;

namespace TinyTracker.WinGet.Tests.ReleaseDates;

// Answers requests with a function and records their URLs.
internal sealed class FakeHttp(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
{
    private readonly List<Uri> _requests = [];

    public IReadOnlyList<Uri> Requests
    {
        get { lock (_requests) return [.. _requests]; }
    }

    public static Task<HttpResponseMessage> Text(string body) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });

    public static Task<HttpResponseMessage> Status(HttpStatusCode status) => Task.FromResult(new HttpResponseMessage(status));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (_requests) _requests.Add(request.RequestUri!);
        return answer(request, cancellationToken);
    }
}
