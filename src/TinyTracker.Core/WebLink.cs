namespace TinyTracker.Core;

public static class WebLink
{
    // Only http and https links open in the browser; anything else counts as none.
    public static string? Clean(string? url) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri.AbsoluteUri
            : null;
}
