using TinyTracker.Core.Installing;
using TinyTracker.Core.Tracking;
using Xunit;

namespace TinyTracker.Core.Tests.Installing;

public sealed class RoutesTests
{
    private static PackageSnapshot Package(InstallScope scope, InstallerElevation elevation) =>
        new("Example.Editor", "winget", "Example Editor", "2.4.1", "2.5.0", Scope: scope, Elevation: elevation);

    [Theory]
    [InlineData(InstallScope.Machine, InstallerElevation.Prohibited, InstallRoute.App, InstallRoute.App)]
    [InlineData(InstallScope.User, InstallerElevation.Prohibited, InstallRoute.App, InstallRoute.App)]
    [InlineData(InstallScope.User, InstallerElevation.Required, InstallRoute.Helper, InstallRoute.Helper)]
    [InlineData(InstallScope.Machine, InstallerElevation.Unknown, InstallRoute.Helper, InstallRoute.Helper)]
    [InlineData(InstallScope.Machine, InstallerElevation.ElevatesSelf, InstallRoute.Helper, InstallRoute.Helper)]
    [InlineData(InstallScope.User, InstallerElevation.ElevatesSelf, InstallRoute.App, InstallRoute.Helper)]
    [InlineData(InstallScope.Unknown, InstallerElevation.ElevatesSelf, InstallRoute.App, InstallRoute.Helper)]
    [InlineData(InstallScope.User, InstallerElevation.Unknown, InstallRoute.App, InstallRoute.App)]
    [InlineData(InstallScope.Unknown, InstallerElevation.Unknown, InstallRoute.App, InstallRoute.Helper)]
    public void Route_FollowsTheSpecTable(InstallScope scope, InstallerElevation elevation, InstallRoute byDefault, InstallRoute silent) =>
        Assert.Equal((byDefault, silent), (Routes.For(Package(scope, elevation), silentMode: false), Routes.For(Package(scope, elevation), silentMode: true)));

    // Silent mode promises no prompt, so whatever may ask for admin goes through the helper.
    [Fact]
    public void InSilentMode_TheHelperRunsExactlyWhatAsksForAdmin()
    {
        foreach (var scope in Enum.GetValues<InstallScope>())
        {
            foreach (var elevation in Enum.GetValues<InstallerElevation>())
            {
                var package = Package(scope, elevation);
                Assert.Equal(package.AsksForAdmin, Routes.For(package, silentMode: true) == InstallRoute.Helper);
            }
        }
    }

    [Fact]
    public void Request_RunsInTheAppUnlessToldOtherwise() =>
        Assert.Equal(InstallRoute.App, new InstallRequest(new PackageKey("Example.Editor", "winget"), "Example Editor", "2.4.1", "2.5.0").Route);
}
