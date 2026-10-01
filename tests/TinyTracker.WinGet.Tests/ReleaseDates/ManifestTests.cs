using TinyTracker.WinGet.ReleaseDates;
using Xunit;

namespace TinyTracker.WinGet.Tests.ReleaseDates;

public class ManifestTests
{
    [Theory]
    [InlineData("Mozilla.Firefox", "131.0", "m/Mozilla/Firefox/131.0/Mozilla.Firefox.installer.yaml")]
    [InlineData("Notepad++.Notepad++", "8.9.8.1", "n/Notepad%2B%2B/Notepad%2B%2B/8.9.8.1/Notepad%2B%2B.Notepad%2B%2B.installer.yaml")]
    [InlineData("Microsoft.VisualStudio.2022.Community", "17.14.3", "m/Microsoft/VisualStudio/2022/Community/17.14.3/Microsoft.VisualStudio.2022.Community.installer.yaml")]
    [InlineData("7zip.7zip", "25.01", "7/7zip/7zip/25.01/7zip.7zip.installer.yaml")]
    [InlineData("Example.Tool", "1.0 beta", "e/Example/Tool/1.0%20beta/Example.Tool.installer.yaml")]
    [InlineData("#Example.Tool", "1.0", "%23/%23Example/Tool/1.0/%23Example.Tool.installer.yaml")]
    [InlineData("%Example.Tool", "1.0", "%25/%25Example/Tool/1.0/%25Example.Tool.installer.yaml")]
    public void InstallerPath_HasAFolderPerIdPart(string id, string version, string path) =>
        Assert.Equal(path, Manifest.InstallerPath(id, version));

    [Theory]
    [InlineData("Mozilla", "1.0")]
    [InlineData("Mozilla.Fire fox", "1.0")]
    [InlineData("Mozilla.Firefox/x", "1.0")]
    [InlineData("Mozilla..Firefox", "1.0")]
    [InlineData("Mozilla.Firefox", "..")]
    [InlineData("Mozilla.Firefox", "1/2")]
    [InlineData("Mozilla.Firefox", "")]
    [InlineData("", "1.0")]
    public void BadIdOrVersion_HasNoPath(string id, string version) => Assert.Null(Manifest.InstallerPath(id, version));

    [Theory]
    [InlineData("ReleaseDate: 2026-09-20\n")]
    [InlineData("ReleaseDate: '2026-09-20'\n")]
    [InlineData("ReleaseDate: \"2026-09-20\"\n")]
    [InlineData("Installers:\n  - Architecture: x64\n    ReleaseDate: 2026-09-20 # per installer\n")]
    [InlineData("Installers:\n- ReleaseDate: 2026-09-20\n")]
    [InlineData("PackageVersion: 1.0\r\nReleaseDate: 2026-09-20\r\n")]
    public void ReleaseDate_IsRead(string yaml) => Assert.Equal(new DateOnly(2026, 9, 20), Manifest.ReleaseDate(yaml));

    [Fact]
    public void ReleaseDate_FirstOneWins() =>
        Assert.Equal(new DateOnly(2026, 9, 20), Manifest.ReleaseDate("ReleaseDate: 2026-09-20\nInstallers:\n- ReleaseDate: 2026-09-01\n"));

    [Theory]
    [InlineData("ReleaseDate: 2026-13-45\n")]
    [InlineData("ReleaseDate: yesterday\n")]
    [InlineData("# ReleaseDate: 2026-09-20\n")]
    [InlineData("MyReleaseDate: 2026-09-20\n")]
    [InlineData("PackageVersion: 1.0\n")]
    public void MissingOrBadReleaseDate_IsNull(string yaml) => Assert.Null(Manifest.ReleaseDate(yaml));
}
