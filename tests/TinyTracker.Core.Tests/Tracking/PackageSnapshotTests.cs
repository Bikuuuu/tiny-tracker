using TinyTracker.Core.Tracking;
using Xunit;

namespace TinyTracker.Core.Tests.Tracking;

public sealed class PackageSnapshotTests
{
    [Theory]
    [InlineData(InstallScope.User, InstallerElevation.Unknown, false)]
    [InlineData(InstallScope.User, InstallerElevation.Prohibited, false)]
    [InlineData(InstallScope.Machine, InstallerElevation.Prohibited, false)]
    [InlineData(InstallScope.Unknown, InstallerElevation.Prohibited, false)]
    [InlineData(InstallScope.Machine, InstallerElevation.Unknown, true)]
    [InlineData(InstallScope.User, InstallerElevation.Required, true)]
    [InlineData(InstallScope.User, InstallerElevation.ElevatesSelf, true)]
    [InlineData(InstallScope.Unknown, InstallerElevation.Unknown, true)]
    public void AsksForAdmin_FollowsTheScopeAndTheInstaller(InstallScope scope, InstallerElevation elevation, bool asks) =>
        Assert.Equal(asks, new PackageSnapshot("Example.Editor", "winget", "Example Editor", "2.4.1", "2.5.0", Scope: scope, Elevation: elevation).AsksForAdmin);

    [Theory]
    [InlineData("- Fixed a security issue in the updater.", true)]
    [InlineData("- Faster start.", false)]
    [InlineData(null, false)]
    public void IsSecurityFix_ReadsTheNotes(string? notes, bool fix) =>
        Assert.Equal(fix, new PackageSnapshot("Example.Editor", "winget", "Example Editor", "2.4.1", "2.5.0", ReleaseNotes: notes).IsSecurityFix);

    // Read once per snapshot: a copy with other notes reads them again.
    [Fact]
    public void CopyWithOtherNotes_ReadsThemAgain()
    {
        var fix = new PackageSnapshot("Example.Editor", "winget", "Example Editor", "2.4.1", "2.5.0", ReleaseNotes: "- Fixed a security issue in the updater.");
        Assert.False((fix with { ReleaseNotes = "- Faster start." }).IsSecurityFix);
        Assert.True((fix with { ReleaseNotes = null } with { ReleaseNotes = "- Patched a security hole." }).IsSecurityFix);
        Assert.Equal(fix, fix with { });
        Assert.True((fix with { Name = "Example Editor 2" }).IsSecurityFix);
    }

    // The answer comes from the notes alone, so snapshots made either way stay equal.
    [Fact]
    public void SnapshotsWithTheSameNotes_AreEqual()
    {
        const string notes = "- Fixed a security issue in the updater.";
        var made = new PackageSnapshot("Example.Editor", "winget", "Example Editor", "2.4.1", "2.5.0", ReleaseNotes: notes);
        var copied = new PackageSnapshot("Example.Editor", "winget", "Example Editor", "2.4.1", "2.5.0") with { ReleaseNotes = notes };
        Assert.Equal(made, copied);
        Assert.Equal(made.GetHashCode(), copied.GetHashCode());
    }
}
