using TinyTracker.Core.Tracking;
using Xunit;

namespace TinyTracker.Core.Tests.Tracking;

public class LocalAppTests
{
    [Fact]
    public void MachineEntry_IsInHklm() =>
        Assert.Equal(new UninstallEntry(false, false, "{EXAMPLE-1234}"), LocalApp.Parse(@"ARP\Machine\X64\{EXAMPLE-1234}"));

    [Fact]
    public void X86MachineEntry_IsInThe32BitView() =>
        Assert.Equal(new UninstallEntry(false, true, "Example Editor"), LocalApp.Parse(@"ARP\Machine\X86\Example Editor"));

    [Fact]
    public void UserEntry_IsInHkcu() =>
        Assert.Equal(new UninstallEntry(true, false, "Example Chat"), LocalApp.Parse(@"ARP\User\X64\Example Chat"));

    [Fact]
    public void KeyPath_IsUnderUninstall() =>
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Example Editor", new UninstallEntry(false, false, "Example Editor").KeyPath);

    [Fact]
    public void MsixPackage_IsFoundByItsFullName() =>
        Assert.Equal(new MsixPackage("Example.App_1.0.0.0_x64__abcdefgh"), LocalApp.Parse(@"MSIX\Example.App_1.0.0.0_x64__abcdefgh"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"ARP\Machine\X64\")]
    [InlineData(@"ARP\Somewhere\X64\Example")]
    [InlineData(@"MSIX\")]
    [InlineData(@"Example.Editor")]
    public void Unknown_HasNoPlace(string? localId) => Assert.Null(LocalApp.Parse(localId));

    [Theory]
    [InlineData(@"C:\Program Files\Example\app.exe,0", @"C:\Program Files\Example\app.exe", 0)]
    [InlineData(@"C:\Program Files\Example\app.exe,-101", @"C:\Program Files\Example\app.exe", -101)]
    [InlineData(@"""C:\Program Files\Example\app.ico""", @"C:\Program Files\Example\app.ico", 0)]
    [InlineData(@"""C:\Program Files\Example\app.exe"",2", @"C:\Program Files\Example\app.exe", 2)]
    [InlineData(@"C:\Example, Inc\app.exe", @"C:\Example, Inc\app.exe", 0)]
    [InlineData(@"  %SystemRoot%\system32\app.dll , 3 ", @"%SystemRoot%\system32\app.dll", 3)]
    public void DisplayIcon_IsSplitIntoPathAndIndex(string value, string path, int index) => Assert.Equal((path, index), LocalApp.DisplayIcon(value));

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    [InlineData(@"""unclosed")]
    public void EmptyDisplayIcon_HasNoPath(string? value) => Assert.Null(LocalApp.DisplayIcon(value));
}
