namespace TinyTracker.Core.Tracking;

public enum AppStatus
{
    UpToDate,
    Available,
    Skipped,
    Phantom,
    VersionUnknown,
    // Not installed anymore.
    NotFound,
    // The catalog no longer has the package.
    NotInCatalog,
}
