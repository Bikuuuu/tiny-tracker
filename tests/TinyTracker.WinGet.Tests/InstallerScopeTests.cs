using Microsoft.Management.Deployment;
using TinyTracker.Core.Tracking;
using Xunit;

namespace TinyTracker.WinGet.Tests;

// Most manifests don't state a scope, so a strict User or System filter would find no installer.
public class InstallerScopeTests
{
    [Theory]
    [InlineData(InstallScope.User, PackageInstallScope.UserOrUnknown)]
    [InlineData(InstallScope.Machine, PackageInstallScope.SystemOrUnknown)]
    [InlineData(InstallScope.Unknown, PackageInstallScope.Any)]
    public void InstalledScope_AlsoAcceptsInstallersWithoutAScope(InstallScope installed, PackageInstallScope filter) =>
        Assert.Equal(filter, WinGetSession.InstallerScopeFor(installed));
}
