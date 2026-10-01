using TinyTracker.Core.Tracking;

namespace TinyTracker.Core.Installing;

// Who runs an update: the app itself, unelevated, or the admin helper.
public enum InstallRoute
{
    App,
    Helper,
}

public static class Routes
{
    // Spec §6.3. The first case that fits decides.
    public static InstallRoute For(PackageSnapshot package, bool silentMode) => package switch
    {
        { Elevation: InstallerElevation.Prohibited } => InstallRoute.App,
        { Elevation: InstallerElevation.Required } or { Scope: InstallScope.Machine } => InstallRoute.Helper,
        { Elevation: InstallerElevation.ElevatesSelf } => silentMode ? InstallRoute.Helper : InstallRoute.App,
        { Scope: InstallScope.User } => InstallRoute.App,
        _ => silentMode ? InstallRoute.Helper : InstallRoute.App,
    };
}
