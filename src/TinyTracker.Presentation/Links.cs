using TinyTracker.Core;

namespace TinyTracker.Presentation;

// The app opens web links and the App Installer Store page, nothing else.
public static class Links
{
    public static string? Openable(string? url) => url == Notice.AppInstallerStoreLink ? url : WebLink.Clean(url);
}
