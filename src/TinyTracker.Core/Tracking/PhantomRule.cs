using TinyTracker.Core.Versions;

namespace TinyTracker.Core.Tracking;

public static class PhantomRule
{
    // A reported success that changed nothing while winget still offers the same version.
    public static bool IsPhantom(string installedBefore, string installedAfter, string attempted, string? availableAfter) =>
        PackageVersion.Same(installedBefore, installedAfter)
        && !PackageVersion.Parse(availableAfter).IsUnknown
        && PackageVersion.Same(attempted, availableAfter);

    public static TrackedApp Flag(TrackedApp app, string version) =>
        app.Offer is { } offer && PackageVersion.Same(offer.Version, version)
            ? app with { Offer = offer with { Phantom = true } }
            : app;
}
